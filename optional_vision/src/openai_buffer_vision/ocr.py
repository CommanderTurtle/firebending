from __future__ import annotations

from collections import defaultdict

import numpy as np

from .model import BoundingBox, Element, Viewport


def detect_text(
    image: np.ndarray,
    viewport: Viewport,
    minimum_confidence: float = 45.0,
    engine: str = "tesseract",
) -> list[Element]:
    if engine == "paddle":
        try:
            return _paddle_text(image, viewport, minimum_confidence)
        except RuntimeError:
            raise
        except Exception as exc:
            # Match the optional producer's warning-only fallback contract.
            raise RuntimeError(f"Paddle OCR failed: {exc}") from exc
    if engine != "tesseract":
        raise ValueError("OCR engine must be tesseract or paddle")
    try:
        import pytesseract
        from pytesseract import Output
    except ImportError as exc:
        raise RuntimeError(
            "OCR support is not installed; use the 'ocr' or 'all' extra"
        ) from exc

    try:
        # CaptureFrame is BGR (as used by OpenCV); Tesseract expects RGB.
        rgb = image[:, :, ::-1] if image.ndim == 3 and image.shape[2] == 3 else image
        data = pytesseract.image_to_data(rgb, output_type=Output.DICT)
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


def _paddle_text(
    image: np.ndarray, viewport: Viewport, minimum_confidence: float
) -> list[Element]:
    try:
        from rapidocr import LangDet, LangRec, ModelType, OCRVersion, RapidOCR
    except ImportError as exc:
        raise RuntimeError(
            "Paddle OCR is not installed; sync the optional 'paddle' extra"
        ) from exc

    # Same upright-screen PP-OCRv6 small profile as the consolidated browser work.
    # One local instance per call; no warm server/socket, GPU provider or global cache.
    engine = RapidOCR(
        params={
            "Global.log_level": "error",
            "Global.use_cls": False,
            "Global.text_score": minimum_confidence / 100.0,
            "Det.ocr_version": OCRVersion.PPOCRV6,
            "Det.model_type": ModelType.SMALL,
            "Det.lang_type": LangDet.EN,
            "Det.limit_type": "min",
            "Det.limit_side_len": 320,
            "Det.box_thresh": 0.5,
            "Det.thresh": 0.3,
            "Det.unclip_ratio": 1.6,
            "Rec.ocr_version": OCRVersion.PPOCRV6,
            "Rec.model_type": ModelType.SMALL,
            "Rec.lang_type": LangRec.EN,
            "EngineConfig.onnxruntime.use_cuda": False,
            "EngineConfig.onnxruntime.use_dml": False,
            "EngineConfig.onnxruntime.use_cann": False,
            "EngineConfig.onnxruntime.intra_op_num_threads": 4,
        }
    )
    result = engine(image)
    elements = []
    if result.txts is None:
        return elements
    for box, text, score in zip(result.boxes, result.txts, result.scores, strict=True):
        if not text.strip() or float(score) * 100 < minimum_confidence:
            continue
        xs, ys = [float(point[0]) for point in box], [float(point[1]) for point in box]
        elements.append(
            Element(
                role="text",
                name=text.strip(),
                source="paddle",
                bbox=BoundingBox(
                    viewport.left + min(xs),
                    viewport.top + min(ys),
                    max(xs) - min(xs),
                    max(ys) - min(ys),
                ),
                metadata={"confidence": round(float(score) * 100, 2)},
            )
        )
    return elements


def reading_text(elements: list[Element]) -> str:
    """Reconstruct rows/column gaps from line geometry without dropping recognized text."""
    from statistics import median

    rows: list[list[Element]] = []
    for item in sorted(
        elements, key=lambda value: value.bbox.y + value.bbox.height / 2
    ):
        if rows:
            row = rows[-1]
            center = median(entry.bbox.y + entry.bbox.height / 2 for entry in row)
            height = median(entry.bbox.height for entry in row)
            if (
                abs(item.bbox.y + item.bbox.height / 2 - center)
                < min(height, item.bbox.height) / 2
            ):
                row.append(item)
                continue
        rows.append([item])
    if not rows:
        return ""
    char_width = max(
        1.0, median(item.bbox.width / max(1, len(item.name)) for item in elements)
    )
    left = min(item.bbox.x for item in elements)
    line_height = median(item.bbox.height for item in elements)
    lines = []
    previous_bottom = None
    for row in rows:
        row.sort(key=lambda item: item.bbox.x)
        top = min(item.bbox.y for item in row)
        if previous_bottom is not None and top - previous_bottom > line_height:
            lines.append("")
        pieces, last_right = [], left
        for item in row:
            # Bound indentation memory for malformed model geometry, not text length.
            gap = min(
                4096,
                max(
                    0 if not pieces else 1,
                    round((item.bbox.x - last_right) / char_width),
                ),
            )
            pieces.append(" " * gap + item.name)
            last_right = item.bbox.x2
        lines.append("".join(pieces).rstrip())
        previous_bottom = max(item.bbox.y2 for item in row)
    return "\n".join(lines)
