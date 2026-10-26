"""Cold OCR subprocess protocol: image bytes on stdin, one JSON text result on stdout.

No screen capture, sockets, daemonization, file buffer or NInfer calls.
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import sys
import warnings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--engine", choices=("tesseract", "paddle"), default="tesseract"
    )
    args = parser.parse_args()
    try:
        raw = sys.stdin.buffer.read(20 * 1024 * 1024 + 1)
        if not raw or len(raw) > 20 * 1024 * 1024:
            raise ValueError("Image must contain 1 byte through 20 MiB")
        # Keep third-party initialization/download logs off the machine-readable stdout.
        with contextlib.redirect_stdout(sys.stderr):
            import numpy as np
            from PIL import Image, ImageOps

            from .model import Viewport
            from .ocr import detect_text, reading_text

            Image.MAX_IMAGE_PIXELS = 16_000_000
            with warnings.catch_warnings():
                warnings.simplefilter("error", Image.DecompressionBombWarning)
                with Image.open(io.BytesIO(raw)) as opened:
                    if opened.width * opened.height > 16_000_000:
                        raise ValueError("OCR image exceeds 16 million pixels")
                    image = ImageOps.exif_transpose(opened)
                    if "A" in image.getbands() or "transparency" in image.info:
                        rgba = image.convert("RGBA")
                        background = Image.new("RGBA", rgba.size, "white")
                        image = Image.alpha_composite(background, rgba)
                    rgb = image.convert("RGB")
                    frame = np.ascontiguousarray(np.asarray(rgb)[:, :, ::-1])
                    text = reading_text(
                        detect_text(
                            frame, Viewport(rgb.width, rgb.height), engine=args.engine
                        )
                    )
        print(json.dumps({"text": text, "engine": args.engine}, ensure_ascii=False))
        return 0
    except Exception as exc:  # noqa: BLE001 -- subprocess boundary returns one failure, never a partial result
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
