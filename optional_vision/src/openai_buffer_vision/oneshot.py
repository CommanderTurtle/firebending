"""Cold image subprocess: bytes on stdin, one JSON result on stdout.

No screen capture, sockets, daemonization, file buffer or NInfer calls.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import sys


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--operation", choices=("ocr", "cereal"), default="ocr")
    parser.add_argument("--level", type=int, choices=(1, 2, 3), default=1)
    parser.add_argument(
        "--style", choices=("ascii", "braille", "block"), default="ascii"
    )
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
            from PIL import Image

            from .image_input import decode_image

            with decode_image(raw) as image:
                if args.operation == "cereal":
                    from .cereal import serialize_image

                    result = serialize_image(image, args.level, args.style)
                else:
                    # Cereal never imports either OCR backend or initializes a model.
                    from .model import Viewport
                    from .ocr import detect_text, reading_text

                    background = Image.new("RGBA", image.size, "white")
                    rgb = Image.alpha_composite(background, image).convert("RGB")
                    frame = np.ascontiguousarray(np.asarray(rgb)[:, :, ::-1])
                    text = reading_text(
                        detect_text(
                            frame, Viewport(rgb.width, rgb.height), engine=args.engine
                        )
                    )
                    result = {"text": text, "engine": args.engine}
        print(json.dumps(result, ensure_ascii=False))
        return 0
    except Exception as exc:  # noqa: BLE001 -- subprocess boundary returns one failure, never a partial result
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
