"""Deterministic raster-to-text geometry. No OCR, network or learned models.

The complete input canvas maps to a fixed rectangular character grid. Glyph
matching compares raster coverage, local stroke derivatives and shade density;
it is not character recognition and never assigns names to pictured objects.
"""

from __future__ import annotations

from math import floor

import numpy as np
from PIL import Image, ImageDraw, ImageFont

RESOLUTIONS = {1: 32, 2: 128, 3: 256}
CELL_WIDTH = 6
CELL_HEIGHT = 12
SHADES = ".,:;irsXA253hMHGS#9B&@"


def grid_size(width: int, height: int, level: int) -> tuple[int, int]:
    """2:1 terminal cells: a square image occupies N columns and N/2 rows."""
    if level not in RESOLUTIONS or width < 1 or height < 1:
        raise ValueError("Use resolution level 1, 2 or 3 and a nonempty image")
    side = RESOLUTIONS[level]
    scale = side / max(width, height)
    return max(1, floor(width * scale)), max(1, floor(height * scale / 2))


def _patches(plane: np.ndarray, columns: int, rows: int) -> np.ndarray:
    return (
        plane.reshape(rows, CELL_HEIGHT, columns, CELL_WIDTH)
        .transpose(0, 2, 1, 3)
        .reshape(-1, CELL_HEIGHT, CELL_WIDTH)
    )


def _features(patches: np.ndarray) -> np.ndarray:
    # A weak line still has a direction. Normalize derivatives per cell, not
    # source colors: there is no autocontrast, threshold cleanup or smoothing.
    dx = np.diff(patches, axis=2, append=patches[:, :, -1:])
    dy = np.diff(patches, axis=1, append=patches[:, -1:, :])
    peak = np.maximum(
        np.maximum(np.abs(dx).max(axis=(1, 2)), np.abs(dy).max(axis=(1, 2))), 1 / 255
    )
    derivative_scale = 0.45 / peak[:, None, None]
    return np.concatenate(
        (
            patches.reshape(len(patches), -1),
            (dx * derivative_scale).reshape(len(patches), -1),
            (dy * derivative_scale).reshape(len(patches), -1),
        ),
        axis=1,
    )


