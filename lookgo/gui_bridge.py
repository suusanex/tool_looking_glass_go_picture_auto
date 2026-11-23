from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import json
import math
from pathlib import Path
import sys
import threading
from typing import TextIO
import uuid

from filelock import FileLock, Timeout

from .settings import log_path
from .worker import ensure_worker, get_saved_state, send_command, start_worker


def _stopped_state() -> dict:
    state = get_saved_state()
    prior_status = state.get("status", "unknown")
    state["last_status"] = prior_status
    state["status"] = "stopped"
    state["images"] = []
    state["index"] = None
    state["worker_running"] = False
    state["busy"] = False
    state["paused"] = False
    state["active_operation_id"] = None
    state["active_list_sha256"] = None
    state["log"] = str(log_path())
    return state


def _running_state(raw_state: dict | None) -> dict:
    state = dict(raw_state) if isinstance(raw_state, dict) else {}
    state["worker_running"] = True
    state["log"] = str(log_path())
    return state


def _handle_command(payload: dict, worker_start_lock: threading.Lock) -> dict:
    command = payload.get("command")
    if command == "status":
        try:
            response = send_command({"command": "status"}, timeout=3)
            return {"ok": True, "state": _running_state(response.get("state"))}
        except (OSError, EOFError, TimeoutError):
            return {"ok": True, "state": _stopped_state()}

    if command == "cancel":
        operation_id = str(payload.get("operation_id") or "")
        try:
            response = send_command({"command": "cancel", "operation_id": operation_id}, timeout=3)
            return {"ok": bool(response.get("ok", True))}
        except (OSError, EOFError, TimeoutError):
            return {"ok": True}

    if command == "stop":
        try:
            response = send_command({"command": "stop"}, timeout=5)
        except (OSError, EOFError):
            return {"ok": True, "state": _stopped_state()}
        except TimeoutError as exc:
            return {"ok": False, "error": str(exc)}
        if not response.get("ok"):
            return {"ok": False, "error": response.get("error", "Stop failed")}
        lock = FileLock(str(log_path().parent / "worker.lock"))
        deadline = _monotonic() + 15
        while _monotonic() < deadline:
            try:
                lock.acquire(timeout=0)
                lock.release()
                break
            except Timeout:
                _sleep(0.05)
        else:
            return {"ok": False, "error": "Display worker did not release its lock after stop"}
        state = get_saved_state()
        if state.get("status") == "error":
            return {
                "ok": False,
                "error": state.get("last_error") or "Display worker failed during shutdown",
                "state": _stopped_state(),
            }
        return {"ok": True, "state": _stopped_state()}

    if command in {"next", "prev", "pause", "resume", "jump"}:
        worker_payload: dict = {"command": command}
        if command == "jump":
            worker_payload["index"] = payload.get("index")
        try:
            response = send_command(worker_payload, timeout=35)
        except (OSError, EOFError):
            return {"ok": False, "error": "Display worker is not running"}
        except TimeoutError as exc:
            return {"ok": False, "error": str(exc)}
        if "state" in response and isinstance(response["state"], dict):
            response = {**response, "state": _running_state(response["state"])}
        return response

    if command == "show":
        raw_list_path = payload.get("list_path")
        if not isinstance(raw_list_path, str) or not raw_list_path.strip():
            return {"ok": False, "error": "list_path is required"}
        try:
            interval = float(payload.get("interval", 10))
        except (TypeError, ValueError):
            return {"ok": False, "error": "--interval must be a finite number greater than zero"}
        if not math.isfinite(interval) or interval <= 0:
            return {"ok": False, "error": "--interval must be greater than zero"}

        list_path = Path(raw_list_path).expanduser().resolve()
        operation_id = str(payload.get("operation_id") or uuid.uuid4().hex)
        expected_sha256 = payload.get("expected_sha256")
        try:
            with worker_start_lock:
                try:
                    send_command({"command": "status"}, timeout=1)
                except (OSError, EOFError, TimeoutError):
                    start_worker()
                    ensure_worker()

            worker_payload: dict = {
                "command": "show",
                "list_path": str(list_path),
                "interval": interval,
                "operation_id": operation_id,
            }
            if expected_sha256 is not None:
                worker_payload["expected_sha256"] = expected_sha256
            response = send_command(worker_payload, timeout=1800)
        except (OSError, EOFError, TimeoutError, RuntimeError, ValueError) as exc:
            return {"ok": False, "error": str(exc)}

        if "state" in response and isinstance(response["state"], dict):
            response = {**response, "state": _running_state(response["state"])}
        return response

    return {"ok": False, "error": f"Unknown command: {command}"}


def _monotonic() -> float:
    import time

    return time.monotonic()


def _sleep(seconds: float) -> None:
    import time

    time.sleep(seconds)


def gui_bridge_entry(
    stdin: TextIO | None = None,
    stdout: TextIO | None = None,
    executor_workers: int = 8,
) -> int:
    if stdin is None:
        if hasattr(sys.stdin, "reconfigure"):
            sys.stdin.reconfigure(encoding="utf-8", errors="replace")
        stdin = sys.stdin
    if stdout is None:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        stdout = sys.stdout

    write_lock = threading.Lock()
    worker_start_lock = threading.Lock()

    def write_json(obj: dict) -> None:
        line = json.dumps(obj, ensure_ascii=False)
        with write_lock:
            stdout.write(line + "\n")
            stdout.flush()

    write_json({"event": "ready", "protocol": 1, "log": str(log_path())})

    with ThreadPoolExecutor(max_workers=executor_workers, thread_name_prefix="lookgo-bridge") as pool:
        def run_request(req_id: object, payload: dict) -> None:
            try:
                result = _handle_command(payload, worker_start_lock)
            except Exception as exc:
                result = {"ok": False, "error": str(exc)}
            response = {"id": req_id, **result}
            write_json(response)

        while True:
            raw_line = stdin.readline()
            if raw_line == "":
                break
            stripped = raw_line.strip()
            if not stripped:
                continue
            try:
                payload = json.loads(stripped)
            except json.JSONDecodeError as exc:
                write_json({"id": None, "ok": False, "error": f"Invalid JSON: {exc.msg}"})
                continue
            if not isinstance(payload, dict):
                write_json({"id": None, "ok": False, "error": "Request must be a JSON object"})
                continue

            req_id = payload.get("id")
            if payload.get("command") == "shutdown":
                write_json({"id": req_id, "ok": True})
                break

            pool.submit(run_request, req_id, payload)

    return 0
