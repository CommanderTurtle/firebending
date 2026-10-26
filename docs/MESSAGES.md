# Markdown messages and pipes

These endpoints are additive. The existing `/v1/chat/completions`, `/v1/buffer`,
Zuko, Iroh, macro, and vision contracts remain available.

The daemon is still a **manual response broker**, not an inference engine or a
new tool runner. A model relay reads a waiting request and posts its answer.
There are no hidden model calls, desktop nudges, tools, or generation-token caps.

## The six small endpoints

| Endpoint | Input | Result |
| --- | --- | --- |
| `POST /v1/file` | Literal path, JSON string, or JSON `{ "path": "...", "mime_type": "image/png" }` | Stage a path for the next message |
| `POST /v1/message` | Plain text or JSON with a `message`, `text`, or `content` string; optional `model` | Wait for the model; return `text/markdown` |
| `POST /v1/format` | Model JSON, one content/message entry, JSON-encoded string, standard SSE capture, or plain text | Return readable Markdown without creating a request |
| `POST /v1/multichat` | `1` or `0` | Preserve conversation, or turn preservation off and clear history |
| `POST /v1/delimiter` | A literal marker, or `0` | Enable capture with that marker, or disable it and clear capture |
| `GET /v1/check` | No body | Return and consume the last captured section |

Additional methods: `GET /v1/file` inspects paths, `DELETE /v1/file` clears paths,
`GET /v1/multichat` reports mode/count, and `POST /v1/check` extracts a section
from piped text. None of these clears conversation history; use multichat `0`.

All new endpoints are loopback CLI/same-origin only. They do not inherit the
legacy API's wildcard CORS. They perform no shell interpolation or URL fetching.

## Curl usage

PowerShell examples use `curl.exe` to avoid the older PowerShell alias:

```powershell
$base = 'http://127.0.0.1:5087'

# Path only: surrounding literal single/double quotes are accepted too.
curl.exe -fsS --data-binary 'C:\path with spaces\picture.png' "$base/v1/file"
curl.exe -fsS --data-binary 'Explain the attached image.' "$base/v1/message"

# Pipe model JSON from a file, or from another program's stdout.
curl.exe -fsS --data-binary '@response.json' "$base/v1/format"
Get-Content -Raw response.json | curl.exe -fsS --data-binary '@-' "$base/v1/format"

# Preserve subsequent turns until explicitly switched off.
curl.exe -fsS --data-binary '1' "$base/v1/multichat"
curl.exe -fsS --data-binary 'What should we change next?' "$base/v1/message"
curl.exe -fsS --data-binary '0' "$base/v1/multichat"

# Capture a final section without changing the full reply delivered to you.
curl.exe -fsS --data-binary 'HERE' "$base/v1/delimiter"
curl.exe -fsS --data-binary 'Finish with a table between two standalone HERE lines.' "$base/v1/message"
curl.exe -fsS "$base/v1/check"
curl.exe -fsS --data-binary '0' "$base/v1/delimiter"
```

On POSIX shells use `curl` with the same options and a local POSIX file path.
`--data-binary @-` reads stdin without curl's form-field encoding. Avoid `-d @-`
for multiline input, because curl can strip line breaks when reading a file.

A fully manual formatting/extraction pipe works too:

```powershell
curl.exe -fsS --data-binary 'HERE' "$base/v1/delimiter"
curl.exe -fsS --data-binary '@response.json' "$base/v1/format" |
    curl.exe -fsS --data-binary '@-' "$base/v1/check"
```

Use the same `?session=name` on file, message, multichat, delimiter, and check
calls to isolate another conversation. Without it, they share `default`.
Only one message may be waiting in a session; overlapping sends return `409`.
Different sessions may wait independently. At most 128 named sessions are held.

## Model relay handoff

While a curl to `/v1/message` is waiting:

1. Inspect `/v1/pending` for its `msg-...` request ID.
2. `GET /v1/requests/msg-...` returns `body` and `reply_url`. Forward the complete
   `body` to your model using the relay you already own.
3. POST the model's JSON response to `reply_url`. A plain Markdown reply is
   accepted there too. The reply route returns `202`; the waiting message curl
   receives formatted Markdown, including a `<details><summary>Reasoning</summary>`
   section when reasoning was supplied.

```powershell
curl.exe -fsS "$base/v1/pending"
curl.exe -fsS "$base/v1/requests/msg-REPLACE_WITH_ID"
curl.exe -fsS -H 'Content-Type: application/json' --data-binary '@model-response.json' "$base/v1/requests/msg-REPLACE_WITH_ID"
```

