from __future__ import annotations

import io
import json
from pathlib import Path
import threading
import time

from lookgo import gui_bridge


def _run_bridge(monkeypatch, lines: list[str], handler) -> list[dict]:
    stdin = io.StringIO("\n".join(lines) + ("\n" if lines else ""))
    stdout = io.StringIO()
    monkeypatch.setattr(gui_bridge, "send_command", handler)
    monkeypatch.setattr(gui_bridge, "log_path", lambda: Path(r"C:\logs\lookgo.log"))
    code = gui_bridge.gui_bridge_entry(stdin=stdin, stdout=stdout, executor_workers=4)
    assert code == 0
    return [json.loads(line) for line in stdout.getvalue().splitlines() if line.strip()]


def test_bridge_emits_ready_and_falls_back_when_worker_stopped(monkeypatch):
    monkeypatch.setattr(gui_bridge, "get_saved_state", lambda: {
        "status": "playing",
        "images": [r"C:\img\a.png"],
        "index": 0,
        "busy": True,
        "paused": True,
        "active_operation_id": "old-op",
        "active_list_sha256": "old-sha",
    })

    def fail_send(_payload, timeout=3):
        raise OSError("pipe missing")

    messages = _run_bridge(
        monkeypatch,
        [json.dumps({"id": "1", "command": "status"}), json.dumps({"id": "2", "command": "shutdown"})],
        fail_send,
    )
    assert messages[0]["event"] == "ready"
    status_resp = messages[1]
    assert status_resp["id"] == "1"
    assert status_resp["ok"] is True
    state = status_resp["state"]
    assert state["status"] == "stopped"
    assert state["last_status"] == "playing"
    assert state["images"] == []
    assert state["index"] is None
    assert state["worker_running"] is False
    assert state["busy"] is False
    assert state["paused"] is False
    assert state["active_operation_id"] is None
    assert state["active_list_sha256"] is None
    assert messages[2] == {"id": "2", "ok": True}


def test_bridge_forwards_playback_commands_and_japanese_paths(monkeypatch, tmp_path: Path):
    seen = []
    playlist = tmp_path / "プレイリスト 日本語.txt"
    playlist.write_text("a.png\n", encoding="utf-8")

    def fake_send(payload, timeout=3):
        seen.append((payload, timeout))
        cmd = payload["command"]
        if cmd == "status":
            return {"ok": True, "state": {"status": "playing", "images": [str(tmp_path / "a.png")], "index": 0}}
        if cmd == "show":
            return {
                "ok": True,
                "state": {
                    "status": "playing",
                    "images": [str(tmp_path / "a.png")],
                    "index": 0,
                    "list_path": str(playlist.resolve()),
                    "active_operation_id": payload["operation_id"],
                    "active_list_sha256": payload["expected_sha256"],
                },
            }
        return {"ok": True, "state": {"status": "paused", "images": [str(tmp_path / "a.png")], "index": 0, "paused": True}}

    messages = _run_bridge(
        monkeypatch,
        [
            json.dumps({
                "id": "s1",
                "command": "show",
                "list_path": str(playlist),
                "interval": 12.5,
                "operation_id": "op-jp",
                "expected_sha256": "abc123",
            }, ensure_ascii=False),
            json.dumps({"id": "n1", "command": "next"}),
            json.dumps({"id": "j1", "command": "jump", "index": 0}),
            json.dumps({"id": "p1", "command": "pause"}),
            json.dumps({"id": "r1", "command": "resume"}),
            json.dumps({"id": "q1", "command": "shutdown"}),
        ],
        fake_send,
    )
    assert messages[0]["event"] == "ready"
    by_id = {m.get("id"): m for m in messages[1:]}
    assert by_id["s1"]["ok"] is True
    assert by_id["s1"]["state"]["worker_running"] is True
    assert by_id["s1"]["state"]["active_operation_id"] == "op-jp"
    assert by_id["s1"]["state"]["active_list_sha256"] == "abc123"
    assert by_id["n1"]["ok"] is True
    assert by_id["j1"]["ok"] is True
    assert by_id["p1"]["ok"] is True
    assert by_id["r1"]["ok"] is True
    assert by_id["q1"] == {"id": "q1", "ok": True}
    assert any(p.get("command") == "jump" and p.get("index") == 0 for p, _ in seen)


