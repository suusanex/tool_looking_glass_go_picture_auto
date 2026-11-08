from __future__ import annotations

from dataclasses import dataclass
import json
import logging
from pathlib import Path
import re
import urllib.error
import urllib.request
import uuid

from .settings import DEPTHINESS, DEPTH_LOCATION, FOCUS


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

    def present(self, image_path: Path, duration_ms: int) -> str:
        if self.orchestration is None or self.device is None:
            raise RuntimeError("Bridge renderer is not connected")
        self._available_go()
        name = f"lookgo-{uuid.uuid4().hex}"
        playlist = {"orchestration": self.orchestration, "name": name}
        self._put("instance_playlist", {**playlist, "loop": True})
        self.owned_playlists.add(name)
        uri = str(image_path.resolve())
        self._put("insert_playlist_entry", {
            **playlist,
            "id": 0,
            "index": 0,
            "uri": uri,
            "focus": FOCUS,
            "rows": self.device.rows,
            "cols": self.device.columns,
            "aspect": self.device.aspect,
            "view_count": self.device.view_count,
            "durationMS": max(1, int(duration_ms)),
            "crop_pos_x": 0,
            "crop_pos_y": 0,
            "isRGBD": 1,
            "depth_loc": DEPTH_LOCATION,
            "depth_inversion": 0,
            "chroma_depth": 0,
            "depthiness": DEPTHINESS,
            "zoom": 1,
        })
        self._put("play_playlist", {**playlist, "head_index": self.device.index})
        self._put("show_window", {
            "orchestration": self.orchestration,
            "show_window": True,
            "head_index": self.device.index,
        })

        previous = self.active_playlist
        self.active_playlist = name
        if previous and previous != name:
            self._delete_playlist(previous)
        logging.info("Displayed RGBD image through Bridge playlist %s", name)
        return name

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
            if self.device:
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
        if errors:
            raise RuntimeError("Bridge cleanup failed: " + "; ".join(map(str, errors)))
