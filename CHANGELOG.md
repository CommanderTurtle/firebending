# Changelog

## Unreleased - 2026-10-25

- Added Markdown message, standalone format, one-use file path, multichat,
  delimiter, and check endpoints, with private request/reply lookup.
- Added bounded missing-reply failure/cleanup and safe explicit retry behavior;
  retained the existing compatibility chat/buffer and automation contracts.
- Fixed stale targeted replies falling through to unrelated requests, queue
  tombstone retention, and SSE disconnect cleanup.
- Split the monolithic F# source by responsibility without replacing the
  existing compatibility response normalizer or observation contract.
- Refreshed Python/MCP dependencies and added a reproducible Bun lock/typecheck.
- Corrected OCR channel ordering and Windows foreground HWND width; made a
  disappearing UIA root an explicit fallback rather than a pipeline crash.
- Added headless HTTP regressions and made the consolidated gate fail fast.
- Taught the Jev framework prompt to bundle every question that shares one `state`
  into a single framework, and documented the measured decision-throughput envelope
  (prompt-bound cost: ~0.05s vs ~0.27s per decision, no gain past 4-wide fan-out).
- Added `?batch=1` to `/v1/jevask`: 1-32 states in one call, asked sequentially
  inside one lane hold (256-decision cap), every state validated before the first
  native request, and per-state failures reported without losing the states that
  answered. Single-state responses keep their exact previous status and body.
- Stopped the Jev framework prompt from adding meta questions (self-confidence,
  certainty, ambiguity); each one was a whole extra decision on every state.

## 0.1.0 - 2026-07-16

- Consolidated the F# manual chat-completions buffer daemon.
- Enforced the versioned vision-observation contract at ingestion.
- Added schema discovery and request correlation for observations.
- Added a cleaned, read-only Python suite for capture, UIA, OCR, CV, deduplication, ScreenTag output, overlays, and daemon posting.
- Added the Surfingkeys DOM producer, integration smoke tests, CI, and repository documentation.
