from __future__ import annotations

import hashlib
import json
import queue
import threading
import time
from pathlib import Path

import pytest

from PIL import Image

from lookgo.bridge import PlaylistRejected, PlaylistTrackingLost, PlaylistUnconfirmed
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
            self.seeks = []
            self.steps = []
            self.pauses = 0
            self.resumes = 0
            self.playback_log_offset = None
            self.closed = False

        def show_playlist(self, paths, interval_ms):
            self.presented.append((list(paths), interval_ms))

        def pause_playback(self):
            self.pauses += 1

        def resume_playback(self):
            self.resumes += 1

        def seek_to_index(self, index, *, pause_after=False):
            self.seeks.append((index, pause_after))

        def step_next(self, *, pause_after=False):
            self.steps.append(("next", pause_after))

        def step_previous(self, *, pause_after=False):
            self.steps.append(("prev", pause_after))

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
    worker.pending_list_paths = {}
    worker.pending_list_digests = {}
    worker.cancelled_generations = set()
    worker.index = 0
    worker.playback_cursor = None
    worker.interval = 10.0
    worker.paused = False
    worker.list_path = None
    worker.active_operation_id = None
    worker.active_list_sha256 = None
    worker.last_change = time.monotonic()
    worker.generation = 0
    worker.busy = False
    worker.stopping = False
    worker.last_error = None

    state_file = tmp_path / "worker-state.json"
    cache_dir = tmp_path / "cache"
    cache_dir.mkdir()
    bridge_log = tmp_path / "bridge.log"
    bridge_log.write_bytes(b"")
    monkeypatch.setattr(worker_module, "state_path", lambda: state_file)
    monkeypatch.setattr("lookgo.bridge._current_bridge_log", lambda: bridge_log)
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
    worker.paused = False
    worker.active_operation_id = "seed-op"
    worker.active_list_sha256 = "seed-sha"
    worker._save_state("playing")


def send_req(worker: worker_module.DisplayWorker, payload: dict) -> FakeConnection:
    connection = FakeConnection()
    worker._handle_request(worker_module.Request(payload, connection))
    return connection


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
    worker.renderer.show_playlist = lambda paths, interval_ms: draw_observations.append((list(paths), interval_ms, list(first_connection.responses)))
    worker._pump()
    assert draw_observations == [(worker.active, 3000, [])]
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
    worker.renderer.show_playlist = lambda *_args, **_kwargs: (_ for _ in ()).throw(
        PlaylistUnconfirmed("playback was not confirmed")
    )

    connection = submit_show(worker, playlist)
    try:
        worker._pump()
    except PlaylistUnconfirmed as exc:
        assert str(exc) == "playback was not confirmed"
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


