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

## 0.1.0 - 2026-07-16

- Consolidated the F# manual chat-completions buffer daemon.
- Enforced the versioned vision-observation contract at ingestion.
- Added schema discovery and request correlation for observations.
- Added a cleaned, read-only Python suite for capture, UIA, OCR, CV, deduplication, ScreenTag output, overlays, and daemon posting.
- Added the Surfingkeys DOM producer, integration smoke tests, CI, and repository documentation.
