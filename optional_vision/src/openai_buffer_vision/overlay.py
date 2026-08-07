from __future__ import annotations

from pathlib import Path

import numpy as np

from .model import Element, Viewport


def write_overlay(
    image: np.ndarray, viewport: Viewport, elements: list[Element], output: Path
) -> None:
    try:
        import cv2
    except ImportError as exc:
        raise RuntimeError(
            "Overlay rendering requires the 'cv' or 'all' extra"
        ) from exc

    canvas = image.copy()
    for element in elements:
        x1 = round(element.bbox.x - viewport.left)
        y1 = round(element.bbox.y - viewport.top)
        x2 = round(element.bbox.x2 - viewport.left)
        y2 = round(element.bbox.y2 - viewport.top)
        cv2.rectangle(canvas, (x1, y1), (x2, y2), (28, 220, 255), 2)
        cv2.putText(
            canvas,
            element.id,
            (x1, max(14, y1 - 4)),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.45,
            (28, 220, 255),
            1,
            cv2.LINE_AA,
        )

    output.parent.mkdir(parents=True, exist_ok=True)
    if not cv2.imwrite(str(output), canvas):
        raise RuntimeError(f"Failed to write overlay to {output}")
