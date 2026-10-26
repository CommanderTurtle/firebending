# One-shot perception and Jev

These routes are separate from the existing manual chat broker and `/v1/file`.
They do not read, stage, consume or clear message attachments. Nothing polls or
loads a model at daemon startup, on a health check, or while idle.

| Route | Input | Output |
| --- | --- | --- |
| `POST /v1/jevset` | Task/question, then refinements or literal `Y` / `N` | Validated native SystemOne JSON and save prompt |
| `GET /v1/jevset` | Optional `?session=name` | Current draft, activated framework and turn count; no inference |
| `POST /v1/jevask` | One text pipe, JSON string/object/array/null | Results, probabilities, native confidence and full native response |
| `POST /v1/cereal` | One image, or literal `1` / `2` / `3` | Local raster-to-text, or select 32/128/256 detail |
| `GET /v1/cereal` | No body | Selected detail; no helper/model started |
| `POST /v1/ocr` | One image, or literal `0` / `1` | Recognized text, or engine selection acknowledgement |
| `GET /v1/ocr` | No body | Selected engine; no process/model started |

## Setup

Only Jev uses an **already running** NInfer server: native `/v1/systemone`
support (`--jev`) for asking, function tool calls for drafting. Firebending never
starts another NInfer instance. **Cereal is entirely programmatic: zero model
inference, OCR, model discovery or network calls.** OCR is its own separate route.

Configuration uses `appsettings.json`'s `Daemon` section or environment variables:

| Setting | Default | Meaning |
| --- | --- | --- |
| `OPENAI_BUFFER_Daemon__NinferBaseUrl` | `http://127.0.0.1:8000` | HTTP(S) origin or `/v1` URL |
| `OPENAI_BUFFER_Daemon__NinferModel` | empty | Discover the single `/v1/models` entry on demand; set explicitly if multiple |
| `OPENAI_BUFFER_Daemon__NinferApiKey` | empty | Optional bearer credential; keep outside committed configuration |
| `OPENAI_BUFFER_Daemon__NinferMaxTokens` | `8192` | Jev draft completion limit; never used by Cereal |
| `OPENAI_BUFFER_Daemon__PerceptionTimeoutSeconds` | `120` | Whole operation deadline, including reads/retries; range 1–900 seconds |
| `OPENAI_BUFFER_Daemon__OcrPython` | discovered venv or `python`/`python3` | Cold image-helper Python with `openai_buffer_vision` and selected extra |

For Cereal alone (Pillow + the existing NumPy dependency; no OCR engines):

```sh
uv sync --project optional_vision --extra cereal
```

For both OCR engines:

```sh
uv sync --project optional_vision --extra ocr --extra paddle
```

Alternatively, `./scripts/Build.ps1 -PaddleOcr` builds the daemon and syncs the
optional vision environment with Paddle included. A plain build intentionally
syncs only the existing `all` extra, so use that switch on later builds too if
you want to retain Paddle in the same managed environment.

Tesseract also needs the native `tesseract` executable on `PATH` and its language
data. Paddle is optional even with the existing `all` extra. Its CPU backend is
RapidOCR/ONNX Runtime using Paddle PP-OCRv6 small detection/recognition models,
not a browser or the browser WASM bundle. CUDA, DirectML and CANN are disabled.
RapidOCR can download/cache model weights on first use; allow network access for
that first load or pre-provision its model cache. Cached weights on disk do not
mean a model is kept resident. Every HTTP OCR operation starts a fresh Python
helper, waits for it, and reaps it (including on cancellation). A cold first
download may require a larger configured deadline.

When running a published daemon outside the source checkout, set `OcrPython` to
the installed environment explicitly. Source runs search the working/executable
directory and up to six ancestors for `optional_vision/.venv`. No package install
or model warm-up is performed by the daemon.

## Draft, refine, activate, ask

Shell examples below use `curl`; on Windows PowerShell use `curl.exe` to avoid
the legacy alias. The standard URL is `http://127.0.0.1:5087`.

```sh
base=http://127.0.0.1:5087
curl -fsS --data-binary 'Identify the exact playing card. Use a choice question with all 52 rank/suit combinations plus unknown. Include a noul question for whether the card is clearly visible.' "$base/v1/jevset?session=cards"

# Ordinary replies continue the draft conversation, without activating it.
curl -fsS --data-binary 'Also include a joker option and explain unreadable ranks in the unknown criterion.' "$base/v1/jevset?session=cards"

# Activate only after reviewing the returned JSON.
curl -fsS --data-binary Y "$base/v1/jevset?session=cards"
curl -fsS --data-binary 'An upright card labeled A and SPADES.' "$base/v1/jevask?session=cards"
```

