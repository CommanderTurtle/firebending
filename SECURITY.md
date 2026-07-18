# Security

## Boundary

The daemon binds to `127.0.0.1` by default and has no authentication. Keep it on loopback unless you deliberately add an authenticated network boundary. Do not expose it directly to an untrusted LAN or the public internet.

Browser observation ingestion allows CORS and Private Network Access headers so a locally opened producer can post to loopback. Treat accepted observations and chat bodies as untrusted data.

`optional_vision` is read-only. It contains no input injection, shell execution, arbitrary action endpoint, or hidden automation loop.

## Reports

Report vulnerabilities privately to the repository maintainer. Include affected version, reproduction steps, and expected impact. Do not include credentials, client records, or captured private screen content in a public issue.
