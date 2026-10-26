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
same private loopback/exact-origin guard. Jev/cereal send supplied text/images to
the operator-configured NInfer origin only when invoked; there is no startup or
idle inference. They do not enter the pending queue, use `/v1/file`, or write
bodies/results into request logs. Jev drafts/history/activated frameworks live in
bounded in-memory sessions; restart clears them. OCR uses a cold subprocess with
bounded input/output and termination cleanup, never a shell-interpolated command.
Paddle's first-use model-weight downloads/cache are separate from image inputs;
recognized images are not uploaded to an OCR service. HTTP image URLs and Windows
UNC/device paths are not fetched. See [limits and configuration](docs/PERCEPTION.md).

Generated rubrics and ASCII remain untrusted model output. Wire validation and
console-safe characters do not prove semantic correctness or prevent all prompt
injection into a downstream model. No returned tool call is executed as code.

## Reports

Report vulnerabilities privately to the repository maintainer. Include affected version, reproduction steps, and expected impact. Do not include credentials, client records, or captured private screen content in a public issue.
