from __future__ import annotations

import hashlib
import json
import queue
import threading
import time
from pathlib import Path

from PIL import Image

from lookgo import worker as worker_module


class FakeConnection:
    def __init__(self) -> None:
        self.responses: list[dict] = []
        self.closed = False

    def send_bytes(self, value: bytes) -> None:
        self.responses.append(json.loads(value.decode("utf-8")))

    def poll(self, _timeout: float = 0.0) -> bool:
        return False

    def close(self) -> None:
        self.closed = True


class DisconnectedConnection(FakeConnection):
    def poll(self, _timeout: float = 0.0) -> bool:
        raise EOFError("client gone")


class InlineThread:
    """変換スレッドの開始点を保ちつつ、準備処理を決定的に実行する。"""

    def __init__(self, *, target, **_kwargs) -> None:
        self.target = target

    def start(self) -> None:
        self.target()


def make_worker(monkeypatch, tmp_path: Path) -> worker_module.DisplayWorker:
    worker = worker_module.DisplayWorker.__new__(worker_module.DisplayWorker)
    worker.incoming = queue.Queue()
    worker.prepared = queue.Queue()
    worker.pending = queue.Queue()
    worker.cancel_prepare = threading.Event()
    worker.listener = None
    worker.estimator = object()
    class FakeRenderer:
        def __init__(self):
            self.presented = []
            self.closed = False

        def present(self, path, duration_ms):
            self.presented.append((path, duration_ms))

        def health_check(self):
            pass

        def close(self):
            self.closed = True

    worker.renderer = FakeRenderer()
    worker.aspect = 1.0
    worker.max_texture_size = 4092
    worker.active = []
    worker.active_sources = []
    worker.pending_sources = {}
    worker.pending_intervals = {}
    worker.pending_operations = {}
    worker.cancelled_generations = set()
    worker.index = 0
    worker.interval = 10.0
    worker.last_change = time.monotonic()
    worker.generation = 0
    worker.busy = False
    worker.stopping = False
    worker.last_error = None

    state_file = tmp_path / "worker-state.json"
    cache_dir = tmp_path / "cache"
    cache_dir.mkdir()
    monkeypatch.setattr(worker_module, "state_path", lambda: state_file)
    monkeypatch.setattr(worker_module, "cache_directory", lambda: cache_dir)
    monkeypatch.setattr(worker_module, "cache_key", lambda data, *_args: hashlib.sha256(data).hexdigest())
    monkeypatch.setattr(worker_module.threading, "Thread", InlineThread)
    return worker


def write_image(path: Path, color: tuple[int, int, int]) -> bytes:
    Image.new("RGB", (4, 3), color).save(path, format="PNG")
    return path.read_bytes()


def write_playlist(path: Path, images: list[Path]) -> None:
    path.write_text("\n".join(str(image) for image in images), encoding="utf-8")


def cache_images(worker: worker_module.DisplayWorker, images: list[Path]) -> list[Path]:
    staged = []
    for image in images:
        data = image.read_bytes()
        cached = worker_module.cache_directory() / f"{worker_module.cache_key(data, 1.0, worker.max_texture_size // 2)}.png"
        cached.write_bytes(data)
        staged.append(cached)
    return staged


def submit_show(worker: worker_module.DisplayWorker, list_path: Path, interval: float = 10) -> FakeConnection:
    connection = FakeConnection()
    worker._handle_request(worker_module.Request(
        {"command": "show", "list_path": str(list_path), "interval": interval}, connection
    ))
    return connection


def seed_active(worker: worker_module.DisplayWorker, sources: list[Path], interval: float = 7) -> None:
    worker.active_sources = list(sources)
    worker.active = [Path(f"cache-{index}.png") for index in range(len(sources))]
    worker.interval = interval
    worker.index = 0
    worker._save_state("playing")


