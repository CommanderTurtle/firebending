from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any


@dataclass(frozen=True, slots=True)
class BoundingBox:
    x: float
    y: float
    width: float
    height: float

    @property
    def x2(self) -> float:
        return self.x + self.width

    @property
    def y2(self) -> float:
        return self.y + self.height

    @property
    def area(self) -> float:
        return max(0.0, self.width) * max(0.0, self.height)

    def intersection(self, other: BoundingBox) -> float:
        width = max(0.0, min(self.x2, other.x2) - max(self.x, other.x))
        height = max(0.0, min(self.y2, other.y2) - max(self.y, other.y))
        return width * height

    def iou(self, other: BoundingBox) -> float:
        intersection = self.intersection(other)
        union = self.area + other.area - intersection
        return intersection / union if union else 0.0

    def visible_in(self, viewport: Viewport) -> bool:
        return self.intersection(viewport.bounds) > 0.0

    def normalized(self, viewport: Viewport) -> dict[str, int]:
        def scale(value: float, origin: float, span: float) -> int:
            return max(0, min(999, round((value - origin) / span * 999)))

        return {
            "x1": scale(self.x, viewport.left, viewport.width),
            "y1": scale(self.y, viewport.top, viewport.height),
            "x2": scale(self.x2, viewport.left, viewport.width),
            "y2": scale(self.y2, viewport.top, viewport.height),
        }

    def to_dict(self) -> dict[str, float]:
        return {
            "x": self.x,
            "y": self.y,
            "width": self.width,
            "height": self.height,
            "x2": self.x2,
            "y2": self.y2,
        }


@dataclass(frozen=True, slots=True)
class Viewport:
    width: float
    height: float
    left: float = 0.0
    top: float = 0.0
    monitor: int = 1
    device_pixel_ratio: float = 1.0

    @property
    def bounds(self) -> BoundingBox:
        return BoundingBox(self.left, self.top, self.width, self.height)

    def to_dict(self) -> dict[str, float | int]:
        return {
            "width": self.width,
            "height": self.height,
            "left": self.left,
            "top": self.top,
            "monitor": self.monitor,
            "device_pixel_ratio": self.device_pixel_ratio,
        }


@dataclass(slots=True)
class Element:
    role: str
    bbox: BoundingBox
    source: str
    name: str = ""
    state: dict[str, Any] = field(default_factory=dict)
    metadata: dict[str, Any] = field(default_factory=dict)
    id: str = ""

    def to_dict(self, viewport: Viewport) -> dict[str, Any]:
        result: dict[str, Any] = {
            "id": self.id,
            "role": self.role,
            "name": self.name,
            "bbox": self.bbox.to_dict(),
            "bbox_normalized": self.bbox.normalized(viewport),
            "source": self.source,
        }
        if self.state:
            result["state"] = self.state
        if self.metadata:
            result["metadata"] = self.metadata
        return result


@dataclass(slots=True)
class Observation:
    source: str
    viewport: Viewport
    elements: list[Element]
    request_id: str | None = None
    window: dict[str, Any] = field(default_factory=dict)
    metadata: dict[str, Any] = field(default_factory=dict)
    captured_at: str = field(
        default_factory=lambda: (
            datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        )
    )

    def screen_tag(self) -> str:
        lines: list[str] = []
        for element in self.elements:
            label = f' "{element.name}"' if element.name else ""
            lines.append(
                f"[{element.id}] {element.role}{label} @ "
                f"({round(element.bbox.x)},{round(element.bbox.y)})-"
                f"({round(element.bbox.x2)},{round(element.bbox.y2)}) [{element.source}]"
            )
        return "\n".join(lines)

    def to_dict(self) -> dict[str, Any]:
        result: dict[str, Any] = {
            "schema_version": "vision-observation/1",
            "source": self.source,
            "captured_at": self.captured_at,
            "coordinate_space": "physical-screen",
            "viewport": self.viewport.to_dict(),
            "elements": [element.to_dict(self.viewport) for element in self.elements],
            "screen_tag": self.screen_tag(),
        }
        if self.request_id:
            result["request_id"] = self.request_id
        if self.window:
            result["window"] = self.window
        if self.metadata:
            result["metadata"] = self.metadata
        return result
