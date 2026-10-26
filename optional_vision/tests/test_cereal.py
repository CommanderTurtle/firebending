"""Real deterministic renderer fixtures. No OCR/NInfer/server required."""

import io
import sys
from types import SimpleNamespace

import numpy as np
import pytest
from openai_buffer_vision.cereal import grid_size, serialize_image
from openai_buffer_vision.image_input import decode_image
from PIL import Image, ImageDraw


@pytest.mark.parametrize(("level", "side"), [(1, 32), (2, 128), (3, 256)])
def test_square_resolution_levels_account_for_tall_terminal_cells(level, side):
    assert grid_size(256, 256, level) == (side, side // 2)
    assert grid_size(256, 128, level) == (side, side // 4)
    assert grid_size(128, 256, level) == (side // 2, side // 2)


@pytest.mark.parametrize("style", ["ascii", "braille", "block"])
def test_fully_transparent_canvas_keeps_rows_columns_and_ignores_hidden_rgb(style):
    red = serialize_image(Image.new("RGBA", (64, 64), (255, 0, 0, 0)), style=style)
    blue = serialize_image(Image.new("RGBA", (64, 64), (0, 0, 255, 0)), style=style)
    assert red == blue
    assert red["text"] == (" " * 32 + "\n") * 16
    assert red["trimmed"] is False


@pytest.mark.parametrize(
    "color", [(255, 255, 255, 255), (0, 0, 0, 255), (255, 255, 255, 32)]
)
@pytest.mark.parametrize("style", ["ascii", "braille", "block"])
def test_white_black_and_faint_alpha_shapes_are_not_cropped_or_discarded(color, style):
    image = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    ImageDraw.Draw(image).rectangle((96, 96, 159, 159), fill=color)
    result = serialize_image(image, style=style)
    lines = result["text"].splitlines()
    assert len(lines) == 16 and all(len(line) == 32 for line in lines)
    assert all(not line.strip() for line in lines[:3] + lines[-3:])
    assert any(line.strip() for line in lines[3:-3])
    assert all(line[:6] == " " * 6 and line[-6:] == " " * 6 for line in lines)


def test_raster_strokes_are_ascii_and_repeatable_without_input_mutation():
    image = Image.new("RGBA", (160, 200), "white")
    draw = ImageDraw.Draw(image)
    draw.rectangle((8, 8, 151, 191), outline="black", width=3)
    draw.polygon([(80, 45), (45, 100), (80, 140), (115, 100)], fill="black")
    original = image.tobytes()
    first = serialize_image(image, level=2)
    assert first == serialize_image(image, level=2)
    assert image.tobytes() == original
    assert all(
        character == "\n" or 32 <= ord(character) < 127 for character in first["text"]
    )
    assert first["source_width"] == 160 and first["source_height"] == 200
    assert first["pixel_step_x"] * first["columns"] == 160
    assert first["pixel_step_y"] * first["rows"] == 200


def test_block_palette_retains_multiple_original_gray_levels():
    gradient = np.tile(np.arange(256, dtype=np.uint8), (32, 1))
    result = serialize_image(Image.fromarray(gradient), level=2, style="block")
    assert set(result["text"]) >= set("░▒▓█\n")


def test_flat_ascii_shades_do_not_all_collapse_to_one_dense_symbol():
    shades = [
        serialize_image(Image.new("L", (8, 8), value))["text"][0]
        for value in (0, 64, 128, 192, 255)
    ]
    assert len(set(shades)) == 5


def test_image_decode_preserves_alpha_hidden_color_and_full_canvas():
    image = Image.new("RGBA", (11, 9), (17, 31, 247, 0))
    data = io.BytesIO()
    image.save(data, format="PNG")
    decoded = decode_image(data.getvalue())
    assert decoded.size == (11, 9) and decoded.mode == "RGBA"
    assert decoded.getpixel((0, 0)) == (17, 31, 247, 0)


def test_cereal_helper_never_imports_ocr_backends_or_calls_a_model(monkeypatch, capsys):
    from openai_buffer_vision import oneshot

    data = io.BytesIO()
    Image.new("RGBA", (8, 8), "white").save(data, format="PNG")
    monkeypatch.setattr(
        sys, "stdin", SimpleNamespace(buffer=io.BytesIO(data.getvalue()))
    )
    monkeypatch.setattr(sys, "argv", ["oneshot", "--operation", "cereal"])
    for module in (
        "openai_buffer_vision.ocr",
        "rapidocr",
        "onnxruntime",
        "pytesseract",
    ):
        monkeypatch.setitem(sys.modules, module, None)
    assert oneshot.main() == 0
    assert '"schema": "cereal/1"' in capsys.readouterr().out
