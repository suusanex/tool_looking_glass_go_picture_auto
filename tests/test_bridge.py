from __future__ import annotations

import json
from pathlib import Path

import pytest

from lookgo.bridge import DisplayDevice, RestBridge


def wrap(name: str, value, kind: str = "WSTRING") -> dict:
    return {"name": name, "type": kind, "value": value}


def response(endpoint: str, payload, status: str = "Completion") -> dict:
    return {
        "name": endpoint,
        "payload": payload,
        "status": wrap("status", status),
    }


def available_devices(go_index: int = 7, go_hwid: str = "LKG-GO") -> dict:
    third_party = {
        "hardwareVersion": wrap("hardwareVersion", "thirdparty"),
        "state": wrap("state", "ok"),
        "index": wrap("index", 0, "UNSIGNED_INT"),
        "hwid": wrap("hwid", "OTHER"),
    }
    go = {
        "hardwareVersion": wrap("hardwareVersion", "go_p"),
        "state": wrap("state", "ok"),
        "index": wrap("index", go_index, "UNSIGNED_INT"),
        "hwid": wrap("hwid", go_hwid),
        "defaultQuilt": wrap(
            "defaultQuilt",
            json.dumps({"quiltAspect": 0.5625, "quiltX": 4092, "quiltY": 4092, "tileX": 11, "tileY": 6}),
        ),
    }
    return response("available_output_devices", wrap("payload", {
        "0": wrap("0", third_party, "VARIANT_MAP"),
        "1": wrap("1", go, "VARIANT_MAP"),
    }, "VARIANT_MAP"))


def test_connect_checks_bridge_version_and_selects_go_by_hardware_type(monkeypatch):
    renderer = RestBridge()
    requests = []
    token = "{unique-token}"

    def fake_http(method, endpoint="", body=None):
        requests.append((method, endpoint, body))
        if endpoint == "":
            return None
        if endpoint == "bridge_version":
            return response(endpoint, wrap("payload", "2.6.3"))
        if endpoint == "enter_orchestration":
            assert body and body["name"].startswith("lookgo-")
            return response(endpoint, wrap("payload", token))
        if endpoint == "available_output_devices":
            assert body == {"orchestration": token}
            return available_devices()
        raise AssertionError(endpoint)

    monkeypatch.setattr(renderer, "_http", fake_http)
    device = renderer.connect()

    assert device == DisplayDevice(7, "LKG-GO", 0.5625, 6, 11, 66)
    assert renderer.orchestration == token
    assert requests[0] == ("GET", "", None)


@pytest.mark.parametrize("status", ["Pending", "Failure", "UnknownOrchestration"])
def test_only_completion_is_accepted(status: str):
    with pytest.raises(RuntimeError, match=status):
        RestBridge._completion(response("play_playlist", wrap("payload", {}), status), "play_playlist")


def test_present_creates_and_plays_new_playlist_before_deleting_old(tmp_path: Path, monkeypatch):
    renderer = RestBridge()
    renderer.orchestration = "{orch}"
    renderer.device = DisplayDevice(2, "LKG-E04851", 0.5625, 6, 11, 66)
    renderer.active_playlist = "lookgo-old"
    renderer.owned_playlists.add("lookgo-old")
    monkeypatch.setattr(renderer, "_available_go", lambda: None)
    calls = []

    def fake_put(endpoint: str, body: dict):
        calls.append((endpoint, body))
        if endpoint == "available_output_devices":
            return available_devices()
        return response(endpoint, wrap("payload", {}))

    monkeypatch.setattr(renderer, "_put", fake_put)
    image = tmp_path / "RGBD 日本語.png"
    image.write_bytes(b"file")
    name = renderer.present(image, duration_ms=15_000)

    assert name.startswith("lookgo-")
    assert [endpoint for endpoint, _ in calls] == [
        "instance_playlist", "insert_playlist_entry", "play_playlist", "show_window", "delete_playlist"
    ]
    insert = calls[1][1]
    assert insert["uri"] == str(image.resolve())
    assert insert["id"] == 0 and insert["index"] == 0
    assert (insert["rows"], insert["cols"], insert["view_count"]) == (6, 11, 66)
    assert insert["aspect"] == 0.5625 and insert["depth_loc"] == 2 and insert["isRGBD"] == 1
    assert calls[2][1]["head_index"] == 2
    assert calls[3][1] == {"orchestration": "{orch}", "show_window": True, "head_index": 2}
    assert calls[4][1] == {"orchestration": "{orch}", "name": "lookgo-old", "loop": True}
    assert renderer.active_playlist == name
    assert renderer.owned_playlists == {name}


def test_present_failure_does_not_delete_previous_playlist(tmp_path: Path, monkeypatch):
    renderer = RestBridge()
    renderer.orchestration = "{orch}"
    renderer.device = DisplayDevice(2, "LKG-E04851", 0.5625, 6, 11, 66)
    renderer.active_playlist = "lookgo-old"
    renderer.owned_playlists.add("lookgo-old")
    monkeypatch.setattr(renderer, "_available_go", lambda: None)

    def fail_play(endpoint: str, _body: dict):
        if endpoint == "play_playlist":
            raise RuntimeError("Bridge request play_playlist returned status Failure")
        return response(endpoint, wrap("payload", {}))

    monkeypatch.setattr(renderer, "_put", fail_play)
    with pytest.raises(RuntimeError, match="play_playlist"):
        renderer.present(tmp_path / "image.png", duration_ms=10_000)
    assert renderer.active_playlist == "lookgo-old"
    assert "lookgo-old" in renderer.owned_playlists


def test_close_hides_go_deletes_only_owned_playlists_and_exits(monkeypatch):
    renderer = RestBridge()
    renderer.orchestration = "{orch}"
    renderer.device = DisplayDevice(2, "LKG-E04851", 0.5625, 6, 11, 66)
    renderer.active_playlist = "lookgo-owned"
    renderer.owned_playlists = {"lookgo-owned", "lookgo-staging"}
    calls = []
    monkeypatch.setattr(renderer, "_put", lambda endpoint, body: calls.append((endpoint, body)) or response(endpoint, wrap("payload", {})))

    renderer.close()

    assert calls[0] == ("show_window", {"orchestration": "{orch}", "show_window": False, "head_index": 2})
    assert [endpoint for endpoint, _ in calls].count("delete_playlist") == 2
    assert calls[-1] == ("exit_orchestration", {"orchestration": "{orch}"})
    assert renderer.orchestration is None


def test_http_uses_utf8_json_put_body(monkeypatch):
    renderer = RestBridge()
    observed = {}

    class FakeResponse:
        status = 200

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def read(self):
            return json.dumps(response("enter_orchestration", wrap("payload", "token"))).encode("utf-8")

    def fake_open(request, timeout):
        observed["method"] = request.get_method()
        observed["body"] = json.loads(request.data.decode("utf-8"))
        observed["timeout"] = timeout
        return FakeResponse()

    monkeypatch.setattr("urllib.request.urlopen", fake_open)
    result = renderer._http("PUT", "enter_orchestration", {"name": "Look Glass 日本語"})

    assert observed == {"method": "PUT", "body": {"name": "Look Glass 日本語"}, "timeout": 10.0}
    assert result["status"]["value"] == "Completion"
