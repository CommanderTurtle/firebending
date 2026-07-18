from __future__ import annotations

import numpy as np

from .model import BoundingBox, Element, Viewport


def detect_regions(
    image: np.ndarray,
    viewport: Viewport,
    min_area: float = 180.0,
    max_area_ratio: float = 0.35,
    limit: int = 250,
) -> list[Element]:
    try:
        import cv2
    except ImportError as exc:
        raise RuntimeError(
            "OpenCV support is not installed; use the 'cv' or 'all' extra"
        ) from exc

    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    edges = cv2.Canny(cv2.GaussianBlur(gray, (3, 3), 0), 55, 160)
    contours, _ = cv2.findContours(edges, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    maximum_area = viewport.width * viewport.height * max_area_ratio
    elements: list[Element] = []

    for contour in contours:
        x, y, width, height = cv2.boundingRect(contour)
        area = float(width * height)
        if area < min_area or area > maximum_area or width < 8 or height < 8:
            continue
        elements.append(
            Element(
                role="visual-region",
                bbox=BoundingBox(viewport.left + x, viewport.top + y, width, height),
                source="opencv",
                metadata={"contour_area": float(cv2.contourArea(contour))},
            )
        )

    elements.sort(key=lambda item: (item.bbox.y, item.bbox.x, item.bbox.area))
    return elements[:limit]
