from __future__ import annotations

from dataclasses import dataclass
import json
import logging
import os
from pathlib import Path
import re
import time
import urllib.error
import urllib.request
import uuid

from .settings import DEPTHINESS, DEPTH_LOCATION, FOCUS

# 対象画像は競合がなければ 1 秒未満で Showing になる。出なければ表示せず止める。
_PLAYBACK_START_TIMEOUT = 30.0
# 1枚表示で pause するまでの余裕。固定表示の期限ではない。
_HOLD_DURATION_MS = 60_000


class PlaylistRejected(RuntimeError):
    """play_playlist を送る前の失敗。既存の再生は変えていない。"""


class PlaylistUnconfirmed(RuntimeError):
    """play_playlist を送ったあと、反映成功前に失敗した。表示は確定しない。"""


class PlaylistTrackingLost(RuntimeError):
    """反映成功後に Bridge ログから現在位置を対応付けできなくなった。再生は続けられる。"""


@dataclass(frozen=True)
class DisplayDevice:
    index: int
    hwid: str
    aspect: float
    rows: int
    columns: int
    view_count: int


class RestBridge:
    """Looking Glass Bridge公式REST APIを使う表示接続。"""

    def __init__(self, timeout: float = 10.0) -> None:
        self.base_url = "http://127.0.0.1:33334/"
        self.timeout = timeout
        self.orchestration: str | None = None
        self.device: DisplayDevice | None = None
        self.active_playlist: str | None = None
        self.owned_playlists: set[str] = set()
        # play_playlist を送る直前のログ位置。反映成功後の位置追跡が、その前の Loading を取りこぼさない。
        self.playback_log_offset: int | None = None
        # show_window(true) が成功するまで False。未表示の間は close でも隠さない。
        self.window_shown = False

    def _http(self, method: str, endpoint: str = "", body: dict | None = None) -> dict | None:
        data = None if body is None else json.dumps(body, ensure_ascii=False).encode("utf-8")
        request = urllib.request.Request(
            self.base_url + endpoint,
            data=data,
            headers={"Content-Type": "application/json"} if data is not None else {},
            method=method,
        )
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                if response.status == 408:
                    raise TimeoutError(f"Looking Glass Bridge timed out at {endpoint or '/'}")
                if not 200 <= response.status < 300:
                    raise RuntimeError(f"Looking Glass Bridge returned HTTP {response.status} at {endpoint or '/'}")
                if endpoint == "":
                    return None
                result = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            if exc.code == 408:
                raise TimeoutError(f"Looking Glass Bridge timed out at {endpoint}") from exc
            raise RuntimeError(f"Looking Glass Bridge returned HTTP {exc.code} at {endpoint}") from exc
        except (urllib.error.URLError, TimeoutError) as exc:
            raise RuntimeError(f"Cannot reach Looking Glass Bridge at {endpoint or '/'}: {exc}") from exc
        if not isinstance(result, dict):
            raise RuntimeError(f"Looking Glass Bridge returned invalid JSON at {endpoint}")
        return result

    @staticmethod
    def _completion(response: dict, endpoint: str) -> None:
        status = response.get("status")
        status_name = status.get("name") if isinstance(status, dict) else None
        status_value = status.get("value") if isinstance(status, dict) else None
        # Bridge.js の sendMessage は Completion と Pending を失敗にしない。Pending は非同期受付。
        if status_value not in {"Completion", "Pending"}:
            raise RuntimeError(
                f"Bridge request {endpoint} returned status {status_value!r} "
                f"({status_name or 'unnamed'}): {response}"
            )

    def _put(self, endpoint: str, body: dict) -> dict:
        response = self._http("PUT", endpoint, body)
        assert response is not None
        self._completion(response, endpoint)
        return response

    def connect(self) -> DisplayDevice:
        self._http("GET")
        version_response = self._put("bridge_version", {})
        version = self._wrapped_value(version_response.get("payload"))
        match = re.match(r"^(\d+)\.(\d+)", str(version))
        if not match or (int(match.group(1)), int(match.group(2))) < (2, 6):
            raise RuntimeError(f"Looking Glass Bridge 2.6 or newer is required; received {version!r}")

        orchestration_response = self._put("enter_orchestration", {"name": f"lookgo-{uuid.uuid4().hex}"})
        token = self._wrapped_value(orchestration_response.get("payload"))
        if not isinstance(token, str) or not token:
            raise RuntimeError("Bridge did not return an orchestration token")
        self.orchestration = token
        devices_response = self._put("available_output_devices", {"orchestration": token})
        devices = self._wrapped_value(devices_response.get("payload"))
        if not isinstance(devices, dict):
            raise RuntimeError("Bridge returned no output-device list")

        candidates: list[DisplayDevice] = []
        for item in devices.values():
            record = self._wrapped_value(item)
            if not isinstance(record, dict):
                continue
            if self._wrapped_value(record.get("hardwareVersion")) != "go_p":
                continue
            if self._wrapped_value(record.get("state")) != "ok":
                continue
            index = int(self._wrapped_value(record.get("index")))
            hwid = str(self._wrapped_value(record.get("hwid")))
            quilt_raw = self._wrapped_value(record.get("defaultQuilt"))
            try:
                quilt = json.loads(quilt_raw)
                aspect = float(quilt["quiltAspect"])
                columns = int(quilt["tileX"])
                rows = int(quilt["tileY"])
            except (TypeError, ValueError, KeyError, json.JSONDecodeError) as exc:
                raise RuntimeError(f"Bridge returned invalid Go quilt settings: {quilt_raw!r}") from exc
            if not hwid or aspect <= 0 or rows <= 0 or columns <= 0:
                raise RuntimeError("Bridge returned invalid Go device information")
            candidates.append(DisplayDevice(index, hwid, aspect, rows, columns, rows * columns))
        if not candidates:
            raise RuntimeError("No connected Looking Glass Go display is available in Bridge")
        self.device = sorted(candidates, key=lambda device: device.index)[0]
        logging.info(
            "Connected to Looking Glass Go %s at head %s with aspect %.4f and quilt %sx%s",
            self.device.hwid, self.device.index, self.device.aspect,
            self.device.columns, self.device.rows,
        )
        return self.device

    @staticmethod
    def _wrapped_value(value):
        if isinstance(value, dict) and "value" in value:
            return value["value"]
        return value

    def _available_go(self) -> None:
        if not self.orchestration or self.device is None:
            raise RuntimeError("Bridge renderer is not connected")
        response = self._put("available_output_devices", {"orchestration": self.orchestration})
        devices = self._wrapped_value(response.get("payload"))
        if not isinstance(devices, dict):
            raise RuntimeError("Bridge returned no output-device list")
        connected = False
        for item in devices.values():
            record = self._wrapped_value(item)
            if not isinstance(record, dict):
                continue
            if (
                self._wrapped_value(record.get("index")) == self.device.index
                and self._wrapped_value(record.get("hwid")) == self.device.hwid
                and self._wrapped_value(record.get("hardwareVersion")) == "go_p"
                and self._wrapped_value(record.get("state")) == "ok"
            ):
                connected = True
                break
        if not connected:
            raise RuntimeError(f"Looking Glass Go {self.device.hwid} disconnected from Bridge")

    def health_check(self) -> None:
        self._http("GET")
        self._available_go()

    def show_playlist(self, image_paths: list[Path], interval_ms: int) -> str:
        """確定した画像リストを1つのプレイリストで表示する。

        複数枚は interval_ms ごとに Bridge がループする。1枚は表示後に pause する。
        play_playlist 前の失敗は PlaylistRejected。送ったあとの失敗は PlaylistUnconfirmed で、
        旧リストの再生し直しはしない。
        """
        if self.orchestration is None or self.device is None:
            raise PlaylistRejected("Bridge renderer is not connected")
        if not image_paths:
            raise PlaylistRejected("Playlist has no images")
        try:
            self._available_go()
        except Exception as exc:
            raise PlaylistRejected(str(exc)) from exc

        pause = len(image_paths) == 1
        duration_ms = _HOLD_DURATION_MS if pause else max(1, int(interval_ms))
        name = f"lookgo-{uuid.uuid4().hex}"
        playlist = {"orchestration": self.orchestration, "name": name}
        try:
            self._put("instance_playlist", {**playlist, "loop": True})
            self.owned_playlists.add(name)
            for index, image_path in enumerate(image_paths):
                self._put("insert_playlist_entry", {
                    **playlist,
                    "id": index,
                    "index": index,
                    "uri": str(image_path.resolve()),
                    "focus": FOCUS,
                    "rows": self.device.rows,
                    "cols": self.device.columns,
                    "aspect": self.device.aspect,
                    "view_count": self.device.view_count,
                    "durationMS": duration_ms,
                    "crop_pos_x": 0,
                    "crop_pos_y": 0,
                    "isRGBD": 1,
                    "depth_loc": DEPTH_LOCATION,
                    "depth_inversion": 0,
                    "chroma_depth": 0,
                    "depthiness": DEPTHINESS,
                    "zoom": 1,
                })
        except Exception as exc:
            self._discard_unplayed(name)
            raise PlaylistRejected(str(exc)) from exc

        try:
            log_path = _current_bridge_log()
            log_offset = log_path.stat().st_size
        except Exception as exc:
            self._discard_unplayed(name)
            raise PlaylistRejected(f"Cannot read Looking Glass Bridge log before playback: {exc}") from exc

        # play_playlist の HTTP 応答も New Item Playing も、そのプロセスで最初の OpenGL 初期化より先に届く。
        # 初期化中の show_window は REST を止めて、再起動まで GET / もタイムアウトさせる。
        # Bridge ログに Showing が出てから表示する。出ないときは show_window を送らない。
        try:
            self._put("play_playlist", {**playlist, "head_index": self.device.index})
        except Exception as exc:
            raise PlaylistUnconfirmed(str(exc)) from exc
        try:
            _wait_until_showing(log_path, log_offset, image_paths[0].name, _PLAYBACK_START_TIMEOUT)
            self._put("show_window", {
                "orchestration": self.orchestration,
                "show_window": True,
                "head_index": self.device.index,
            })
            self.window_shown = True
            if pause:
                self._put("transport_control_pause", {"orchestration": self.orchestration})
        except Exception as exc:
            raise PlaylistUnconfirmed(str(exc)) from exc

        previous = self.active_playlist
        self.playback_log_offset = log_offset
        self.active_playlist = name
        if previous and previous != name:
            try:
                self._delete_playlist(previous)
            except Exception:
                # 新しい表示は確定済み。削除失敗で再生を止めず、close 時に所有リストとして再試行する。
                logging.exception("Failed to delete the previous Bridge playlist %s", previous)
        logging.info("Displayed %s RGBD images through Bridge playlist %s", len(image_paths), name)
        return name

    def _discard_unplayed(self, name: str) -> None:
        if name not in self.owned_playlists:
            return
        try:
            self._delete_playlist(name)
        except Exception:
            logging.exception("Failed to delete unplayed Bridge playlist %s", name)

    def _delete_playlist(self, name: str) -> None:
        if not self.orchestration:
            return
        self._put("delete_playlist", {"orchestration": self.orchestration, "name": name, "loop": True})
        self.owned_playlists.discard(name)
        if self.active_playlist == name:
            self.active_playlist = None

    def close(self) -> None:
        errors: list[Exception] = []
        if self.orchestration:
            # 再生開始前の show_window(false) も初回 OpenGL 初期化と衝突して REST を止める。
            if self.device and self.window_shown:
                try:
                    self._put("show_window", {
                        "orchestration": self.orchestration,
                        "show_window": False,
                        "head_index": self.device.index,
                    })
                except Exception as exc:
                    errors.append(exc)
            for name in list(self.owned_playlists):
                try:
                    self._delete_playlist(name)
                except Exception as exc:
                    errors.append(exc)
            try:
                self._put("exit_orchestration", {"orchestration": self.orchestration})
            except Exception as exc:
                errors.append(exc)
        self.orchestration = None
        self.device = None
        self.window_shown = False
        if errors:
            raise RuntimeError("Bridge cleanup failed: " + "; ".join(map(str, errors)))