def _atlas() -> tuple[list[str], np.ndarray, np.ndarray]:
    # Pillow's bundled font, not host font discovery or a model download.
    font = ImageFont.load_default()
    glyphs = [chr(code) for code in range(32, 127) if code != 96]
    masks = []
    for glyph in glyphs:
        left, top, right, bottom = font.getbbox(glyph)
        raster = Image.new("L", (max(1, right - left), max(1, bottom - top)), 0)
        ImageDraw.Draw(raster).text((-left, -top), glyph, fill=255, font=font)
        raster.thumbnail((CELL_WIDTH, CELL_HEIGHT), Image.Resampling.LANCZOS)
        cell = Image.new("L", (CELL_WIDTH, CELL_HEIGHT), 0)
        cell.paste(
            raster,
            ((CELL_WIDTH - raster.width) // 2, (CELL_HEIGHT - raster.height) // 2),
        )
        masks.append(np.asarray(cell, dtype=np.float32) / 255)
    # Full-cell geometric primitives avoid dependence on a font's side bearings.
    for glyph, segment in [
        ("-", (0, 6, 5, 6)),
        ("_", (0, 11, 5, 11)),
        ("|", (3, 0, 3, 11)),
        ("/", (0, 11, 5, 0)),
        ("\\", (0, 0, 5, 11)),
        ("+", None),
        ("#", None),
    ]:
        cell = Image.new("L", (CELL_WIDTH, CELL_HEIGHT), 255 if glyph == "#" else 0)
        draw = ImageDraw.Draw(cell)
        if segment is not None:
            draw.line(segment, fill=255, width=1)
        elif glyph == "+":
            draw.line((0, 6, 5, 6), fill=255)
            draw.line((3, 0, 3, 11), fill=255)
        glyphs.append(glyph)
        masks.append(np.asarray(cell, dtype=np.float32) / 255)
    coverage = np.stack(masks)
    return glyphs, _features(coverage), coverage.mean(axis=(1, 2))


def _ascii(ink: np.ndarray, alpha: np.ndarray, columns: int, rows: int) -> list[str]:
    patches = _patches(ink, columns, rows)
    visible = _patches(alpha, columns, rows).max(axis=(1, 2)) > 0
    cells = [" "] * (columns * rows)
    positions = np.flatnonzero(visible)
    if not len(positions):
        return [" " * columns for _ in range(rows)]
    patches = patches[visible]
    # Constant-color regions use the complete ordered shade alphabet. They do
    # not all collapse to the densest geometric template merely because an
    # actual printed glyph cannot cover 100% of its terminal cell.
    flat = patches.max(axis=(1, 2)) == patches.min(axis=(1, 2))
    for position, value in zip(positions[flat], patches[flat, 0, 0], strict=True):
        cells[int(position)] = SHADES[
            min(len(SHADES) - 1, round(float(value) * (len(SHADES) - 1)))
        ]
    positions = positions[~flat]
    patches = patches[~flat]
    if not len(positions):
        return [
            "".join(cells[row * columns : (row + 1) * columns]) for row in range(rows)
        ]
    features = _features(patches)
    glyphs, atlas, densities = _atlas()
    atlas_square = (atlas * atlas).sum(axis=1)
    # Fixed accumulation order and stable first-index tie breaking; no BLAS/GPU.
    for start in range(0, len(features), 256):
        batch = features[start : start + 256]
        difference = (
            (batch * batch).sum(axis=1)[:, None]
            + atlas_square[None, :]
            - 2 * np.einsum("nf,gf->ng", batch, atlas, optimize=False)
        )
        difference += (
            18
            * (
                patches[start : start + 256].mean(axis=(1, 2))[:, None]
                - densities[None, :]
            )
            ** 2
        )
        for offset, index in enumerate(np.argmin(difference, axis=1)):
            position = int(positions[start + offset])
            glyph = glyphs[int(index)]
            # A visible white/very faint sample has a light shade, not the same
            # representation as a fully transparent sample.
            cells[position] = "." if glyph == " " else glyph
    return ["".join(cells[row * columns : (row + 1) * columns]) for row in range(rows)]


def _braille(ink: np.ndarray, alpha: np.ndarray, columns: int, rows: int) -> list[str]:
    # Each Braille cell retains 2x4 spatial samples, with fixed ordered shade
    # quantization. This is the output alphabet, not source-image noise injection.
    values = np.asarray(
        Image.fromarray(ink).resize((columns * 2, rows * 4), Image.Resampling.BOX)
    )
    coverage = np.asarray(
        Image.fromarray(alpha).resize((columns * 2, rows * 4), Image.Resampling.BOX)
    )
    thresholds = (
        np.array([[0, 4], [6, 2], [1, 5], [7, 3]], dtype=np.float32) + 0.5
    ) / 8
    dots = (
        (0, 0, 1),
        (0, 1, 2),
        (0, 2, 4),
        (1, 0, 8),
        (1, 1, 16),
        (1, 2, 32),
        (0, 3, 64),
        (1, 3, 128),
    )
    lines = []
    for row in range(rows):
        line = []
        for column in range(columns):
            tile = values[row * 4 : (row + 1) * 4, column * 2 : (column + 1) * 2]
            opacity = coverage[row * 4 : (row + 1) * 4, column * 2 : (column + 1) * 2]
            code = sum(
                bit
                for x, y, bit in dots
                if opacity[y, x] > 0 and tile[y, x] >= thresholds[y, x]
            )
            if not code and opacity.max() > 0:
                y, x = np.unravel_index(int(np.argmax(tile)), tile.shape)
                code = next(bit for px, py, bit in dots if px == x and py == y)
            line.append(chr(0x2800 + code) if code else " ")
        lines.append("".join(line))
    return lines


def _block(ink: np.ndarray, alpha: np.ndarray, columns: int, rows: int) -> list[str]:
    values = np.asarray(
        Image.fromarray(ink).resize((columns, rows * 2), Image.Resampling.BOX)
    )
    coverage = np.asarray(
        Image.fromarray(alpha).resize((columns, rows * 2), Image.Resampling.BOX)
    )
    lines = []
    for row in range(rows):
        line = []
        for column in range(columns):
            top, bottom = values[row * 2 : row * 2 + 2, column]
            a_top, a_bottom = coverage[row * 2 : row * 2 + 2, column]
            if max(a_top, a_bottom) == 0:
                glyph = " "
            elif top >= 0.5 > bottom:
                glyph = "▀"
            elif bottom >= 0.5 > top:
                glyph = "▄"
            else:
                glyph = "░▒▓█"[min(3, int(float(top + bottom) * 2))]
            line.append(glyph)
        lines.append("".join(line))
    return lines


def serialize_image(image: Image.Image, level: int = 1, style: str = "ascii") -> dict:
    if style not in {"ascii", "braille", "block"}:
        raise ValueError("Style must be ascii, braille or block")
    columns, rows = grid_size(*image.size, level)
    # Premultiplied resampling prevents RGB hidden under alpha=0 from leaking.
    # The entire source canvas participates: no trim, crop, background removal,
    # deskew, denoising, auto-threshold, autocontrast or content-aware reframing.
    rgba = (
        image.convert("RGBA")
        .convert("RGBa")
        .resize((columns * CELL_WIDTH, rows * CELL_HEIGHT), Image.Resampling.LANCZOS)
        .convert("RGBA")
    )
    pixels = np.asarray(rgba, dtype=np.float32) / 255
    alpha = pixels[:, :, 3]
    luminance = pixels[:, :, :3] @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
    # Dark -> dense, light -> sparse. Transparent is always blank; the smallest
    # visible shade is distinct from transparency. No adaptive contrast remap.
    ink = alpha * (0.08 + 0.92 * (1 - luminance))
    lines = {"ascii": _ascii, "braille": _braille, "block": _block}[style](
        ink, alpha, columns, rows
    )
    return {
        "text": "\n".join(lines) + "\n",
        "schema": "cereal/1",
        "method": "raster-stroke-shade",
        "level": level,
        "resolution": RESOLUTIONS[level],
        "style": style,
        "source_width": image.width,
        "source_height": image.height,
        "columns": columns,
        "rows": rows,
        "cell_aspect": 2,
        "trimmed": False,
        "alpha": "coverage",
        "shade_palette": SHADES
        if style == "ascii"
        else ("░▒▓█▀▄" if style == "block" else "braille-2x4"),
        "pixel_step_x": image.width / columns,
        "pixel_step_y": image.height / rows,
    }
