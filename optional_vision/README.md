# Optional Vision

`optional_vision` converts a desktop snapshot into the repository's typed vision-observation contract. It is deliberately read-only: it captures, labels, deduplicates, serializes, and optionally posts observations. It never clicks, types, or executes an action.

## Install

```powershell
uv sync --project optional_vision --extra all --group dev
uv run --project optional_vision buffer-vision doctor
```

The base install provides screen capture and daemon posting. Extras add Windows UI Automation, OpenCV geometry, and Tesseract OCR. OCR also requires the native `tesseract` executable on `PATH`.

Paddle is a separate opt-in CPU backend, not included by `all`:

```sh
uv sync --project optional_vision --extra all --extra paddle --group dev
uv run --project optional_vision buffer-vision observe --ocr --ocr-engine paddle --post
```

It uses RapidOCR/ONNX Runtime and Paddle PP-OCRv6 small models with GPU providers
disabled. Initial use may download/cache weights; no warm OCR service is started.
`--ocr-engine tesseract` is the default, and `--ocr` is still required to enable
text recognition. The existing UIA/OCR/CV preference order is unchanged.
When using the PowerShell build helper, pass `./scripts/Build.ps1 -PaddleOcr` to
include/retain the Paddle extra; the default build syncs only `all`.

The F# `/v1/ocr` endpoint invokes `openai_buffer_vision.oneshot` in a fresh helper
process per supplied image, independently of desktop capture or staged message
files. See [perception setup](../docs/PERCEPTION.md) for Python selection,
image inputs, cold-process cleanup and the `0`/`1` switch.

## Observe

```powershell
uv run --project optional_vision buffer-vision observe --output observation.json
uv run --project optional_vision buffer-vision observe --cv --overlay observation.png
uv run --project optional_vision buffer-vision observe --cv --ocr --post
```

Structured UI Automation is preferred. OCR and generic CV regions are fallbacks, and CV output is intentionally labeled `visual-region` rather than pretending a contour is a button.
