from __future__ import annotations

from dataclasses import dataclass
from typing import Sequence

import mss
import numpy as np

from .model import Viewport


@dataclass(slots=True)
class CaptureFrame:
    image: np.ndarray
    viewport: Viewport


def parse_region(value: str) -> tuple[int, int, int, int]:
    values = tuple(int(part.strip()) for part in value.split(","))
    if len(values) != 4 or values[2] <= 0 or values[3] <= 0:
        raise ValueError(
            "region must be left,top,width,height with positive width and height"
        )
    return values


def capture_screen(
    monitor: int = 1,
    region: Sequence[int] | None = None,
) -> CaptureFrame:
    with mss.mss() as session:
        if region is None:
            if monitor < 0 or monitor >= len(session.monitors):
                raise ValueError(
                    f"monitor {monitor} does not exist; valid values are 0-{len(session.monitors) - 1}"
                )
            target = dict(session.monitors[monitor])
        else:
            if len(region) != 4:
                raise ValueError("region requires left, top, width, and height")
            target = {
                "left": int(region[0]),
                "top": int(region[1]),
                "width": int(region[2]),
                "height": int(region[3]),
            }

        shot = session.grab(target)
        image = np.asarray(shot, dtype=np.uint8)[:, :, :3].copy()
        viewport = Viewport(
            width=float(target["width"]),
            height=float(target["height"]),
            left=float(target["left"]),
            top=float(target["top"]),
            monitor=monitor,
        )
        return CaptureFrame(image=image, viewport=viewport)
