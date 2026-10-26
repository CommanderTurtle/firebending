# Security

## Boundary

The daemon binds to `127.0.0.1` by default and has no authentication. Keep it on loopback unless you deliberately add an authenticated network boundary. Do not expose it directly to an untrusted LAN or the public internet.

Browser observation ingestion allows CORS and Private Network Access headers so a locally opened producer can post to loopback. Treat accepted observations and chat bodies as untrusted data.

The additive message/file/format/multichat/delimiter/check and request-by-ID
routes accept loopback CLI or exact same-origin browser requests only. They
reject foreign Origin headers, cross-site fetches, nonlocal peers, and rebinding
hostnames. File paths are literal filesystem data, never shell commands or URLs.
Only explicit local callers can stage them. File bytes are read on send, with a
64 MiB/64-file per-message limit; full file-bearing requests are excluded from
the legacy latest-request log. Use the private request-by-ID callback to avoid
copying replies into the legacy buffer log.

Existing buffer and automation endpoints deliberately retain their original
localhost/CORS behavior in this additive pass. They are not an authenticated
security boundary: untrusted browser code and other local processes remain a
risk for those legacy routes. Do not expose the daemon to the network; do not
treat the new guards as authentication for the old API. Model replies are
untrusted Markdown, not sanitized HTML. New formatting/reply bodies and enabled
conversation history have no artificial text truncation and use available RAM.

`optional_vision` is read-only. It contains no input injection, shell execution, arbitrary action endpoint, or hidden automation loop.

The explicit `/v1/jevset`, `/v1/jevask`, `/v1/cereal` and `/v1/ocr` routes use the
same private loopback/exact-origin guard. Only explicit Jev calls send text to
the operator-configured NInfer origin; there is no startup or idle inference.
Cereal is local raster math: no OCR, model discovery/inference, model downloads
or network calls. Its raw text output is not semantically filtered or rewritten
before callers pipe it onward. These routes do not enter the pending queue, use `/v1/file`, or write
bodies/results into request logs. Jev drafts/history/activated frameworks live in
bounded in-memory sessions; restart clears them. OCR uses a cold subprocess with
bounded input/output and termination cleanup, never a shell-interpolated command.
Paddle's first-use model-weight downloads/cache are separate from image inputs;
recognized images are not uploaded to an OCR service. HTTP image URLs and Windows
UNC/device paths are not fetched. See [limits and configuration](docs/PERCEPTION.md).

Generated Jev rubrics remain untrusted model output. Cereal's deterministic art
can visually represent arbitrary input, including text someone placed in an
image; it does not recognize, obey or remove those instructions. Neither wire
validation nor a character grid prevents prompt injection into a downstream
model chosen by the caller. No returned tool call is executed as code.

## Reports

Report vulnerabilities privately to the repository maintainer. Include affected version, reproduction steps, and expected impact. Do not include credentials, client records, or captured private screen content in a public issue.
