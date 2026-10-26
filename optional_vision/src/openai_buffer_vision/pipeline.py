from __future__ import annotations

from dataclasses import dataclass, field
from time import perf_counter
from typing import Any

from .capture import CaptureFrame, capture_screen
from .model import Element, Observation, Viewport

SOURCE_PRIORITY = {"uia": 0, "tesseract": 1, "paddle": 1, "opencv": 2}


@dataclass(slots=True)
class PipelineOptions:
    monitor: int = 1
    region: tuple[int, int, int, int] | None = None
    use_uia: bool = True
    use_cv: bool = False
    use_ocr: bool = False
    include_containers: bool = False
    max_uia_nodes: int = 300
    request_id: str | None = None
    ocr_engine: str = "tesseract"


@dataclass(slots=True)
class PipelineResult:
    observation: Observation
    frame: CaptureFrame
    warnings: list[str] = field(default_factory=list)


def deduplicate(elements: list[Element], viewport: Viewport) -> list[Element]:
    candidates = [
        item
        for item in elements
        if item.bbox.area > 0 and item.bbox.visible_in(viewport)
    ]
    candidates.sort(
        key=lambda item: (
            SOURCE_PRIORITY.get(item.source, 99),
            item.bbox.y,
            item.bbox.x,
            item.bbox.area,
            item.role,
            item.name,
        )
    )

    accepted: list[Element] = []
    exact: set[tuple[Any, ...]] = set()
    for candidate in candidates:
        key = (
            candidate.source,
            candidate.role,
            candidate.name.casefold(),
            round(candidate.bbox.x),
            round(candidate.bbox.y),
            round(candidate.bbox.width),
            round(candidate.bbox.height),
        )
        if key in exact:
            continue

        duplicate = False
        for existing in accepted:
            overlap = candidate.bbox.intersection(existing.bbox)
            coverage = overlap / candidate.bbox.area if candidate.bbox.area else 0.0
            same_label = (
                bool(candidate.name)
                and candidate.name.casefold() == existing.name.casefold()
                and candidate.role == existing.role
            )
            if (
                candidate.source == "opencv"
                and existing.source != "opencv"
                and coverage >= 0.75
            ):
                duplicate = True
                break
            if same_label and candidate.bbox.iou(existing.bbox) >= 0.65:
                duplicate = True
                break

        if not duplicate:
            exact.add(key)
            accepted.append(candidate)

    accepted.sort(
        key=lambda item: (
            item.bbox.y,
            item.bbox.x,
            SOURCE_PRIORITY.get(item.source, 99),
        )
    )
    for index, element in enumerate(accepted, start=1):
        element.id = f"E{index:04d}"
    return accepted


def observe(options: PipelineOptions) -> PipelineResult:
    started = perf_counter()
    frame = capture_screen(options.monitor, options.region)
    elements: list[Element] = []
    warnings: list[str] = []
    window: dict[str, Any] = {}
    timings: dict[str, float] = {
        "capture_ms": round((perf_counter() - started) * 1000, 2)
    }

    if options.use_uia:
        stage = perf_counter()
        try:
            from .uia import collect_uia

            uia_elements, window = collect_uia(
                frame.viewport,
                include_containers=options.include_containers,
                max_nodes=options.max_uia_nodes,
            )
            elements.extend(uia_elements)
        except RuntimeError as exc:
            warnings.append(str(exc))
        timings["uia_ms"] = round((perf_counter() - stage) * 1000, 2)

    if options.use_ocr:
        stage = perf_counter()
        try:
            from .ocr import detect_text

            elements.extend(
                detect_text(frame.image, frame.viewport, engine=options.ocr_engine)
            )
        except RuntimeError as exc:
            warnings.append(str(exc))
        timings["ocr_ms"] = round((perf_counter() - stage) * 1000, 2)

    if options.use_cv:
        stage = perf_counter()
        try:
            from .cv import detect_regions

            elements.extend(detect_regions(frame.image, frame.viewport))
        except RuntimeError as exc:
            warnings.append(str(exc))
        timings["cv_ms"] = round((perf_counter() - stage) * 1000, 2)

    merged = deduplicate(elements, frame.viewport)
    timings["total_ms"] = round((perf_counter() - started) * 1000, 2)
    sources = sorted(
        {item.source for item in merged},
        key=lambda source: SOURCE_PRIORITY.get(source, 99),
    )
    observation = Observation(
        source="+".join(sources) if sources else "screen-capture",
        viewport=frame.viewport,
        elements=merged,
        request_id=options.request_id,
        window=window,
        metadata={"timings": timings, "warnings": warnings},
    )
    return PipelineResult(observation=observation, frame=frame, warnings=warnings)