Drafting uses a required function call containing the complete framework JSON.
The daemon validates the native `state/questions/model` contract: choice,
noul and score types, descriptions, domains, duplicates and size bounds. Invalid
tool/JSON/schema output gets at most two repair prompts after the first attempt.
This is **wire-contract validation**, not a hidden trial classification or proof
that the rubric is musically/visually/semantically correct. A valid draft is
returned in full before the `Save for /v1/jevask? Y/N` prompt. Review it.

`Y` activates the current validated draft. Further text continues refinement
without altering the activated framework until another `Y`. `N` clears the
draft, its conversation **and** the activated framework. `jevask` has no separate
saved framework or result history: it uses the current activation owned by
`jevset` for each one-shot request. After `N`, it returns 409 until a new draft is
activated with `Y`. An in-flight ask cannot return a stale result after its
framework was cleared/replaced.
Restarting clears all Jev state. All state is in memory, not the legacy request
log. `?session=name` isolates frameworks/conversations; omission uses `default`.
There are at most 128 named sessions. A late inference result cannot restore a
draft after `Y` or `N` changed it.

`jevask` replaces only `state`, retaining the activated questions. Plain input
becomes a string; `Content-Type: application/json` accepts a native EntryType
(string, object, array or null). It never refines or saves anything. The model
alias comes from operator configuration/discovery, not user JSON. Use
`?session=cards&format=json` for the validated native response as JSON instead
of Markdown. Returned distributions must contain every expected label, sum to
one and agree with the winning choice/score. Invalid upstream data is an error.

For `choice`, the table shows the selected probability **and** native confidence
(normalized distribution concentration). For `score`, it shows expected level,
modal probability and native confidence. For `noul`, it shows P(true), including
when false is more likely; native noul has no separate confidence field. None of
these numbers is calibrated real-world accuracy.

## Cereal: image → ASCII → Jev

```sh
# Select detail; this only changes an integer, not an image buffer.
curl -fsS --data-binary 1 "$base/v1/cereal"  # 32
curl -fsS --data-binary 2 "$base/v1/cereal"  # 128
curl -fsS --data-binary 3 "$base/v1/cereal"  # 256

# Raw art on stdout, directly into a manually configured Jev framework.
curl -fsS -H 'Content-Type: image/png' --data-binary @card.png "$base/v1/cereal" |
  curl -fsS --data-binary @- "$base/v1/jevask?session=cards"

# Or copy the exact same text. Cereal does not choose/forward its destination.
curl -fsS -H 'Content-Type: image/png' --data-binary @card.png "$base/v1/cereal" | wl-copy
```

On Windows, use `curl.exe` and e.g. `clip.exe` for the default ASCII output.
The default response is **raw `text/plain`**: no labels, menu, JSON envelope,
Markdown fence, model rewrite, guessed objects, or semantic validation/repair
layer. One image goes in; one text image comes out. `/v1/jevask` accepts that
text unchanged if the caller chooses to pipe it there. Cereal never supplies a
card name or invokes Jev itself; the framework and interpretation belong to you.

The renderer decodes the full raster, applies EXIF orientation, and resamples in
premultiplied-alpha space onto an aspect-correct rectangular character grid.
There is **no trimming, cropping, background removal, autocontrast, deskew,
denoising, auto-threshold cleanup, padding removal or content-aware reframing**.
Every output row and column is retained, including all-space borders and trailing
spaces. Fully transparent images return a complete blank grid, not an error.
Hidden RGB under alpha=0 cannot contribute ink. White, black and partially
transparent foregrounds remain distinct from completely transparent samples.

ASCII cells use a fixed 6×12 sampling geometry. Each cell is matched against a
local glyph atlas using sampled coverage, horizontal/vertical stroke derivatives
and shade-density error. The atlas uses Pillow's bundled font plus full-cell
geometric strokes, not system font lookup, weights, learned recognition or OCR.
Constant-color regions use the complete ordered shade alphabet; detailed cells
use stroke matching. Dark regions use dense symbols, light regions sparse ones;
finite glyph density is the available grayscale resolution. RGB is projected to luminance, not ANSI
color. Weak strokes retain a local derivative direction; source colors are not
adaptively enhanced. This is spatial raster serialization, not reconstructed SVG
paths or a promise of lossless text recognition.

Detail levels `1/2/3` set a 32/128/256 long-edge grid budget. Terminal cells are
2:1 tall: a square at level 3 is 256 columns × 128 rows (the visual equivalent of
a 256×256 square), not a vertically stretched square of 256 text rows. Rectangular
inputs keep their original aspect ratio within one-cell rounding. The selected
level is process-wide; each request snapshots it. `?level=3` overrides one image
without changing the default. No image, result or staged file is retained.

Optional output alphabets and envelopes:

