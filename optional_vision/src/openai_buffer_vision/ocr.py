from __future__ import annotations

from collections import defaultdict

import numpy as np

from .model import BoundingBox, Element, Viewport


def detect_text(
    image: np.ndarray,
    viewport: Viewport,
    minimum_confidence: float = 45.0,
) -> list[Element]:
    try:
        import pytesseract
        from pytesseract import Output
    except ImportError as exc:
        raise RuntimeError(
            "OCR support is not installed; use the 'ocr' or 'all' extra"
        ) from exc

    try:
        data = pytesseract.image_to_data(image, output_type=Output.DICT)
    except pytesseract.TesseractNotFoundError as exc:
        raise RuntimeError(
            "Tesseract is not installed or its executable is not on PATH"
        ) from exc

    groups: dict[tuple[int, int, int], list[int]] = defaultdict(list)
    for index, text in enumerate(data["text"]):
        confidence = float(data["conf"][index])
        if text.strip() and confidence >= minimum_confidence:
            groups[
                (
                    data["block_num"][index],
                    data["par_num"][index],
                    data["line_num"][index],
                )
            ].append(index)

    elements: list[Element] = []
    for indices in groups.values():
        left = min(data["left"][index] for index in indices)
        top = min(data["top"][index] for index in indices)
        right = max(data["left"][index] + data["width"][index] for index in indices)
        bottom = max(data["top"][index] + data["height"][index] for index in indices)
        name = " ".join(data["text"][index].strip() for index in indices)
        confidence = sum(float(data["conf"][index]) for index in indices) / len(indices)
        elements.append(
            Element(
                role="text",
                name=name,
                bbox=BoundingBox(
                    viewport.left + left, viewport.top + top, right - left, bottom - top
                ),
                source="tesseract",
                metadata={"confidence": round(confidence, 2)},
            )
        )
    return elements
