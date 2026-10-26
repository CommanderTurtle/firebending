# One-shot perception and Jev

These routes are separate from the existing manual chat broker and `/v1/file`.
They do not read, stage, consume or clear message attachments. Nothing polls or
loads a model at daemon startup, on a health check, or while idle.

| Route | Input | Output |
| --- | --- | --- |
| `POST /v1/jevset` | Task/question, then refinements or literal `Y` / `N` | Validated native SystemOne JSON and save prompt |
| `GET /v1/jevset` | Optional `?session=name` | Current draft, activated framework and turn count; no inference |
| `POST /v1/jevask` | One text pipe, JSON string/object/array/null | Results, probabilities, native confidence and full native response |
| `POST /v1/cereal` | One image | Labeled, console-safe ASCII in a multiline code block |
| `POST /v1/ocr` | One image, or literal `0` / `1` | Recognized text, or engine selection acknowledgement |
| `GET /v1/ocr` | No body | Selected engine; no process/model started |

## Setup

Jev and cereal use an **already running** NInfer server. Jev requires its native
`/v1/systemone` support (`--jev`); drafting requires function tool calls, and
cereal additionally requires a vision-capable model and its image support.
Firebending never starts another NInfer instance. OCR does not call NInfer.

Configuration uses `appsettings.json`'s `Daemon` section or environment variables:

| Setting | Default | Meaning |
| --- | --- | --- |
| `OPENAI_BUFFER_Daemon__NinferBaseUrl` | `http://127.0.0.1:8000` | HTTP(S) origin or `/v1` URL |
| `OPENAI_BUFFER_Daemon__NinferModel` | empty | Discover the single `/v1/models` entry on demand; set explicitly if multiple |
| `OPENAI_BUFFER_Daemon__NinferApiKey` | empty | Optional bearer credential; keep outside committed configuration |
| `OPENAI_BUFFER_Daemon__NinferMaxTokens` | `8192` | Draft/ASCII completion limit; incomplete output is an error, never silently truncated |
| `OPENAI_BUFFER_Daemon__PerceptionTimeoutSeconds` | `120` | Whole operation deadline, including reads/retries; range 1–900 seconds |
| `OPENAI_BUFFER_Daemon__OcrPython` | discovered venv or `python`/`python3` | Python with `openai_buffer_vision` and chosen OCR extra installed |

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
without altering the activated framework until another `Y`. `N` discards the
draft and its conversation; an already activated framework remains available.
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
curl -fsS -H 'Content-Type: image/png' --data-binary @card.png "$base/v1/cereal" |
  curl -fsS --data-binary @- "$base/v1/jevask?session=cards"
```

Cereal sends the image to the configured vision model with a required
`submit_cereal` function. It requests visible text, labels, object structure and
relative arrangement (for cards: rank and full suit name, not an ambiguous glyph).
It is not a brightness-to-character filter or genuine 3D reconstruction. Fidelity
still depends on the model; unreadable content should remain uncertain rather
than be invented. Instructions pictured in an image are treated as content.

Art must fit 96 columns × 48 lines, using printable ASCII and LF only: no ANSI
controls, tabs, Unicode or embedded fences. Invalid output is redrafted within
the same three-attempt/deadline bounds, never cropped. The returned Markdown
fence can safely pass through `jevask` as part of its state. There is no image or
ASCII retention, automatic screen capture, action execution, or file-buffer use.

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
plaintext data URLs also work. Cereal accepts PNG/JPEG/GIF/WebP signatures;
OCR accepts Pillow-supported images, normalizes EXIF orientation and composites
alpha onto white before OCR. Images are limited to 20 MiB (32 MiB request
envelope); OCR additionally limits decoded images to 16 million pixels.

## Backpressure and failures

One process-wide inference/OCR lane avoids competing heavy work. Busy requests
return 429 with `Retry-After: 1`, not an unbounded queue. Switches and draft Y/N
remain immediate and do not start inference. Native busy 423/429 responses may be
retried twice within the whole deadline; transport errors/timeouts are not blindly
replayed. Errors are non-success HTTP responses, never fabricated classifications.
Use `curl -f` so a pipeline does not mistake an error body for recognized content.

The routes share the loopback CLI/exact-same-origin guard used by the message
routes. Request bodies/results are not copied into the legacy request log or
message history. Only Jev draft/refinement/framework state is retained in memory.
Images and text sent to Jev/cereal leave the daemon for the configured NInfer
origin; choose that origin deliberately. No new desktop/input authority is added.

## Reference lineage

The original Macrohard `heatvision` branch was inspected as historical design
reference, including retained uploads before the tree was emptied. Its old
entrypoint editor exposed an opt-in `useOcr` screen autofill toggle, but execution
did not implement it. No working ASCII/Daredevil serializer was present to
transplant. This implementation preserves the idea of opt-in perception feeding
typed inputs without importing unfinished automation code. The existing
observation contract stays read-only; the new ASCII pipe and native Jev
draft/activation boundary stand on their own.
