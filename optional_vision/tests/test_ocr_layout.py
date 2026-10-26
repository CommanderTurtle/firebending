"""Pure geometry fixtures; these do not initialize or import an OCR backend."""

import pytest
from openai_buffer_vision.model import BoundingBox, Element
from openai_buffer_vision.ocr import reading_text


def line(name, x, y, width, height=10):
    return Element("text", BoundingBox(x, y, width, height), "fixture", name=name)


def test_empty_layout_returns_empty_text():
    assert reading_text([]) == ""


def test_rows_and_column_gaps_preserve_all_recognized_text():
    text = reading_text(
        [line("right", 80, 0, 50), line("left", 0, 0, 40), line("below", 20, 20, 50)]
    )
    assert text == "left    right\n  below"


def test_large_vertical_gap_adds_one_separator_not_a_full_blank_screen():
    text = reading_text([line("first", 0, 0, 50), line("last", 0, 10000, 40)])
    assert text == "first\n\nlast"


@pytest.mark.parametrize("source", ["tesseract", "paddle"])
def test_layout_is_engine_independent_and_does_not_truncate_text(source):
    content = "word " * 10000
    element = Element("text", BoundingBox(100, 100, 50000, 10), source, name=content)
    assert reading_text([element]) == content.rstrip()
