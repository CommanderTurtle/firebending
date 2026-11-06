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
  instead of drafting one, which lets a fixed question be asked word for word over the ordinary picture
  route - no endpoint of its own, since the machine already asks models about pictures. `getmouse`
  asks `Where is the box of coordinates? If it exists, it's beside either a crosshair or mouse`, reads
  the pointer from that tag's top-left corner - the overlay draws the tag below and to the right of the
  cursor whose numbers it prints - and reports each thing the last look found by how far its centre
  sits from the pointer's centre in pixels (`2: Green button, -400i+50j`), turning normalized boxes
  into distances a person can act on. One request, one box, centre-to-centre arithmetic.
- The question was chosen by measurement, not taste. Asked for the crosshair itself on a screenshot
  whose mark was then located pixel by pixel, six runs landed 136-143 px off in the same direction, on
  the "Was this helpful?" row and the `ESC`/`PrSc` keycaps about 140 px above the thin grey `+`, while
  claiming 68-88%. The probe asked twice for a while - the first box sizing a window from its own
  weakest edge, the crop asked again, the answer scaled back over the whole picture - and the second
  look returned the middle of its crop every single time, magnified or not; magnifying that crop was
  dropped as a resample of information the window already held, since the answer is a fraction of
  whatever it is measured over. The tag question answers in one pass, 100 px out on that picture at 28%
  confidence, its left and right edges within 8 px of the real tag - a box of text in the right column,
  about 135 px too high. Both numbers are in the guide, so a driver reads the confidence and the `t ==
  b` tell rather than trusting the score.
- Added the rerun button the pipeline was missing: `rerun {}` asks the agreed list of the same picture
  again, with no rewrite and nothing to re-approve, and prints the same per-thing update with fresh
  boxes and agreement. It does not reshow the picture - `show: true` asks for that - and it costs what
  a look costs, one request per entry. Worth having because it turns a weak answer into a measurement:
  measured on the same screenshot, three asks held `t 0.08, b 0.54, l 0.80` while the right edge moved
  1.00 to 0.91 and agreement wandered 0.244-0.260, which is the difference between a stable box and a
  guessed one, and it was not visible from a single look. Whatever framework is agreed right now is what
  gets asked, and the newest answer replaces the old, so `crop`, `getmouse` and the walks all read it.
  `plan { confirm }` and `rerun` now run through one look function rather than two copies of it.
- Put the overlay's plate layout into the arithmetic rather than into any picture: the coordinate plate
  is drawn at a fixed margin below and right of the cursor whose numbers it prints, so the pointer box is
  that plate *translated* by its layout margin, `-100i+50j` - `i` across, `j` upward, the frame the circle
  placer takes. Measured against a mark located pixel by pixel, then confirmed live against the overlay's
  own coordinates to 0 px across and 21 px down on a native-resolution capture. The box moves; it is not
  resized, collapsed or corrected, so every vector stays centre-of-pointer to centre-of-object over four
  real edges. Worth having: against the plate's real bounds the translated centre lands 17.3 px from the
  mark where the raw centre is 128.3 px away.
- Made the look's text the result: `plan { confirm }` no longer draws the findings over the picture or
  attaches it (`show: true` asks for it, and the drawing pass is skipped when it is not), and the
  printed line that named the drawn file now names the picture itself so `view` still has something to
  open. A look that only wants coordinates no longer pays for a picture nobody asked to see, and a
  driver is not steered into a second look at an image it never requested.
- Taught that MCP to speak MCP itself: `move` and `clickreturn` take the vector `getmouse` printed and
  walk the Macrohelp overlay's circle placer through the overlay's own MCP - open the menu, feed the
  vector, press one key - which makes the two tools the same walk ending on a different key, `Z`
  leaving the pointer on the thing and `B` clicking it and blinking the pointer back. The target is
  chosen from the titles the last look printed, so with more than one thing found a title is required
  rather than guessed; `offset` and `origin` cover a vector or a starting point the driver supplies.
  Nothing is measured again: no picture, no pixel search, no ask. The overlay keeps one control run
  active at a time, so the walk waits briefly for the previous stroke to land instead of stopping
  whatever else the keyboard was doing. Verified live over stdio: `1: Queen of England, 570i-161j`
  walked to `[3542, 921]` from `[2972, 1082]`, and a title that was not found came back naming the
  titles that were.
- Stopped a picture from being thrown away while it is still in use: the data directory holds two
  kinds of print, the copy of a posted picture and the box drawings and cut-outs made from it, and one
  shared cap of 32 let a dozen cut-outs evict the picture that `getmouse`, `crop` and `view` still read
  back by path, which answered `404` for the rest of the session. The two kinds are capped separately
  now - 64 derived prints, 16 pictures - because the derived ones can always be cut again.
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