def _current_bridge_log() -> Path:
    appdata = os.environ.get("APPDATA")
    root = Path(appdata) / "Looking Glass" if appdata else None
    logs = list(root.glob("Bridge */logs/Looking Glass Bridge *.log")) if root else []
    if not logs:
        raise RuntimeError("Looking Glass Bridge log was not found; show_window was not sent")
    return max(logs, key=lambda path: path.stat().st_mtime)


def _wait_until_showing(log_path: Path, start_offset: int, file_name: str, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    offset = start_offset
    pending = b""
    name = file_name.encode("utf-8")
    logging.info("Waiting until Bridge shows %s before show_window", file_name)
    while True:
        if time.monotonic() >= deadline:
            raise RuntimeError(
                f"Bridge did not show {file_name} within {timeout:.0f}s; show_window was not sent"
            )
        try:
            size = log_path.stat().st_size
            if size < offset:
                offset = 0
                pending = b""
            if size > offset:
                with log_path.open("rb") as handle:
                    handle.seek(offset)
                    pending += handle.read(size - offset)
                offset = size
        except OSError as exc:
            raise RuntimeError(f"Cannot read Looking Glass Bridge log {log_path}: {exc}") from exc
        while b"\n" in pending:
            line, pending = pending.split(b"\n", 1)
            if b"Showing " in line and name in line:
                logging.info("Bridge is showing %s", file_name)
                return
        time.sleep(0.05)


class LoadingCursor:
    """反映成功後の Loading 行から、複数枚プレイリストの現在位置を追う。"""

    def __init__(self, file_names: list[str], start_offset: int | None = None) -> None:
        if len(file_names) < 2:
            raise ValueError("Loading cursor is only used for multiple images")
        self.file_names = list(file_names)
        self.log_path = _current_bridge_log()
        # 再生前の位置を渡されたときは先頭画像の Loading から読む。未指定時は現在位置の次、つまり2枚目から読む。
        if start_offset is None:
            self.offset = self.log_path.stat().st_size
            self.expected = 1
        else:
            self.offset = start_offset
            self.expected = 0
        self.pending = b""

    def poll(self) -> int | None:
        try:
            size = self.log_path.stat().st_size
            if size < self.offset:
                raise PlaylistTrackingLost(f"Bridge log {self.log_path} shrank")
            chunk = b""
            if size > self.offset:
                with self.log_path.open("rb") as handle:
                    handle.seek(self.offset)
                    chunk = handle.read(size - self.offset)
                self.offset = size
        except PlaylistTrackingLost:
            raise
        except OSError as exc:
            raise PlaylistTrackingLost(f"Cannot read Looking Glass Bridge log {self.log_path}: {exc}") from exc
        if not chunk:
            return None
        self.pending += chunk
        latest: int | None = None
        while b"\n" in self.pending:
            raw, self.pending = self.pending.split(b"\n", 1)
            loaded = _loading_file_name(raw)
            if loaded is None:
                continue
            if loaded != self.file_names[self.expected]:
                raise PlaylistTrackingLost(
                    f"Bridge loaded {loaded}, which is not playlist index {self.expected}"
                )
            latest = self.expected
            self.expected = (self.expected + 1) % len(self.file_names)
        return latest


def _loading_file_name(line: bytes) -> str | None:
    text = line.decode("utf-8", errors="replace")
    if "Loading " not in text or " with duration" not in text:
        return None
    shown = text.split("Loading ", 1)[1].split(" with duration", 1)[0].strip()
    if not shown:
        return None
    return Path(shown).name
