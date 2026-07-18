# Optional Vision

`optional_vision` converts a desktop snapshot into the repository's typed vision-observation contract. It is deliberately read-only: it captures, labels, deduplicates, serializes, and optionally posts observations. It never clicks, types, or executes an action.

## Install

```powershell
uv sync --project optional_vision --extra all --group dev
uv run --project optional_vision buffer-vision doctor
```

The base install provides screen capture and daemon posting. Extras add Windows UI Automation, OpenCV geometry, and Tesseract OCR. OCR also requires the native `tesseract` executable on `PATH`.

## Observe

```powershell
uv run --project optional_vision buffer-vision observe --output observation.json
uv run --project optional_vision buffer-vision observe --cv --overlay observation.png
uv run --project optional_vision buffer-vision observe --cv --ocr --post
```

Structured UI Automation is preferred. OCR and generic CV regions are fallbacks, and CV output is intentionally labeled `visual-region` rather than pretending a contour is a button.
