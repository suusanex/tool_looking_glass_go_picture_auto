from __future__ import annotations

import getpass
import hashlib
import io
import json
import logging
import math
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from multiprocessing.connection import Client, Listener

from filelock import FileLock, Timeout
from PIL import Image

from .bridge import RestBridge
from .imaging import DepthEstimator, cache_key, convert_to_rgbd
from .playlist import read_playlist_entries
from .settings import (
    APP_NAME, MAX_IMAGE_EDGE, cache_directory, data_directory, log_path, state_path,
)


def pipe_address() -> str:
    identity = getpass.getuser().encode("utf-8", errors="replace")
    return rf"\\.\pipe\lookgo-{hashlib.sha256(identity).hexdigest()[:20]}"


def _write_json_atomic(path: Path, value: dict) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, path)


def _read_state() -> dict:
    try:
        return json.loads(state_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {"status": "stopped", "images": [], "index": None}


def _send_response(connection, value: dict) -> None:
    connection.send_bytes(json.dumps(value, ensure_ascii=False).encode("utf-8"))


@dataclass
class Request:
    payload: dict
    connection: object


def _serve_client(connection, incoming: queue.Queue) -> None:
    try:
        payload = json.loads(connection.recv_bytes().decode("utf-8"))
        incoming.put(Request(payload, connection))
    except Exception:
        connection.close()


class DisplayWorker:
    def __init__(self) -> None:
        data_directory()
        logging.basicConfig(
            filename=log_path(), level=logging.INFO,
            format="%(asctime)s %(levelname)s %(message)s", encoding="utf-8",
        )
        self.incoming: queue.Queue[Request] = queue.Queue()
        self.prepared: queue.Queue[tuple[int, list[Path] | None, Exception | None]] = queue.Queue()
        self.pending: queue.Queue[tuple[int, object]] = queue.Queue()
        self.cancel_prepare = threading.Event()
        self.listener = None
        self.estimator: DepthEstimator | None = None
        self.renderer: RestBridge | None = None
        self.aspect = 1.0
        self.max_texture_size = 4092
        self.active: list[Path] = []
        self.active_sources: list[Path] = []
        self.pending_sources: dict[int, list[Path]] = {}
        self.pending_intervals: dict[int, float] = {}
        self.pending_operations: dict[int, str] = {}
        self.cancelled_generations: set[int] = set()
        self.index = 0
        self.interval = 10.0
        self.last_change = time.monotonic()
        self.generation = 0
        self.busy = False
        self.stopping = False
        self.last_error = None

    def _save_state(self, status: str) -> None:
        _write_json_atomic(state_path(), {
            "status": status,
            "images": [str(path) for path in self.active_sources],
            "index": self.index if self.active else None,
            "busy": self.busy,
            "last_error": self.last_error,
        })

    def _initialize_display(self) -> None:
        self.renderer = RestBridge()
        device = self.renderer.connect()
        self.aspect = device.aspect

    def _start_listener(self) -> None:
        self.listener = Listener(pipe_address(), family="AF_PIPE", authkey=b"lookgo-local-v1")

        def accept_loop() -> None:
            while not self.stopping:
                try:
                    connection = self.listener.accept()
                    threading.Thread(
                        target=_serve_client, args=(connection, self.incoming), daemon=True
                    ).start()
                except (OSError, EOFError):
                    if not self.stopping:
                        logging.exception("IPC listener stopped unexpectedly")
                    return

        threading.Thread(target=accept_loop, name="lookgo-ipc", daemon=True).start()

    def _handle_request(self, request: Request) -> None:
        payload = request.payload
        command = payload.get("command")
        try:
            if command == "status":
                state = _read_state()
                state["busy"] = self.busy
                _send_response(request.connection, {"ok": True, "state": state})
                request.connection.close()
                return
            if command == "stop":
                self.cancel_prepare.set()
                self.stopping = True
                for generation in list(self.pending_operations):
                    self.cancelled_generations.add(generation)
                    self._reply_pending(generation, {"ok": False, "error": "Playlist update was cancelled by stop"})
                _send_response(request.connection, {"ok": True, "state": {"status": "stopping"}})
                request.connection.close()
                return
            if command == "cancel":
                operation_id = str(payload.get("operation_id", ""))
                generation = next(
                    (item for item, token in self.pending_operations.items() if token == operation_id), None
                )
                if generation is not None:
                    self.cancel_prepare.set()
                    self.cancelled_generations.add(generation)
                _send_response(request.connection, {"ok": True})
                request.connection.close()
                return
            if command != "show":
                raise ValueError(f"Unknown command: {command}")
            if self.busy:
                raise RuntimeError("A playlist update is already in progress")
            entries = read_playlist_entries(Path(payload["list_path"]))
            interval = float(payload.get("interval", 10))
            if not math.isfinite(interval) or interval <= 0:
                raise ValueError("Interval must be greater than zero")
            self.pending_intervals[self.generation + 1] = interval
            self.busy = True
            self.cancel_prepare = threading.Event()
            self.generation += 1
            generation = self.generation
            self.pending_sources[generation] = [entry.path for entry in entries]
            self.pending_operations[generation] = str(payload.get("operation_id", ""))
            self.last_error = None
            self._save_state("updating")
            self.pending.put((generation, request.connection))

            def prepare() -> None:
                try:
                    staged: list[Path] = []
                    root = cache_directory()
                    estimator = self.estimator
                    if estimator is None:
                        estimator = DepthEstimator()
                        self.estimator = estimator
                    for entry in entries:
                        if self.cancel_prepare.is_set():
                            raise RuntimeError("Playlist update was cancelled")
                        image_path = entry.path
                        try:
                            image_bytes = image_path.read_bytes()
                            max_edge = min(MAX_IMAGE_EDGE, self.max_texture_size // 2)
                            key = cache_key(image_bytes, self.aspect, max_edge)
                            # 変換とキャッシュキーに同一の入力バイト列を使い、途中のファイル更新を混入させない。
                            with Image.open(io.BytesIO(image_bytes)) as image:
                                image.verify()
                        except Exception as exc:
                            raise ValueError(f"Playlist line {entry.line}: invalid image '{image_path}': {exc}") from exc
                        cached = root / f"{key}.png"
                        if not cached.is_file():
                            try:
                                rgbd = convert_to_rgbd(
                                    image_bytes, self.aspect, estimator,
                                    max_edge,
                                )
                            except Exception as exc:
                                raise ValueError(f"Playlist line {entry.line}: conversion failed for '{image_path}': {exc}") from exc
                            temporary = cached.with_suffix(".tmp.png")
                            temporary.write_bytes(rgbd)
                            os.replace(temporary, cached)
                        with Image.open(cached) as image:
                            image.verify()
                        staged.append(cached)
                    self.prepared.put((generation, staged, None))
                except Exception as exc:
                    self.prepared.put((generation, None, exc))

            threading.Thread(target=prepare, name=f"lookgo-convert-{generation}", daemon=True).start()
        except Exception as exc:
            _send_response(request.connection, {"ok": False, "error": str(exc)})
            request.connection.close()

    def _pump(self) -> None:
        while True:
            try:
                request = self.incoming.get_nowait()
            except queue.Empty:
                break
            self._handle_request(request)

        while True:
            try:
                generation, staged, error = self.prepared.get_nowait()
            except queue.Empty:
                break
            if generation != self.generation:
                if generation in self.cancelled_generations:
                    self.busy = False
                    self.cancelled_generations.discard(generation)
                    self.pending_sources.pop(generation, None)
                    self.pending_intervals.pop(generation, None)
                    self.pending_operations.pop(generation, None)
                    self._save_state("playing" if self.active else "error")
                    self._reply_pending(generation, {"ok": False, "error": "Playlist update was cancelled"})
                continue
            self.busy = False
            if self.stopping or generation in self.cancelled_generations:
                self.cancelled_generations.discard(generation)
                self.pending_sources.pop(generation, None)
                self.pending_intervals.pop(generation, None)
                self.pending_operations.pop(generation, None)
                self._save_state("playing" if self.active else "stopped")
                self._reply_pending(generation, {"ok": False, "error": "Playlist update was cancelled"})
                continue
            if not self._pending_client_connected(generation):
                self.pending_sources.pop(generation, None)
                self.pending_intervals.pop(generation, None)
                self.pending_operations.pop(generation, None)
                self._save_state("playing" if self.active else "stopped")
                logging.info("Show client disconnected before the playlist could be committed")
                continue
            if error:
                self.last_error = str(error)
                logging.error("Playlist conversion failed: %s", error, exc_info=(type(error), error, error.__traceback__))
                self._save_state("playing" if self.active else "error")
                self._reply_pending(generation, {"ok": False, "error": str(error)})
                self.pending_sources.pop(generation, None)
                self.pending_intervals.pop(generation, None)
                self.pending_operations.pop(generation, None)
                if not self.active:
                    self.stopping = True
                continue
            if self.stopping:
                continue
            assert staged
            try:
                assert self.renderer is not None
                interval = self.pending_intervals[generation]
                self.renderer.present(staged[0], duration_ms=round(interval * 1000))
            except Exception as exc:
                self.last_error = str(exc)
                logging.exception("First frame failed")
                self._save_state("error")
                self._reply_pending(generation, {"ok": False, "error": str(exc)})
                raise
            self.active = staged
            self.active_sources = self.pending_sources.pop(generation, [])
            self.interval = self.pending_intervals.pop(generation, 10.0)
            self.pending_operations.pop(generation, None)
            self.index = 0
            self.last_change = time.monotonic()
            self.last_error = None
            self._save_state("playing")
            self._reply_pending(generation, {"ok": True, "state": _read_state()})

        if self.stopping:
            return
        # 1枚のリストは Bridge 側の loop で固定する。ここでの再送は間隔ごとに表示を作り直す。
        if len(self.active) > 1 and time.monotonic() - self.last_change >= self.interval:
            next_index = (self.index + 1) % len(self.active)
            try:
                assert self.renderer is not None
                self.renderer.present(self.active[next_index], duration_ms=round(self.interval * 1000))
            except Exception:
                logging.exception("Image playback failed")
                raise
            self.index = next_index
            self.last_change = time.monotonic()
            self._save_state("playing")

    def _reply_pending(self, generation: int, response: dict) -> None:
        retained = []
        while True:
            try:
                item_generation, connection = self.pending.get_nowait()
            except queue.Empty:
                break
            if item_generation == generation:
                try:
                    _send_response(connection, response)
                except (OSError, EOFError):
                    logging.info("Client disconnected before receiving the show result")
                finally:
                    connection.close()
            else:
                retained.append((item_generation, connection))
        for item in retained:
            self.pending.put(item)

    def _pending_client_connected(self, generation: int) -> bool:
        retained = []
        connected = False
        while True:
            try:
                item_generation, connection = self.pending.get_nowait()
            except queue.Empty:
                break
            if item_generation == generation:
                try:
                    if connection.poll(0):
                        connection.recv_bytes()
                    connected = True
                except (OSError, EOFError):
                    connection.close()
                    connected = False
            retained.append((item_generation, connection))
        for item in retained:
            self.pending.put(item)
        return connected

    def run(self) -> int:
        lock = FileLock(str(data_directory() / "worker.lock"))
        try:
            lock.acquire(timeout=0)
        except Timeout:
            return 0
        exit_code = 0
        try:
            self._initialize_display()
            self._start_listener()
            self._save_state("idle")
            logging.info("Worker ready for %s", APP_NAME)
            last_health_check = time.monotonic()
            while not self.stopping:
                self._pump()
                now = time.monotonic()
                if now - last_health_check >= 2.0:
                    assert self.renderer is not None
                    self.renderer.health_check()
                    last_health_check = now
                time.sleep(0.05)
        except Exception as exc:
            self.last_error = str(exc)
            logging.exception("Worker stopped after an error")
            exit_code = 1
        finally:
            self.stopping = True
            if self.listener:
                try:
                    self.listener.close()
                except Exception as exc:
                    logging.exception("IPC listener cleanup failed")
                    self.last_error = self.last_error or str(exc)
                    exit_code = 1
            if self.renderer:
                try:
                    self.renderer.close()
                except Exception as exc:
                    logging.exception("Bridge renderer cleanup failed")
                    self.last_error = self.last_error or str(exc)
                    exit_code = 1
            if self.last_error:
                exit_code = 1
            self._save_state("error" if exit_code else "stopped")
            lock.release()
        return exit_code


def _read_pipe_response(connection, timeout: float) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if connection.poll(0.1):
            return json.loads(connection.recv_bytes().decode("utf-8"))
    raise TimeoutError("Timed out waiting for the display worker")


def send_command(payload: dict, timeout: float = 1800) -> dict:
    connection = Client(pipe_address(), family="AF_PIPE", authkey=b"lookgo-local-v1")
    try:
        connection.send_bytes(json.dumps(payload, ensure_ascii=False).encode("utf-8"))
        return _read_pipe_response(connection, timeout)
    finally:
        connection.close()


def start_worker() -> None:
    from .settings import log_path

    creation_flags = (
        subprocess.DETACHED_PROCESS
        | subprocess.CREATE_NEW_PROCESS_GROUP
        | subprocess.CREATE_NO_WINDOW
    )
    output = log_path().open("ab")
    try:
        subprocess.Popen(
            [sys.executable, str(Path(__file__).resolve().parent.parent / "lookgo.py"), "_worker"],
            stdin=subprocess.DEVNULL,
            stdout=output,
            stderr=subprocess.STDOUT,
            shell=False,
            close_fds=True,
            creationflags=creation_flags,
        )
    finally:
        output.close()


def ensure_worker(timeout: float = 20) -> None:
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            send_command({"command": "status"}, timeout=1)
            return
        except (OSError, EOFError, TimeoutError) as exc:
            last_error = exc
            time.sleep(0.2)
    state = _read_state()
    detail = state.get("last_error") or last_error
    raise RuntimeError(f"Display worker did not start: {detail}")


def worker_entry() -> int:
    return DisplayWorker().run()


def rest_smoke_entry(seconds: float = 60.0) -> int:
    """60秒のRGBDパターンを公式Bridge REST APIからGoへ送る実機スモーク。"""
    from PIL import Image, ImageDraw

    path = cache_directory() / "rest-smoke-rgbd.png"
    renderer = RestBridge()
    try:
        device = renderer.connect()
        height = 512
        rgb_width = max(2, round(height * device.aspect))
        rgb = Image.new("RGB", (rgb_width, height), (0, 0, 0))
        draw = ImageDraw.Draw(rgb)
        third = rgb_width // 3
        draw.rectangle((0, 0, third - 1, height - 1), fill=(255, 48, 48))
        draw.rectangle((third, 0, 2 * third - 1, height - 1), fill=(48, 255, 48))
        draw.rectangle((2 * third, 0, rgb_width - 1, height - 1), fill=(48, 48, 255))
        draw.ellipse((rgb_width // 4, height // 3, 3 * rgb_width // 4, 2 * height // 3), fill=(255, 255, 255))
        depth = Image.new("L", (rgb_width, height), 0)
        depth_draw = ImageDraw.Draw(depth)
        for x in range(rgb_width):
            level = round(255 * x / max(1, rgb_width - 1))
            depth_draw.line((x, 0, x, height - 1), fill=level)
        image = Image.new("RGBA", (rgb_width * 2, height), (0, 0, 0, 255))
        image.paste(rgb, (0, 0))
        image.paste(depth.convert("RGB"), (rgb_width, 0))
        image.save(path, format="PNG")
        renderer.present(path, duration_ms=max(1, round(seconds * 1000)))
        print(
            f"REST display smoke active for {seconds:g}s on Go head {device.index} "
            f"(aspect {device.aspect:g}, depth location 2)",
            flush=True,
        )
        time.sleep(seconds)
        return 0
    except Exception as exc:
        print(f"REST display smoke failed: {exc}", file=sys.stderr)
        return 1
    finally:
        try:
            renderer.close()
        except Exception:
            logging.exception("REST display smoke cleanup failed")
        try:
            path.unlink(missing_ok=True)
        except OSError:
            logging.exception("Failed to remove REST smoke image")


def get_saved_state() -> dict:
    return _read_state()