```sh
curl -fsS -H 'Content-Type: image/png' --data-binary @image.png "$base/v1/cereal?level=3&style=block"
curl -fsS -H 'Content-Type: image/png' --data-binary @image.png "$base/v1/cereal?style=braille&format=markdown"
curl -fsS -H 'Content-Type: image/png' --data-binary @image.png "$base/v1/cereal?format=json"
```

`style=ascii` (default) is printable 7-bit text; `block` adds shade/half-block
characters, and `braille` maps fixed 2×4 dot patterns with ordered shade
quantization. Neither uses terminal escape codes. `format=markdown` wraps the
same grid in a code block; `format=json` includes its unchanged `text`, source
dimensions, grid dimensions and pixel steps (`cereal/1`). Row `r`, column `c`
maps to source rectangle `[c*pixel_step_x, r*pixel_step_y, (c+1)*pixel_step_x,
(r+1)*pixel_step_y]`. There are no inserted/deleted line numbers or blank rows.
Use a non-wrapping monospace viewer wide enough for the selected level.

For the image/font API contracts, see the official [Pillow image operations](https://pillow.readthedocs.io/en/stable/reference/ImageOps.html)
and [bundled font API](https://pillow.readthedocs.io/en/stable/reference/ImageFont.html).

## OCR selection and image inputs

```sh
# Default: Tesseract. Switching only sets a flag; no engine is loaded.
curl -fsS --data-binary 0 "$base/v1/ocr"
curl -fsS -H 'Content-Type: image/png' --data-binary @screenshot.png "$base/v1/ocr"

# Opt-in Paddle. Remains cold between individual requests.
curl -fsS --data-binary 1 "$base/v1/ocr"
curl -fsS --data-binary '/absolute/path/screenshot.png' "$base/v1/ocr"
```

Engine selection is process-wide and snapshots at request start. The same switch
does not change desktop `buffer-vision observe` options. Literal `0`/`1` are
reserved on the OCR route; use JSON `{ "path": "0" }` if an image is named `0`.
Recognized lines preserve useful column gaps/blank rows; successful recognition
of no text returns an empty body, not an invented label.

Both image routes accept raw bytes (`image/*` or `application/octet-stream`), a
literal local path (optional surrounding single/double quotes), a JSON string,
`{ "path": "..." }`, or `{ "image_data_url": "data:image/png;base64,..." }`.
Paths are opened once, never shell-evaluated or stored. HTTP URLs and Windows
UNC/device paths are rejected. JSON/data URLs require a JSON content type;
plaintext data URLs also work. Both accept formats supported by the installed
Pillow decoder (including PNG, JPEG, WebP, GIF, BMP, TIFF, ICO and AVIF when its
codec is present). Animated/multi-page inputs use the first frame. Both normalize
EXIF orientation; only OCR composites alpha onto white. Cereal retains alpha
coverage. Images are limited to 20 MiB (32 MiB request envelope) and 16 million
decoded pixels. Literal `1/2/3` select Cereal's level; JSON `{ "path": "1" }`
opens an image named `1` instead.

## Backpressure and failures

One process-wide Jev/OCR lane avoids competing model work. Busy requests
return 429 with `Retry-After: 1`, not an unbounded queue. Switches and draft Y/N
remain immediate and do not start inference. Native busy 423/429 responses may be
retried twice within the whole deadline; transport errors/timeouts are not blindly
replayed. Errors are non-success HTTP responses, never fabricated classifications.
Use `curl -f` so a pipeline does not mistake an error body for recognized content.
Cereal is independent of that lane: a busy Jev model does not block local image
rendering. Each Cereal operation has its own cold helper and bounded deadline.

The routes share the loopback CLI/exact-same-origin guard used by the message
routes. Request bodies/results are not copied into the legacy request log or
message history. Only Jev draft/refinement/framework state is retained in memory.
Only explicit Jev calls send text to the configured NInfer origin; choose that
origin deliberately. Cereal never sends the image/text to any model. No new
desktop/input authority is added. Local-origin guards and bounded resource
handling are transport protections, not an inter-endpoint content filter.

## Reference lineage

The original Macrohard `heatvision` branch was inspected as historical design
reference. The Git branch's old entrypoint editor exposed an unimplemented
`useOcr` toggle. The separate original `Refactor.zip` archive contains the actual
Python ScreenTag coordinate encoders, OpenCV contour path, OCR adapters and
image-compaction prototypes. Its spatial/typed separation is useful reference;
its agent loop, model calls, persistent OCR and entropy/noise injection are not
part of Cereal. Firebending's existing observation/overlay contracts also keep
geometry separate from interpretation.

`mm-tools/img2svg` supplies the related local-only, alpha-aware raster/vector
design reference. Omarchy's `omarchy-transcode-ascii` supplies the terminal cell,
alpha and Braille/block reference. Cereal does not adopt their optional trimming,
background removal or fixed global threshold; it serializes the full canvas.
