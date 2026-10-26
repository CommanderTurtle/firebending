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

## Reports

Report vulnerabilities privately to the repository maintainer. Include affected version, reproduction steps, and expected impact. Do not include credentials, client records, or captured private screen content in a public issue.
