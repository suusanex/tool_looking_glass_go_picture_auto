from __future__ import annotations

import hashlib
import io
import json
import numpy as np
from PIL import Image, ImageOps

from .settings import DEPTHINESS, DEPTH_LOCATION, FOCUS, MAX_IMAGE_EDGE, MODEL_ID, MODEL_REVISION, TRANSFORM_VERSION, model_directory


def cache_key(image_bytes: bytes, target_aspect: float, max_edge: int = MAX_IMAGE_EDGE) -> str:
    digest = hashlib.sha256(image_bytes)
    options = json.dumps(
        [MODEL_REVISION, TRANSFORM_VERSION, float(target_aspect), max_edge, DEPTHINESS, DEPTH_LOCATION, FOCUS],
        separators=(",", ":"),
    ).encode()
    digest.update(options)
    return digest.hexdigest()


def prepare_rgb(image_bytes: bytes, target_aspect: float, max_edge: int = MAX_IMAGE_EDGE) -> Image.Image:
    with Image.open(io.BytesIO(image_bytes)) as source:
        rgb = ImageOps.exif_transpose(source).convert("RGBA")
    # RGBAを黒背景に合成し、元画像の縦横比を保って表示比率のキャンバスへ収める。
    background = Image.new("RGBA", rgb.size, (0, 0, 0, 255))
    background.alpha_composite(rgb)
    image = background.convert("RGB")
    if max(image.size) > max_edge:
        ratio = max_edge / max(image.size)
        image = image.resize((max(1, round(image.width * ratio)), max(1, round(image.height * ratio))), Image.Resampling.LANCZOS)

    if not np.isfinite(target_aspect) or target_aspect <= 0:
        raise ValueError(f"Invalid display aspect ratio: {target_aspect}")
    canvas_width = max(2, max_edge if target_aspect >= 1 else round(max_edge * target_aspect))
    canvas_height = max(2, round(canvas_width / target_aspect))
    scale = min(canvas_width / image.width, canvas_height / image.height)
    size = (max(1, round(image.width * scale)), max(1, round(image.height * scale)))
    fitted = image.resize(size, Image.Resampling.LANCZOS)
    canvas = Image.new("RGB", (canvas_width, canvas_height), (0, 0, 0))
    canvas.paste(fitted, ((canvas_width - fitted.width) // 2, (canvas_height - fitted.height) // 2))
    return canvas


def normalize_depth(depth: np.ndarray) -> Image.Image:
    # Depth Anything V2 は逆深度で、大きい値ほど手前。depth_inversion=0 では白を手前、黒を奥にする。
    values = np.asarray(depth, dtype=np.float32)
    finite = np.isfinite(values)
    if not finite.all():
        raise ValueError("Depth prediction contains non-finite values")
    if not finite.any():
        raise ValueError("Depth prediction contains no values")
    low = float(values[finite].min())
    high = float(values[finite].max())
    if high - low < 1e-6:
        return Image.new("L", (values.shape[-1], values.shape[-2]), 128)
    normalized = np.zeros(values.shape, dtype=np.float32)
    normalized[finite] = (values[finite] - low) / (high - low)
    pixels = np.clip(normalized * 255.0, 0, 255).astype(np.uint8)
    return Image.fromarray(pixels, mode="L")


def encode_rgbd(rgb: Image.Image, depth: Image.Image) -> bytes:
    if depth.size != rgb.size:
        depth = depth.resize(rgb.size, Image.Resampling.BILINEAR)
    rgbd = Image.new("RGBA", (rgb.width * 2, rgb.height))
    rgbd.paste(rgb.convert("RGB"), (0, 0))
    rgbd.paste(depth.convert("L"), (rgb.width, 0))
    output = io.BytesIO()
    rgbd.save(output, format="PNG", optimize=True)
    return output.getvalue()


class DepthEstimator:
    def __init__(self) -> None:
        import torch
        from transformers import AutoImageProcessor, AutoModelForDepthEstimation

        self.torch = torch
        self.processor = AutoImageProcessor.from_pretrained(
            MODEL_ID, revision=MODEL_REVISION, cache_dir=model_directory(),
        )
        self.model = AutoModelForDepthEstimation.from_pretrained(
            MODEL_ID,
            revision=MODEL_REVISION,
            use_safetensors=True,
            cache_dir=model_directory(),
        )
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.model.to(self.device).eval()

    def estimate(self, image: Image.Image) -> Image.Image:
        inputs = self.processor(images=image, return_tensors="pt")
        inputs = {name: tensor.to(self.device) for name, tensor in inputs.items()}
        with self.torch.inference_mode():
            prediction = self.model(**inputs).predicted_depth
            prediction = self.torch.nn.functional.interpolate(
                prediction.unsqueeze(1),
                size=(image.height, image.width),
                mode="bicubic",
                align_corners=False,
            ).squeeze(1).squeeze(0)
        return normalize_depth(prediction.detach().float().cpu().numpy())


def convert_to_rgbd(image_bytes: bytes, target_aspect: float, estimator: DepthEstimator, max_edge: int = MAX_IMAGE_EDGE) -> bytes:
    rgb = prepare_rgb(image_bytes, target_aspect, max_edge)
    depth = estimator.estimate(rgb)
    return encode_rgbd(rgb, depth)
