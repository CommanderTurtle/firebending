import numpy as np
import pytesseract
from openai_buffer_vision.model import Viewport
from openai_buffer_vision.ocr import detect_text


def test_detect_text_groups_words_on_the_same_line(monkeypatch) -> None:
    data = {
        "text": ["Hello", "world"],
        "conf": ["92", "88"],
        "block_num": [1, 1],
        "par_num": [1, 1],
        "line_num": [1, 1],
        "left": [10, 55],
        "top": [20, 20],
        "width": [40, 45],
        "height": [18, 18],
    }
    monkeypatch.setattr(pytesseract, "image_to_data", lambda image, output_type: data)

    elements = detect_text(
        np.zeros((100, 200, 3), dtype=np.uint8), Viewport(width=200, height=100)
    )

    assert len(elements) == 1
    assert elements[0].name == "Hello world"
    assert elements[0].bbox.to_dict() == {
        "x": 10.0,
        "y": 20.0,
        "width": 90,
        "height": 18,
        "x2": 100.0,
        "y2": 38.0,
    }


def test_capture_bgr_is_converted_to_rgb_without_mutating_source(monkeypatch):
    seen = []
    monkeypatch.setattr(
        pytesseract,
        "image_to_data",
        lambda image, output_type: seen.append(image.copy()) or {"text": []},
    )
    image = np.array([[[10, 20, 30]]], dtype=np.uint8)
    assert detect_text(image, Viewport(1, 1)) == []
    assert seen[0].tolist() == [[[30, 20, 10]]]
    assert image.tolist() == [[[10, 20, 30]]]