def test_single_image_is_not_redrawn_and_multiple_images_follow_the_log(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    first = tmp_path / "first.png"
    seed_active(worker, [first], interval=1)
    worker.active = [Path("first-cache.png")]
    drawn = []
    worker.renderer.show_playlist = lambda paths, interval_ms: drawn.append(paths)
    worker.last_change = time.monotonic() - 2

    worker._pump()

    assert worker.index == 0
    assert drawn == []

    log_path = tmp_path / "bridge.log"
    log_path.write_bytes(b"")
    monkeypatch.setattr("lookgo.bridge._current_bridge_log", lambda: log_path)
    worker.active = [Path("a.png"), Path("b.png")]
    worker.active_sources = [tmp_path / "a.png", tmp_path / "b.png"]
    worker.index = 0
    worker.playback_cursor = worker_module.LoadingCursor(["a.png", "b.png"])
    with log_path.open("ab") as handle:
        handle.write(b"Loading C:\\cache\\b.png with duration of: 2000 ms\n")
    worker._pump()
    assert worker.index == 1
    assert drawn == []
    state = json.loads(worker_module.state_path().read_text(encoding="utf-8"))
    assert state["status"] == "playing"
    assert state["index"] == 1

    worker.playback_cursor.poll = lambda: (_ for _ in ()).throw(PlaylistTrackingLost("unreadable"))
    worker._pump()
    assert worker.index is None
    assert worker.playback_cursor is None
    assert worker.stopping is False
    state = json.loads(worker_module.state_path().read_text(encoding="utf-8"))
    assert state["status"] == "playing"
    assert state["index"] is None
    assert state["images"] == [str(tmp_path / "a.png"), str(tmp_path / "b.png")]


def test_rejected_replacement_keeps_the_current_display(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    old = [tmp_path / "old.png"]
    seed_active(worker, old, interval=4)
    replacement = tmp_path / "replacement.png"
    write_image(replacement, (1, 2, 3))
    cache_images(worker, [replacement])
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [replacement])
    worker.renderer.show_playlist = lambda *_args, **_kwargs: (_ for _ in ()).throw(PlaylistRejected("insert failed"))

    connection = submit_show(worker, playlist, interval=9)
    worker._pump()

    assert connection.responses[0]["ok"] is False
    assert worker.active_sources == old
    assert worker.interval == 4
    assert worker.stopping is False


def test_unconfirmed_replacement_stops_without_reporting_the_old_list(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    old = [tmp_path / "old.png"]
    seed_active(worker, old, interval=4)
    replacement = tmp_path / "replacement.png"
    write_image(replacement, (4, 5, 6))
    cache_images(worker, [replacement])
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [replacement])
    worker.renderer.show_playlist = lambda *_args, **_kwargs: (_ for _ in ()).throw(
        PlaylistUnconfirmed("showing was not confirmed")
    )

    connection = submit_show(worker, playlist, interval=9)
    with pytest.raises(PlaylistUnconfirmed):
        worker._pump()

    assert connection.responses[0]["ok"] is False
    assert worker.active_sources == []
    assert worker.stopping is True
    state = json.loads(worker_module.state_path().read_text(encoding="utf-8"))
    assert state["status"] == "error"
    assert state["images"] == []
    assert state["index"] is None


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


def test_next_prev_seek_existing_playlist_and_update_loading_cursor(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    sources = [tmp_path / "a.png", tmp_path / "b.png", tmp_path / "c.png"]
    seed_active(worker, sources, interval=10)
    worker.active = [Path("a.png"), Path("b.png"), Path("c.png")]

    r1 = send_req(worker, {"command": "next"})
    assert r1.responses[0]["ok"] is True
    assert r1.responses[0]["state"]["index"] == 1
    assert r1.responses[0]["state"]["active_operation_id"] == "seed-op"

    r2 = send_req(worker, {"command": "next"})
    assert r2.responses[0]["state"]["index"] == 2
    r3 = send_req(worker, {"command": "next"})
    assert r3.responses[0]["state"]["index"] == 0
    r4 = send_req(worker, {"command": "prev"})
    assert r4.responses[0]["state"]["index"] == 2

    assert worker.renderer.presented == []
    assert worker.renderer.seeks == [(1, False), (2, False), (0, False), (2, False)]

    # LoadingCursor expects index 0 (a.png) after seeking to index 2
    log_path = tmp_path / "bridge.log"
    with log_path.open("ab") as handle:
        handle.write(b"Loading C:\\cache\\a.png with duration of: 10000 ms\n")
    worker._pump()
    assert worker.index == 0


def test_pause_resume_and_navigation_while_paused(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    sources = [tmp_path / "a.png", tmp_path / "b.png", tmp_path / "c.png"]
    seed_active(worker, sources, interval=1)
    worker.active = [Path("a.png"), Path("b.png"), Path("c.png")]

    pause_resp = send_req(worker, {"command": "pause"}).responses[0]
    assert pause_resp["ok"] is True
    assert pause_resp["state"]["paused"] is True
    assert pause_resp["state"]["status"] == "paused"
    assert worker.renderer.pauses == 1

    # Duplicate pause is a no-op on Bridge
    pause_dup = send_req(worker, {"command": "pause"}).responses[0]
    assert pause_dup["ok"] is True
    assert worker.renderer.pauses == 1

    # next/prev/jump pass pause_after=True and keep paused=True
    next_resp = send_req(worker, {"command": "next"}).responses[0]
    assert next_resp["state"]["index"] == 1
    assert next_resp["state"]["paused"] is True
    assert next_resp["state"]["status"] == "paused"

    jump_resp = send_req(worker, {"command": "jump", "index": 2}).responses[0]
    assert jump_resp["state"]["index"] == 2
    assert jump_resp["state"]["paused"] is True

    prev_resp = send_req(worker, {"command": "prev"}).responses[0]
    assert prev_resp["state"]["index"] == 1
    assert prev_resp["state"]["paused"] is True
    assert worker.renderer.seeks == [(1, True), (2, True), (1, True)]

    resume_resp = send_req(worker, {"command": "resume"}).responses[0]
    assert resume_resp["ok"] is True
    assert resume_resp["state"]["paused"] is False
    assert resume_resp["state"]["status"] == "playing"
    assert worker.renderer.resumes == 1
    assert worker.active_operation_id == "seed-op"
    assert worker.renderer.presented == []

    # After resume from index 1, log advancing to c.png tracks to index 2
    log_path = tmp_path / "bridge.log"
    with log_path.open("ab") as handle:
        handle.write(b"Loading C:\\cache\\c.png with duration of: 1000 ms\n")
    worker._pump()
    assert worker.index == 2


def test_jump_same_index_and_single_image_skip_bridge_calls_and_invalid_jump_rejected(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    seed_active(worker, [tmp_path / "only.png"], interval=5)

    for cmd in (
        {"command": "next"},
        {"command": "prev"},
        {"command": "jump", "index": 0},
        {"command": "pause"},
        {"command": "resume"},
    ):
        resp = send_req(worker, cmd).responses[0]
        assert resp["ok"] is True
        assert resp["state"]["index"] == 0
    assert worker.renderer.presented == []
    assert worker.renderer.seeks == []
    assert worker.renderer.steps == []
    assert worker.renderer.pauses == 0
    assert worker.renderer.resumes == 0

    for bad in (
        {"command": "jump", "index": -1},
        {"command": "jump", "index": 1},
        {"command": "jump", "index": True},
        {"command": "jump", "index": "0"},
        {"command": "jump"},
    ):
        resp = send_req(worker, bad).responses[0]
        assert resp["ok"] is False
        assert worker.stopping is False


def test_playback_controls_work_while_busy_and_failed_show_preserves_paused_and_operation_id(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    old = [tmp_path / "old-a.png", tmp_path / "old-b.png"]
    seed_active(worker, old, interval=11)
    send_req(worker, {"command": "pause"})

    replacement = tmp_path / "replacement.png"
    write_image(replacement, (1, 2, 3))
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [replacement])
    monkeypatch.setattr(worker_module, "convert_to_rgbd", lambda *_args: (_ for _ in ()).throw(RuntimeError("boom")))

    show_conn = submit_show(worker, playlist, interval=4)
    assert worker.busy is True

    nav = send_req(worker, {"command": "next"}).responses[0]
    assert nav["ok"] is True
    assert nav["state"]["index"] == 1
    assert nav["state"]["paused"] is True
    assert nav["state"]["busy"] is True
    assert nav["state"]["active_operation_id"] == "seed-op"

    worker._pump()
    assert show_conn.responses[0]["ok"] is False
    assert worker.active_sources == old
    assert worker.paused is True
    assert worker.active_operation_id == "seed-op"
    assert worker.active_list_sha256 == "seed-sha"
    state = json.loads(worker_module.state_path().read_text(encoding="utf-8"))
    assert state["status"] == "paused"
    assert state["paused"] is True
    assert state["active_operation_id"] == "seed-op"


def test_rejected_show_while_paused_preserves_paused_and_unconfirmed_clears_metadata(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    old = [tmp_path / "old-a.png", tmp_path / "old-b.png"]
    seed_active(worker, old, interval=11)
    send_req(worker, {"command": "pause"})

    replacement = tmp_path / "replacement.png"
    write_image(replacement, (1, 2, 3))
    cache_images(worker, [replacement])
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [replacement])
    worker.renderer.show_playlist = lambda *_args, **_kwargs: (_ for _ in ()).throw(PlaylistRejected("insert failed"))

    rej_conn = submit_show(worker, playlist, interval=4)
    worker._pump()
    assert rej_conn.responses[0]["ok"] is False
    assert worker.paused is True
    assert worker.active_operation_id == "seed-op"
    state = json.loads(worker_module.state_path().read_text(encoding="utf-8"))
    assert state["status"] == "paused"
    assert state["paused"] is True

    worker.renderer.show_playlist = lambda *_args, **_kwargs: (_ for _ in ()).throw(PlaylistUnconfirmed("play failed"))
    unconf_conn = submit_show(worker, playlist, interval=4)
    with pytest.raises(PlaylistUnconfirmed):
        worker._pump()
    assert unconf_conn.responses[0]["ok"] is False
    assert worker.paused is False
    assert worker.active_operation_id is None
    assert worker.active_list_sha256 is None
    state = json.loads(worker_module.state_path().read_text(encoding="utf-8"))
    assert state["status"] == "error"
    assert state["paused"] is False
    assert state["active_operation_id"] is None


def test_tracking_lost_preserves_status_supports_relative_step_and_recovers_on_jump(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    sources = [tmp_path / "a.png", tmp_path / "b.png", tmp_path / "c.png"]
    seed_active(worker, sources, interval=5)
    worker.active = [Path("a.png"), Path("b.png"), Path("c.png")]
    send_req(worker, {"command": "pause"})

    class FailingCursor:
        def poll(self):
            raise PlaylistTrackingLost("log lost")

    worker.playback_cursor = FailingCursor()
    worker._pump()
    assert worker.index is None
    assert worker.playback_cursor is None
    state = json.loads(worker_module.state_path().read_text(encoding="utf-8"))
    assert state["status"] == "paused"
    assert state["index"] is None

    # next / prev while index is None use step_next / step_previous and keep index=None
    r_next = send_req(worker, {"command": "next"}).responses[0]
    assert r_next["ok"] is True
    assert r_next["state"]["index"] is None
    r_prev = send_req(worker, {"command": "prev"}).responses[0]
    assert r_prev["ok"] is True
    assert r_prev["state"]["index"] is None
    assert worker.renderer.steps == [("next", True), ("prev", True)]

    # jump(2) recovers index=2 and re-attaches cursor
    r_jump = send_req(worker, {"command": "jump", "index": 2}).responses[0]
    assert r_jump["ok"] is True
    assert r_jump["state"]["index"] == 2
    assert worker.playback_cursor is not None


def test_expected_sha256_verification_and_successful_show_resets_pause_and_operation_id(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    seed_active(worker, [tmp_path / "old.png"], interval=9)
    send_req(worker, {"command": "pause"})

    img = tmp_path / "new.png"
    write_image(img, (10, 20, 30))
    cache_images(worker, [img])
    playlist = tmp_path / "list.txt"
    write_playlist(playlist, [img])
    digest = hashlib.sha256(playlist.read_bytes()).hexdigest()

    mismatch = send_req(worker, {
        "command": "show",
        "list_path": str(playlist),
        "interval": 6,
        "operation_id": "op-bad",
        "expected_sha256": "0" * 64,
    }).responses[0]
    assert mismatch["ok"] is False
    assert "changed" in mismatch["error"]
    assert worker.busy is False
    assert worker.paused is True
    assert worker.active_operation_id == "seed-op"

    ok_conn = send_req(worker, {
        "command": "show",
        "list_path": str(playlist),
        "interval": 6,
        "operation_id": "op-good",
        "expected_sha256": digest,
    })
    worker._pump()
    resp = ok_conn.responses[0]
    assert resp["ok"] is True
    assert resp["state"]["paused"] is False
    assert resp["state"]["status"] == "playing"
    assert resp["state"]["index"] == 0
    assert resp["state"]["list_path"] == str(playlist.resolve())
    assert resp["state"]["active_operation_id"] == "op-good"
    assert resp["state"]["active_list_sha256"] == digest


def test_manual_controls_without_active_playlist_and_bridge_failure_on_transport(monkeypatch, tmp_path):
    worker = make_worker(monkeypatch, tmp_path)
    for cmd in ("next", "prev", "pause", "resume"):
        resp = send_req(worker, {"command": cmd}).responses[0]
        assert resp["ok"] is False
        assert worker.stopping is False
    resp = send_req(worker, {"command": "jump", "index": 0}).responses[0]
    assert resp["ok"] is False
    assert worker.stopping is False

    seed_active(worker, [tmp_path / "a.png", tmp_path / "b.png"])
    worker.renderer.seek_to_index = lambda *_args, **_kwargs: (_ for _ in ()).throw(RuntimeError("bridge disconnected"))
    fail_resp = send_req(worker, {"command": "next"}).responses[0]
    assert fail_resp["ok"] is False
    assert "bridge disconnected" in fail_resp["error"]
    assert worker.stopping is True
    assert worker.active == []
    assert worker.active_sources == []
    assert worker.active_operation_id is None
    state = json.loads(worker_module.state_path().read_text(encoding="utf-8"))
    assert state["status"] == "error"
    assert state["images"] == []
    assert "bridge disconnected" in state["last_error"]

