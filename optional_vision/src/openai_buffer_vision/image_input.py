"""Bounded image decoding shared by the cold local image helpers."""

from __future__ import annotations

import io
import warnings

from PIL import Image, ImageOps

MAX_PIXELS = 16_000_000
MAX_BYTES = 20 * 1024 * 1024


def decode_image(raw: bytes) -> Image.Image:
    """Decode the first frame with its full canvas and alpha; never crop/trim."""
    if not raw or len(raw) > MAX_BYTES:
        raise ValueError("Image must contain 1 byte through 20 MiB")
    Image.MAX_IMAGE_PIXELS = MAX_PIXELS
    with warnings.catch_warnings():
        warnings.simplefilter("error", Image.DecompressionBombWarning)
        with Image.open(io.BytesIO(raw)) as opened:
            if opened.width * opened.height > MAX_PIXELS:
                raise ValueError("Decoded image exceeds 16 million pixels")
            return ImageOps.exif_transpose(opened).convert("RGBA")