The legacy `/v1/buffer` still fulfills these requests if you explicitly target
their ID. Prefer the private reply route: it does not copy replies into the
legacy CORS-readable buffer log. New-message entries in the legacy latest-request
log contain only a lookup pointer and attachment count, never their body, paths,
or file bytes. The complete envelope is available only while its message waits.

## Formatting rules

The same formatter serves both `/v1/format` and `/v1/message`. It recognizes
Chat Completions messages/deltas, text and thinking content blocks, Responses-style
output/summary entries, and a leading complete `<think>...</think>` block.
It does not turn arbitrary metadata JSON into an answer. Intended JSON inside an
actual model content field remains text; it is not recursively stripped away.

JSON escapes are decoded by the JSON parser, including nested serialized outer
strings. Bare illegal control characters inside otherwise valid JSON strings
are repaired without rewriting quotes or other syntax. Actual CRLF/CR are
normalized to LF. The default `?escapes=auto` also
repairs literal `\n`, `\r`, and `\t` in a one-line response when it has no code
backticks or Windows/UNC paths. Ambiguous source code/path text is preserved.
Use `?escapes=off` to disable that additional repair, or `?escapes=literal` to
explicitly repair literal escapes even in otherwise ambiguous text. Literal mode
is intentional conversion, not safe for arbitrary source code.

Markdown, tables, indentation, Unicode, and complete answers are not truncated.
No `max_tokens` or tool fields are added to the model envelope. The new HTTP
routes do not impose a body-size truncation; actual capacity is available RAM.
This is a Markdown transport, not an HTML sanitizer—render model output as
untrusted Markdown in downstream viewers.

## File lifecycle and MIME

Posting `/v1/file` saves an absolute path and optional MIME override in memory.
It does not copy/upload/read the contents yet. Paths are read when `/v1/message`
is sent, so an intervening file edit is included. Repeated file calls append.
At most 64 files and 64 MiB of file bytes may accompany one message.

Images use `image_url` data URLs; PDF/other binary files use `file.file_data`
with their actual MIME; WAV/MP3 use `input_audio`. Text/source files become UTF-8
text parts, with UTF-16/UTF-32 BOM detection as well. Known signatures are checked
before filename inference. Unknown binary data stays `application/octet-stream`,
not a pretend image or MP3. This follows the native content-part approach used
by [LLM's attachment adapter](https://github.com/simonw/llm/blob/main/llm/default_plugins/openai_models.py).
The receiving model must support the supplied media type; this daemon does not
silently OCR, transcode, or promise every model can understand every file.

A successfully written Markdown reply consumes only the paths snapshotted for
that message. Files added during the wait remain staged. Read errors, invalid
replies, timeouts, and disconnected clients do not consume files or append
conversation turns. `DELETE /v1/file` is rejected while that session is sending.
With multichat on, previous native user/assistant messages—including their
attachment parts—remain in conversation memory until multichat is set to `0`.
The `<details>` presentation wrapper is never replayed as conversation content.

## Bounded waiting and retry

`Daemon:MessageTimeoutSeconds` defaults to 900 and must be 1–86400 seconds.
It is independent of the legacy chat timeout. No callback means HTTP `504`, a
`Retry-After: 1` header, pending-request cleanup, and files retained for retry.
Disconnect and daemon shutdown also cancel the wait and release the session.
An orderly shutdown returns `503` to an attached caller instead of an empty
successful answer; restarting still resets all process-local state.
No answer is fabricated, and no automatic desktop action or duplicate model
submission is started. Retry a known failed/timed-out send deliberately; the
late old reply cannot fulfill the replacement request. Do not blindly resend
after a successful/partially received answer: message sends are not an
idempotent transport and can create another conversation turn.

## Delimiter lifecycle

Set the marker before sending. Complete standalone marker lines are preferred;
an inline literal pair is supported too. The section between the first pair is
returned with its indentation and interior blank lines intact, minus the marker
lines themselves. Reasoning is excluded, including when piping our formatted
disclosure into `/v1/check`. The full original reply still reaches the message
caller.

Capture off is the default. Posting literal `0` frees the capture and stops
holding a dedicated output window. Changing the marker clears the old capture.
`GET /v1/check` atomically consumes a capture; a second check returns `404`.
POSTing text extracts it immediately and consumes any existing capture on
success. An incomplete/missing pair returns `404`; disabled capture returns
`409`. A new successful answer without the pair replaces an old capture with
nothing, so a check cannot accidentally return an earlier answer's table.
Clearing/re-setting a marker while a message waits prevents that old in-flight
reply from repopulating capture.

All switches, staged paths, history, and captures are process-local memory.
Restarting the daemon resets them. Multichat history is deliberately not silently
trimmed; clear it with `0` when finished.
