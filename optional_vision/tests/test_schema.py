import json
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker

from openai_buffer_vision.model import BoundingBox, Element, Observation, Viewport


def test_generated_observation_matches_repository_contract() -> None:
    root = Path(__file__).resolve().parents[2]
    schema = json.loads(
        (root / "contracts" / "vision-observation.schema.json").read_text(
            encoding="utf-8"
        )
    )
    observation = Observation(
        source="test",
        viewport=Viewport(width=1280, height=720),
        elements=[
            Element(
                id="E0001",
                role="text",
                name="Hello",
                bbox=BoundingBox(20, 30, 90, 24),
                source="test",
            )
        ],
        request_id="req-contract-test",
    ).to_dict()

    Draft202012Validator(schema, format_checker=FormatChecker()).validate(observation)