def test_show_ack_follows_first_draw_and_replaces_entire_playlist(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    a, b, c, d, e = [tmp_path / f"{name}.png" for name in "ABCDE"]
    for path, color in zip((a, b, c, d, e), ((1, 2, 3), (4, 5, 6), (7, 8, 9), (10, 11, 12), (13, 14, 15))):
        write_image(path, color)
    playlist = tmp_path / "list.txt"
    first = [a, b, c]
    write_playlist(playlist, first)
    cache_images(worker, first)

    first_connection = submit_show(worker, playlist, interval=3)
    assert first_connection.responses == []
    assert worker.active_sources == []

    draw_observations = []
    worker.renderer.present = lambda path, duration_ms: draw_observations.append((path, duration_ms, list(first_connection.responses)))
    worker._pump()
    assert draw_observations == [(worker.active[0], 3000, [])]
    assert first_connection.responses[0]["ok"] is True
    assert worker.active_sources == first
    assert worker.interval == 3

    second = [d, e]
    write_playlist(playlist, second)
    cache_images(worker, second)
    second_connection = submit_show(worker, playlist, interval=5)
    assert second_connection.responses == []
    worker._pump()

    assert second_connection.responses[0]["ok"] is True
    assert worker.active_sources == second
    assert worker.interval == 5
    assert worker.index == 0


def test_failed_update_keeps_old_playlist_and_interval(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    old = [tmp_path / "old-a.png", tmp_path / "old-b.png"]
    seed_active(worker, old, interval=17)
    replacement = tmp_path / "replacement.png"
    write_image(replacement, (20, 30, 40))
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [replacement])
    monkeypatch.setattr(worker_module, "convert_to_rgbd", lambda *_args: (_ for _ in ()).throw(RuntimeError("conversion failed")))

    connection = submit_show(worker, playlist, interval=2)
    worker._pump()

    assert connection.responses[0]["ok"] is False
    assert worker.active_sources == old
    assert worker.interval == 17
    state = json.loads(worker_module.state_path().read_text(encoding="utf-8"))
    assert state["images"] == [str(path) for path in old]


def test_first_preparation_failure_returns_error_and_stops_worker(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    image = tmp_path / "image.png"
    write_image(image, (1, 1, 1))
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [image])
    monkeypatch.setattr(worker_module, "convert_to_rgbd", lambda *_args: (_ for _ in ()).throw(RuntimeError("conversion failed")))

    connection = submit_show(worker, playlist)
    worker._pump()

    assert connection.responses[0]["ok"] is False
    assert worker.active_sources == []
    assert worker.stopping is True


def test_first_draw_failure_returns_error_without_committing(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    image = tmp_path / "image.png"
    write_image(image, (8, 8, 8))
    cache_images(worker, [image])
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [image])
    worker.renderer.present = lambda *_args, **_kwargs: (_ for _ in ()).throw(RuntimeError("draw failed"))

    connection = submit_show(worker, playlist)
    try:
        worker._pump()
    except RuntimeError as exc:
        assert str(exc) == "draw failed"
    else:
        raise AssertionError("A failed first draw must stop the worker pump")

    assert connection.responses[0]["ok"] is False
    assert worker.active_sources == []
    assert worker.interval == 10.0


def test_second_show_is_rejected_while_first_is_staging(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    image = tmp_path / "image.png"
    write_image(image, (3, 2, 1))
    cache_images(worker, [image])
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [image])

    pending = submit_show(worker, playlist)
    rejected = submit_show(worker, playlist)

    assert pending.responses == []
    assert rejected.responses[0]["ok"] is False
    assert "already in progress" in rejected.responses[0]["error"]
    worker._pump()
    assert pending.responses[0]["ok"] is True


def test_status_reports_source_paths_during_conversion(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    active = [tmp_path / "current.png"]
    seed_active(worker, active)
    replacement = tmp_path / "next.png"
    write_image(replacement, (7, 8, 9))
    cache_images(worker, [replacement])
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [replacement])
    submit_show(worker, playlist)

    connection = FakeConnection()
    worker._handle_request(worker_module.Request({"command": "status"}, connection))

    assert connection.responses[0]["state"]["images"] == [str(active[0])]
    assert connection.responses[0]["state"]["busy"] is True


def test_stop_and_prepared_result_in_same_pump_cancel_without_commit(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    old = [tmp_path / "old.png"]
    seed_active(worker, old, interval=11)
    replacement = tmp_path / "replacement.png"
    write_image(replacement, (12, 13, 14))
    cache_images(worker, [replacement])
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [replacement])
    show_connection = submit_show(worker, playlist, interval=2)
    stop_connection = FakeConnection()
    worker.incoming.put(worker_module.Request({"command": "stop"}, stop_connection))

    worker._pump()

    assert stop_connection.responses[0]["ok"] is True
    assert show_connection.responses and show_connection.responses[0]["ok"] is False
    assert worker.active_sources == old
    assert worker.interval == 11


def test_cancel_after_client_timeout_prevents_late_commit(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    old = [tmp_path / "old.png"]
    seed_active(worker, old, interval=9)
    replacement = tmp_path / "replacement.png"
    write_image(replacement, (30, 31, 32))
    cache_images(worker, [replacement])
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [replacement])
    show_connection = FakeConnection()
    worker._handle_request(worker_module.Request({
        "command": "show", "list_path": str(playlist), "interval": 1, "operation_id": "op-timeout"
    }, show_connection))

    cancel_connection = FakeConnection()
    worker._handle_request(worker_module.Request({
        "command": "cancel", "operation_id": "op-timeout"
    }, cancel_connection))
    worker._pump()

    assert cancel_connection.responses[0]["ok"] is True
    assert show_connection.responses[0]["ok"] is False
    assert worker.active_sources == old
    assert worker.interval == 9


def test_disconnected_show_client_is_not_requeued(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    image = tmp_path / "image.png"
    write_image(image, (1, 2, 3))
    cache_images(worker, [image])
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [image])
    submit_show(worker, playlist)

    disconnected = DisconnectedConnection()
    generation, _connection = worker.pending.get_nowait()
    worker.pending.put((generation, disconnected))
    worker._pump()

    assert worker.active_sources == []
    assert worker.pending.empty()
    assert disconnected.closed is True


def test_single_image_stays_fixed_and_multiple_images_wrap(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    first = tmp_path / "first.png"
    seed_active(worker, [first], interval=1)
    drawn = []
    worker.active = [Path("first-cache.png")]
    worker.renderer.present = lambda path, duration_ms: drawn.append(path)
    worker.last_change = time.monotonic() - 2

    worker._pump()

    assert worker.index == 0
    assert drawn == []

    worker.active = [Path("first-cache.png"), Path("second-cache.png")]
    worker.index = 0
    worker.last_change = time.monotonic() - 2
    worker._pump()
    assert worker.index == 1
    assert drawn == [Path("second-cache.png")]
    worker.last_change = time.monotonic() - 2
    worker._pump()
    assert worker.index == 0
    assert drawn[-1] == Path("first-cache.png")


def test_run_releases_singleton_lock_and_display_resources_on_stop(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    calls = []

    class FakeLock:
        def __init__(self, _path):
            pass

        def acquire(self, timeout):
            calls.append(("acquire", timeout))

        def release(self):
            calls.append(("release",))

    class FakeListener:
        def close(self):
            calls.append(("close_listener",))

    monkeypatch.setattr(worker_module, "FileLock", FakeLock)
    monkeypatch.setattr(worker_module, "data_directory", lambda: tmp_path)
    worker.renderer.close = lambda: calls.append(("renderer_close",))
    worker.listener = FakeListener()
    worker._initialize_display = lambda: None
    worker._start_listener = lambda: None

    def stop_on_pump():
        worker.stopping = True

    worker._pump = stop_on_pump

    assert worker.run() == 0
    assert ("close_listener",) in calls
    assert ("renderer_close",) in calls
    assert calls[-1] == ("release",)
