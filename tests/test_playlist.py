from pathlib import Path

from PIL import Image
import pytest

from lookgo.playlist import PlaylistError, read_playlist, read_playlist_entries


def test_reads_bom_relative_japanese_spaces_duplicates_and_order(tmp_path: Path) -> None:
    (tmp_path / "画像 A.png").write_bytes(_png((10, 20, 30)))
    (tmp_path / "B.png").write_bytes(_png((40, 50, 60)))
    listing = tmp_path / "playlist.txt"
    listing.write_text("\ufeff画像 A.png\n\nB.png\n画像 A.png\n", encoding="utf-8")

    assert read_playlist(listing) == [tmp_path / "画像 A.png", tmp_path / "B.png", tmp_path / "画像 A.png"]
    assert [entry.line for entry in read_playlist_entries(listing)] == [1, 3, 4]


def test_reports_missing_file_at_playlist_line(tmp_path: Path) -> None:
    listing = tmp_path / "playlist.txt"
    listing.write_text("\nmissing.png\n", encoding="utf-8")
    with pytest.raises(PlaylistError, match="line 2"):
        read_playlist(listing)


def test_reports_corrupt_image_at_playlist_line(tmp_path: Path) -> None:
    (tmp_path / "broken.png").write_bytes(b"\x89PNG\r\n\x1a\nnot an image")
    listing = tmp_path / "playlist.txt"
    listing.write_text("broken.png\n", encoding="utf-8")
    with pytest.raises(PlaylistError, match="line 1"):
        read_playlist(listing)


def test_rejects_empty_list_and_unsupported_extensions(tmp_path: Path) -> None:
    listing = tmp_path / "playlist.txt"
    listing.write_text("\n \n", encoding="utf-8")
    with pytest.raises(PlaylistError, match="empty"):
        read_playlist(listing)

    (tmp_path / "image.bmp").write_bytes(b"bmp")
    listing.write_text("image.bmp", encoding="utf-8")
    with pytest.raises(PlaylistError, match="line 1"):
        read_playlist(listing)


def _png(color: tuple[int, int, int]) -> bytes:
    import io

    output = io.BytesIO()
    Image.new("RGB", (3, 2), color).save(output, format="PNG")
    return output.getvalue()