def test_bridge_handles_concurrent_status_and_cancel_while_show_is_blocked(monkeypatch, tmp_path: Path):
    playlist = tmp_path / "list.txt"
    playlist.write_text("a.png\n", encoding="utf-8")
    show_entered = threading.Event()
    release_show = threading.Event()

    def fake_send(payload, timeout=3):
        cmd = payload["command"]
        if cmd == "status" and not show_entered.is_set():
            return {"ok": True, "state": {"status": "idle", "images": [], "index": None}}
        if cmd == "show":
            show_entered.set()
            assert release_show.wait(timeout=5)
            return {"ok": False, "error": "Playlist update was cancelled"}
        if cmd == "status":
            return {"ok": True, "state": {"status": "updating", "busy": True, "images": ["a.png"], "index": 0}}
        if cmd == "cancel":
            release_show.set()
            return {"ok": True}
        raise AssertionError(cmd)

    # Stream commands dynamically so status and cancel arrive after show enters
    r_fd, w_fd = __import__("os").pipe()
    reader = __import__("os").fdopen(r_fd, "r", encoding="utf-8")
    writer = __import__("os").fdopen(w_fd, "w", encoding="utf-8")
    stdout = io.StringIO()
    monkeypatch.setattr(gui_bridge, "send_command", fake_send)
    monkeypatch.setattr(gui_bridge, "log_path", lambda: Path(r"C:\logs\lookgo.log"))

    result_box = {}
    t = threading.Thread(
        target=lambda: result_box.setdefault(
            "code", gui_bridge.gui_bridge_entry(stdin=reader, stdout=stdout, executor_workers=4)
        )
    )
    t.start()
    try:
        writer.write(json.dumps({"id": "show1", "command": "show", "list_path": str(playlist), "interval": 5, "operation_id": "op-slow"}) + "\n")
        writer.flush()
        assert show_entered.wait(timeout=3)
        writer.write(json.dumps({"id": "st1", "command": "status"}) + "\n")
        writer.write(json.dumps({"id": "c1", "command": "cancel", "operation_id": "op-slow"}) + "\n")
        writer.flush()
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            lines = [json.loads(x) for x in stdout.getvalue().splitlines() if x.strip()]
            if any(m.get("id") == "show1" for m in lines) and any(m.get("id") == "st1" for m in lines):
                break
            time.sleep(0.02)
        writer.write(json.dumps({"id": "sd", "command": "shutdown"}) + "\n")
        writer.flush()
    finally:
        writer.close()
        t.join(timeout=5)
        reader.close()

    assert result_box["code"] == 0
    lines = [json.loads(x) for x in stdout.getvalue().splitlines() if x.strip()]
    by_id = {m.get("id"): m for m in lines if "id" in m}
    assert by_id["st1"]["state"]["busy"] is True
    assert by_id["c1"]["ok"] is True
    assert by_id["show1"]["ok"] is False


def test_bridge_handles_eof_and_invalid_json_without_stopping_worker(monkeypatch):
    calls = []

    def fake_send(payload, timeout=3):
        calls.append(payload)
        return {"ok": True, "state": {}}

    messages = _run_bridge(monkeypatch, ["{not json", json.dumps([1, 2, 3])], fake_send)
    assert messages[0]["event"] == "ready"
    assert messages[1]["ok"] is False and "Invalid JSON" in messages[1]["error"]
    assert messages[2]["ok"] is False and "JSON object" in messages[2]["error"]
    assert calls == []


def test_bridge_stop_waits_for_lock_and_returns_stopped_state(monkeypatch, tmp_path: Path):
    monkeypatch.setattr(gui_bridge, "log_path", lambda: tmp_path / "lookgo.log")
    monkeypatch.setattr(gui_bridge, "get_saved_state", lambda: {"status": "stopped", "images": [], "index": None})

    messages = _run_bridge(
        monkeypatch,
        [json.dumps({"id": "stop1", "command": "stop"}), json.dumps({"id": "q", "command": "shutdown"})],
        lambda payload, timeout=5: {"ok": True, "state": {"status": "stopping"}},
    )
    stop_msg = next(m for m in messages if m.get("id") == "stop1")
    assert stop_msg["ok"] is True
    assert stop_msg["state"]["status"] == "stopped"
    assert stop_msg["state"]["worker_running"] is False
