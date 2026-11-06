# Changelog

## Unreleased - 2026-11-05

- Added `/v1/bboxset`, `/v1/bboxask` and `/v1/bboxcrop`: name the things to look for, agree on the
  list before anything is measured, post a picture, and get coordinates per entry plus the picture
  with the findings drawn on it and an optional cut-out file each (`?format=markdown|json|png`,
  `?crop=1`, `?pad=`, `?annotate=0`, `?inline=1`).
- Taught the Jev contract to carry a picture with every state and to answer `box` questions with one
  coordinate answer per edge, a chosen aggregate and an edge invariant; a framework that asks box
  questions with no picture is refused instead of answered from text, and a text-only framework
  sends no `images` key rather than an explicit null.
- Added `bbox` drawing and `crop` to the one-shot image helper (`annotate.py`) behind the single cold
  image subprocess in `Perception`; a posted picture is content-hashed once so every entry, the
  drawing and the cut-outs read one file and share one vision cache key, and only the 32 newest
  picture files survive.
- Repaired the JSON slips a model actually makes - a double-encoded tool argument, one object where
  an array was asked for, a number sent as a string - instead of refusing them, and sent the parse
  complaint back verbatim for a bounded redraft; a hopeless draft names its reason and leaves the
  session usable.
- Stopped a partially-failed look from failing wholesale: an object the model refused now reports no
  scores at all, where a NaN had broken the JSON writer for the whole answer.
- Added `optional-mcp/firebending-mcp`: `plan`, `crop`, `view` and sectioned report readers over
  those routes, returning the drawn picture as an MCP image and letting a cut-out be asked for by an
  entry's title or number; verified end to end over stdio against a live daemon.
- Gave the look a pointer probe. `/v1/bboxset` now takes a caller-authored list and validates it
  instead of drafting one, which lets a fixed question be asked word for word: `getmouse` in the MCP
  asks where the mouse or crosshair is, centred exactly on it, and reports each thing the last look
  found by how far its centre sits from the pointer's centre in pixels (`2: Green button, -400i+50j`),
  turning normalized boxes into distances a person can act on.
- The probe now asks twice, invisibly. The first box sizes a window out of its own weakest edge, the
  crop of that window is asked where the centre of the crosshair or the tip of the mouse is, and the
  answer is scaled and shifted back over the whole picture - a coordinate measured over a patch is a
  finer measurement than one measured over a screen. Two measured failures shaped the sizing: a window
  too small to contain the target answers confidently about whatever else is inside it, so the span
  comes from the reported uncertainty rather than from the box, and repeating the whole-picture
  question only jitters the score around the same wrong point. Scored against a crosshair located
  pixel by pixel, the second look halves the error when it returns an extent and changes nothing when
  it returns a point at the middle of its crop; it never makes the answer worse, and the structured
  reply carries the crop's own box so the difference is visible.
- Magnified that patch. `/v1/bboxcrop` grew `?scale=` (1-4, a resize in the same helper call that
  already cut it - no model, no measurement at the endpoint), and the probe now grows its second window
  to about 1400 px across. The answer is a fraction of whatever it is shown, so the magnification
  cannot sharpen the arithmetic - that sharpening came from the window being small, a hundredth of it
  worth 8.65 px against 17.8 px of the whole screen - it can only make a thin mark big enough to place
  rather than guess at. Measured on the same screenshot at ×2 - patches of 1516 to 1764 px cut from
  windows of 758 to 882 px - the answer stayed at the patch's own centre every time, `(968.8, 381.5)`,
  `(970.0, 382.0)`, `(969.9, 385.5)`, 139 to 143 px from the mark, where the un-magnified crop had left
  it: more pixels sharpen what the model can see and cannot move what it reads out of the middle, which
  is what `inside_crop` is there to expose.
- Made the look's text the result: `plan { confirm }` no longer draws the findings over the picture or
  attaches it (`show: true` asks for it, and the drawing pass is skipped when it is not), and the
  printed line that named the drawn file now names the picture itself so `view` still has something to
  open. A look that only wants coordinates no longer pays for a picture nobody asked to see, and a
  driver is not steered into a second look at an image it never requested.
- Added `decide` and `categorize` to that MCP, so the text pipeline is reachable the same way the
  picture one is: agree on the questions first, then judge one state or up to 32 at a time, with
  labelling runs chunked under the 32-state / 256-decision ceiling and a tally on top. A score is
  quoted in the level's own words with the axis stated, because the distribution is keyed from zero
  while a model labels its levels from one. `SKILL.md` documents both pipelines in plain language and
  `configure.mjs` installs it beside the server entry.

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
