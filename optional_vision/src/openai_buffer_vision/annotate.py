"""Draw bounded-box answers over a picture and cut the objects out.

Pillow only: no host font discovery, no OpenCV, no model. A Box answer is four normalized
coordinates, so a coordinate above PIXEL_HINT is read as a pixel instead. Labels are
transparent text with a hairline outline, sitting on the outside edges of their own box.
"""

from __future__ import annotations

import base64
import io

from PIL import Image, ImageDraw, ImageFont

# A coordinate above this is a pixel, not a fraction of the picture.
PIXEL_HINT = 1.5
# Bright green first: one object reads as one green box. Later objects get their own hue, so a
# numbered title can never be mistaken for another object's.
PALETTE = ((0, 255, 0), (0, 229, 255), (255, 212, 0), (255, 122, 217), (140, 255, 120), (255, 138, 76))
OUTLINE = (0, 0, 0, 220)


def png(image: Image.Image) -> str:
    """Base64 PNG: lossless, because an annotated picture is read for its edges and text."""
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return base64.b64encode(buffer.getvalue()).decode("ascii")


def pixels(box: dict, width: int, height: int) -> tuple[int, int, int, int]:
    """Four normalized-or-pixel coordinates -> clamped pixel edges, never inverted."""
    top, bottom = sorted((box["top"], box["bottom"]))
    left, right = sorted((box["left"], box["right"]))
    span = lambda value, limit: round(value) if value > PIXEL_HINT else round(value * limit)
    clamp = lambda value, limit: max(0, min(limit - 1, span(value, limit)))
    return clamp(left, width), clamp(top, height), clamp(right, width), clamp(bottom, height)


def region(box: dict, width: int, height: int, pad: float = 0.0) -> tuple[int, int, int, int]:
    """A crop box: `pixels` grown by `pad` of each side's own size, clamped to the picture."""
    left, top, right, bottom = pixels(box, width, height)
    grow_x, grow_y = round((right - left) * pad), round((bottom - top) * pad)
    return (
        max(0, left - grow_x),
        max(0, top - grow_y),
        min(width, right + 1 + grow_x),
        min(height, bottom + 1 + grow_y),
    )


def crop(image: Image.Image, box: dict, pad: float = 0.0) -> Image.Image:
    area = region(box, image.width, image.height, pad)
    if area[2] <= area[0] or area[3] <= area[1]:
        raise ValueError(f"box has no area inside a {image.width}x{image.height} picture")
    return image.convert("RGBA").crop(area)


def caption(item: dict) -> str:
    """The text line for one object: its name, its four coordinates, its own confidence."""
    box = item["box"]
    text = f"top {box['top']:g} bottom {box['bottom']:g} left {box['left']:g} right {box['right']:g}"
    if item.get("confidence") is not None:
        text += f"  P {item['confidence']:g}"
    return f"{item.get('name') or '?'} \u2014 {text}"


def percent(value) -> str:
    """A score reads as a percentage on the picture; the decimals stay in the text result."""
    return f"{round(float(value) * 100)}%"


def _metrics(painter, text: str, font) -> tuple[int, int]:
    box = painter.textbbox((0, 0), text, font=font)
    return box[2] - box[0], box[3] - box[1]


def _label(painter, xy, text: str, font, colour, edge: int) -> None:
    """Transparent text on the picture: legible from its outline, never from a plate behind it."""
    painter.text(xy, text, font=font, fill=colour, stroke_width=edge, stroke_fill=OUTLINE)


def draw(image: Image.Image, objects: list[dict]) -> Image.Image:
    """Numbered boxes in bright green, one hue per object: the title sits above the top-left
    corner, the score above the top-right one, and the coordinates below the bottom-left."""
    canvas = image.convert("RGBA")
    painter = ImageDraw.Draw(canvas, "RGBA")
    size = max(11, min(28, round(max(canvas.size) / 40)))
    font = ImageFont.load_default(size=size)
    edge = max(1, size // 12)
    stroke = max(1, min(3, round(max(canvas.size) / 480)))
    for index, item in enumerate(objects):
        colour = PALETTE[index % len(PALETTE)]
        left, top, right, bottom = pixels(item["box"], canvas.width, canvas.height)
        # A dark hairline behind the colour keeps an edge readable on bright and dark pixels alike.
        painter.rectangle((left - stroke, top - stroke, right + stroke, bottom + stroke), outline=OUTLINE, width=stroke)
        painter.rectangle((left, top, right, bottom), outline=colour, width=stroke)
        title = f"{index + 1}: {str(item.get('name') or '?')[:32]}"
        title_wide, tall = _metrics(painter, title, font)
        above = max(0, top - stroke - tall)
        _label(painter, (max(0, min(left, canvas.width - title_wide)), above), title, font, colour, edge)
        box = item["box"]
        if item.get("confidence") is not None:
            score = percent(item["confidence"])
            score_wide, _ = _metrics(painter, score, font)
            x = max(0, min(right - score_wide, canvas.width - score_wide))
            if x < left + title_wide:  # a narrow box takes its score inside, in the corner below
                y = max(0, min(bottom - stroke - tall, canvas.height - tall))
                _label(painter, (x, y), score, font, colour, edge)
            else:
                _label(painter, (x, above), score, font, colour, edge)
        where = f"t {box['top']:g} b {box['bottom']:g} l {box['left']:g} r {box['right']:g}"
        where_wide, where_tall = _metrics(painter, where, font)
        y = bottom + stroke
        if y + where_tall > canvas.height:  # a box at the bottom edge keeps its coordinates inside
            y = max(0, bottom - stroke - where_tall)
        _label(painter, (max(0, min(left, canvas.width - where_wide)), y), where, font, colour, edge)
    return canvas
