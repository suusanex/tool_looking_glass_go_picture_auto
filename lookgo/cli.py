from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
import time
import uuid

from filelock import FileLock, Timeout

from .settings import log_path
from .worker import ensure_worker, get_saved_state, rest_smoke_entry, send_command, start_worker, worker_entry


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="lookgo", description="Display images on Looking Glass Go")
    commands = parser.add_subparsers(dest="command", required=True)
    show = commands.add_parser("show", help="Display images from a playlist")
    show.add_argument("--list", required=True, type=Path, help="UTF-8 text file with one image path per line")
    show.add_argument("--interval", type=float, default=10, help="Image interval in seconds (default: 10)")
    commands.add_parser("status", help="Show display worker status")
    commands.add_parser("stop", help="Stop display playback")
    return parser


def _status() -> int:
    try:
        response = send_command({"command": "status"}, timeout=3)
        state = response.get("state", {})
    except (OSError, EOFError, TimeoutError):
        state = get_saved_state()
        prior_status = state.get("status", "unknown")
        state["last_status"] = prior_status
        state["status"] = "stopped"
        state["worker_running"] = False
    else:
        state["worker_running"] = True
    state["log"] = str(log_path())
    print(json.dumps(state, ensure_ascii=False, indent=2))
    return 0


def main(argv: list[str] | None = None) -> int:
    arguments = list(sys.argv[1:] if argv is None else argv)
    if arguments and arguments[0] == "_worker":
        return worker_entry()
    if arguments and arguments[0] == "_smoke":
        return rest_smoke_entry(float(arguments[1]) if len(arguments) > 1 else 60.0)
    if arguments and arguments[0] == "_test":
        import pytest
        return int(pytest.main(arguments[1:] or ["tests"]))
    parser = _parser()
    args = parser.parse_args(arguments)
    try:
        if args.command == "status":
            return _status()
        if args.command == "stop":
            try:
                response = send_command({"command": "stop"}, timeout=5)
            except (OSError, EOFError):
                print("Display worker is not running")
                return 0
            if not response.get("ok"):
                raise RuntimeError(response.get("error", "Stop failed"))
            lock = FileLock(str(log_path().parent / "worker.lock"))
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                try:
                    lock.acquire(timeout=0)
                    lock.release()
                    break
                except Timeout:
                    time.sleep(0.05)
            else:
                raise TimeoutError("Display worker did not release its lock after stop")
            state = get_saved_state()
            if state.get("status") == "error":
                raise RuntimeError(state.get("last_error") or "Display worker failed during shutdown")
            print("Display stopped")
            return 0

        if not math.isfinite(args.interval) or args.interval <= 0:
            raise ValueError("--interval must be greater than zero")
        args.list = args.list.expanduser().resolve()
        try:
            send_command({"command": "status"}, timeout=1)
        except (OSError, EOFError, TimeoutError):
            start_worker()
            ensure_worker()
        operation_id = uuid.uuid4().hex
        try:
            response = send_command({
            "command": "show",
            "list_path": str(args.list),
            "interval": args.interval,
            "operation_id": operation_id,
            })
        except (TimeoutError, KeyboardInterrupt):
            try:
                send_command({"command": "cancel", "operation_id": operation_id}, timeout=3)
            except (OSError, EOFError, TimeoutError):
                pass
            raise
        if not response.get("ok"):
            raise RuntimeError(response.get("error", "Unable to show playlist"))
        state = response.get("state", {})
        print(f"Showing {len(state.get('images', []))} image(s)")
        return 0
    except (OSError, EOFError, TimeoutError, RuntimeError, ValueError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        print(f"Log: {log_path()}", file=sys.stderr)
        return 1
