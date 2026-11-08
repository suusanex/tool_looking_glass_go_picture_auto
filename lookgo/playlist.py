from pathlib import Path
from dataclasses import dataclass

from PIL import Image


class PlaylistError(ValueError):
    """プレイリスト入力エラー。"""


@dataclass(frozen=True)
class PlaylistEntry:
    path: Path
    line: int


def read_playlist_entries(list_path: Path) -> list[PlaylistEntry]:
    list_path = list_path.expanduser().resolve()
    try:
        lines = list_path.read_text(encoding="utf-8-sig").splitlines()
    except OSError as exc:
        raise PlaylistError(f"Cannot read playlist '{list_path}': {exc}") from exc
    except UnicodeError as exc:
        raise PlaylistError(f"Playlist is not valid UTF-8: '{list_path}'") from exc

    images: list[PlaylistEntry] = []
    for number, raw_line in enumerate(lines, 1):
        value = raw_line.strip()
        if not value:
            continue
        item = Path(value).expanduser()
        if not item.is_absolute():
            item = list_path.parent / item
        item = item.resolve()
        if not item.is_file():
            raise PlaylistError(f"Playlist line {number}: image file does not exist: '{item}'")
        if item.suffix.lower() not in {".png", ".jpg", ".jpeg", ".webp"}:
            raise PlaylistError(f"Playlist line {number}: unsupported image type: '{item.suffix}'")
        try:
            with item.open("rb") as stream:
                signature = stream.read(32)
            if item.suffix.lower() == ".png" and not signature.startswith(b"\x89PNG\r\n\x1a\n"):
                raise ValueError("file is not PNG")
            if item.suffix.lower() in {".jpg", ".jpeg"} and not signature.startswith(b"\xff\xd8"):
                raise ValueError("file is not JPEG")
            if item.suffix.lower() == ".webp" and not (signature.startswith(b"RIFF") and signature[8:12] == b"WEBP"):
                raise ValueError("file is not WebP")
            with Image.open(item) as image:
                if item.suffix.lower() in {".png", ".webp"} and image.n_frames != 1:
                    raise ValueError("animated images are not supported")
                image.verify()
        except (OSError, ValueError) as exc:
            raise PlaylistError(f"Playlist line {number}: invalid image file '{item}': {exc}") from exc
        images.append(PlaylistEntry(item, number))
    if not images:
        raise PlaylistError(f"Playlist is empty: '{list_path}'")
    return images


def read_playlist(list_path: Path) -> list[Path]:
    return [entry.path for entry in read_playlist_entries(list_path)]
