from openai_buffer_vision.model import BoundingBox, Element, Viewport
from openai_buffer_vision.pipeline import deduplicate


def test_deduplicate_prefers_structured_element_over_cv_region() -> None:
    viewport = Viewport(width=800, height=600)
    uia = Element("button", BoundingBox(100, 100, 120, 40), "uia", name="Save")
    cv = Element("visual-region", BoundingBox(102, 102, 116, 36), "opencv")

    result = deduplicate([cv, uia], viewport)

    assert [(item.id, item.source) for item in result] == [("E0001", "uia")]


def test_deduplicate_drops_offscreen_elements() -> None:
    viewport = Viewport(width=800, height=600)
    outside = Element("text", BoundingBox(-500, -500, 20, 20), "uia", name="Hidden")

    assert deduplicate([outside], viewport) == []
