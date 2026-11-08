import io

import numpy as np
from PIL import Image
import pytest

from lookgo.imaging import cache_key, encode_rgbd, normalize_depth, prepare_rgb


def test_cache_key_covers_input_revision_and_transform_options() -> None:
    assert cache_key(b"one", 0.5625) != cache_key(b"two", 0.5625)
    assert cache_key(b"one", 0.5625) != cache_key(b"one", 1.0)
    assert cache_key(b"one", 0.5625, 512) != cache_key(b"one", 0.5625, 1024)


def test_normalize_depth_maps_near_to_white_far_to_black_and_handles_flat_values() -> None:
    normalized = np.asarray(normalize_depth(np.array([[0.0, 1.0, 2.0]], dtype=np.float32)))
    assert normalized.tolist() == [[0, 127, 255]]
    assert np.asarray(normalize_depth(np.full((2, 3), 5.0, dtype=np.float32))).tolist() == [
        [128, 128, 128], [128, 128, 128]
    ]


@pytest.mark.parametrize("values", [np.array([[np.nan]]), np.array([[np.inf]]), np.array([[np.nan, 1.0]])])
def test_normalize_depth_rejects_nonfinite_values(values: np.ndarray) -> None:
    with pytest.raises(ValueError, match="non-finite"):
        normalize_depth(values)


def test_prepare_rgb_applies_exif_alpha_black_and_letterbox() -> None:
    source = Image.new("RGBA", (2, 4), (240, 0, 0, 128))
    exif = Image.Exif()
    exif[274] = 6
    encoded = io.BytesIO()
    source.save(encoded, format="PNG", exif=exif)

    output = prepare_rgb(encoded.getvalue(), target_aspect=1.0, max_edge=4)

    assert output.size == (4, 4)
    pixels = np.asarray(output)
    assert pixels[0, 0].tolist() == [0, 0, 0]
    assert pixels[1, 1].tolist() == [120, 0, 0]


def test_rgbd_packs_rgb_left_and_grayscale_depth_right() -> None:
    rgb = Image.new("RGB", (2, 1), (20, 30, 40))
    depth = Image.fromarray(np.array([[0, 255]], dtype=np.uint8), mode="L")
    with Image.open(io.BytesIO(encode_rgbd(rgb, depth))) as rgbd:
        assert rgbd.mode == "RGBA"
        assert rgbd.size == (4, 1)
        assert list(rgbd.getdata()) == [
            (20, 30, 40, 255), (20, 30, 40, 255),
            (0, 0, 0, 255), (255, 255, 255, 255),
        ]
