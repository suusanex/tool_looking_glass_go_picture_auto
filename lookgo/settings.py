from pathlib import Path
import os

APP_NAME = "lookgo"
MODEL_ID = "depth-anything/Depth-Anything-V2-Small-hf"
MODEL_REVISION = "5426e4f0f36572d16453bbda7a8389317b1bef99"
TRANSFORM_VERSION = "rgbd-fit-1"
DEPTHINESS = 1.0
FOCUS = 0.0
DEPTH_LOCATION = 2
MAX_IMAGE_EDGE = 2048


def data_directory() -> Path:
    root = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
    path = root / APP_NAME
    path.mkdir(parents=True, exist_ok=True)
    return path


def cache_directory() -> Path:
    path = data_directory() / "cache"
    path.mkdir(parents=True, exist_ok=True)
    return path


def model_directory() -> Path:
    path = data_directory() / "models"
    path.mkdir(parents=True, exist_ok=True)
    return path


def state_path() -> Path:
    return data_directory() / "state.json"


def log_path() -> Path:
    return data_directory() / "lookgo.log"
