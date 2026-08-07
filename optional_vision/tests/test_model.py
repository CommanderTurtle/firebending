from openai_buffer_vision.model import BoundingBox, Element, Observation, Viewport


def test_normalized_box_accounts_for_viewport_origin() -> None:
    viewport = Viewport(width=1000, height=500, left=-100, top=50)
    box = BoundingBox(x=-100, y=50, width=1000, height=500)

    assert box.normalized(viewport) == {"x1": 0, "y1": 0, "x2": 999, "y2": 999}


def test_observation_has_stable_screen_tag_and_schema_version() -> None:
    viewport = Viewport(width=1920, height=1080)
    element = Element(
        id="E0001",
        role="button",
        name="Submit",
        bbox=BoundingBox(10, 20, 80, 30),
        source="uia",
    )
    payload = Observation(source="uia", viewport=viewport, elements=[element]).to_dict()

    assert payload["schema_version"] == "vision-observation/1"
    assert payload["elements"][0]["bbox_normalized"]["x1"] == 5
    assert '[E0001] button "Submit"' in payload["screen_tag"]
