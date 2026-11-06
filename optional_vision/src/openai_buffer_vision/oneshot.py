"""Cold image subprocess: bytes on stdin, one JSON result on stdout.

No screen capture, sockets, daemonization, file buffer or NInfer calls.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import sys
from pathlib import Path


def write_png(image, path: str) -> None:
    """A lossless picture on disk: an annotated box print is read for its edges and text."""
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    image.save(path, format="PNG")


def crop_path(base: str, index: int) -> str:
    """One crop per planned name, beside the caller's own filename."""
    stem = Path(base).with_suffix("") if Path(base).suffix.lower() == ".png" else Path(base)
    return f"{stem}-{index}.png"


def parse_boxes(boxes: str) -> list[dict]:
    """One JSON array of named Box answers arrives as an argument; stdin stays the picture."""
    try:
        parsed = json.loads(boxes or "")
    except json.JSONDecodeError as exc:
        raise ValueError(f"--boxes: {exc.msg} at position {exc.pos}") from exc
    if not isinstance(parsed, list) or not 1 <= len(parsed) <= 8:
        raise ValueError("--boxes: a JSON array of 1-8 objects")
    for item in parsed:
        box = item.get("box") if isinstance(item, dict) else None
        if not isinstance(box, dict) or not all(
            isinstance(box.get(edge), (int, float)) and not isinstance(box.get(edge), bool)
            for edge in ("top", "bottom", "left", "right")
        ):
            raise ValueError('--boxes: each item needs {"name":...,"box":{"top":..,"bottom":..,"left":..,"right":..}}')
    return parsed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--operation", choices=("ocr", "cereal", "bbox", "crop"), default="ocr")
    parser.add_argument("--level", type=int, choices=(1, 2, 3), default=1)
    parser.add_argument(
        "--boxes",
        default="",
        help='JSON array for bbox/crop: [{"name":"cta","box":{"top":0.68,"bottom":0.82,'
        '"left":0.09,"right":0.32},"confidence":0.99}]',
    )
    parser.add_argument("--pad", type=float, default=0.0, help="crop: widen each side by this fraction of itself")
    parser.add_argument(
        "--scale",
        type=float,
        default=1.0,
        help="crop: magnify the cut-out by this much, so a coordinate measured over it is finer than one measured over the whole picture",
    )
    parser.add_argument(
        "--out",
        default="",
        help="bbox/crop: write PNG files (one for bbox, <out>-1.png... for crop) instead of base64 on stdout",
    )
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
                elif args.operation in ("bbox", "crop"):
                    from .annotate import caption, crop, draw, png

                    objects = parse_boxes(args.boxes)
                    summary = "".join(f"{index + 1}: {caption(item)}\n" for index, item in enumerate(objects))
                    if args.operation == "bbox":
                        canvas = draw(image, objects)
                        result = {"text": summary, "width": canvas.width, "height": canvas.height}
                        if args.out:
                            write_png(canvas, args.out)
                            result["path"] = args.out
                        else:
                            result["image_base64"] = png(canvas)
                    else:
                        cuts = []
                        for item in objects:
                            cut = crop(image, item["box"], args.pad)
                            if args.scale > 1.0:
                                # The answer is a fraction of whatever the model is shown, so growing this
                                # picture cannot sharpen the arithmetic - it can only make the thing itself
                                # big enough to be placed rather than guessed at. Nothing is drawn and
                                # nothing is inferred here.
                                cut = cut.resize(
                                    (max(1, round(cut.width * args.scale)), max(1, round(cut.height * args.scale))),
                                    Image.LANCZOS,
                                )
                            cuts.append(cut)
                        crops = []
                        for index, (item, cut) in enumerate(zip(objects, cuts), start=1):
                            entry = {"name": str(item.get("name") or "?"), "width": cut.width, "height": cut.height}
                            if args.out:
                                path = crop_path(args.out, index)
                                write_png(cut, path)
                                entry["path"] = path
                            else:
                                entry["image_base64"] = png(cut)
                            crops.append(entry)
                        result = {"text": summary, "width": image.width, "height": image.height, "crops": crops}
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
