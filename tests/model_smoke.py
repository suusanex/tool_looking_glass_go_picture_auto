"""初回モデル取得と自動選択デバイスでの推定を確認する手動スモークテスト。"""

import numpy as np
from PIL import Image

from lookgo.imaging import DepthEstimator


def test_depth_anything_small_model_download_and_inference() -> None:
    estimator = DepthEstimator()
    image = Image.new("RGB", (256, 256), (120, 80, 40))
    depth = estimator.estimate(image)

    assert estimator.device in {"cpu", "cuda"}
    assert depth.size == image.size
    assert np.asarray(depth).dtype == np.uint8
