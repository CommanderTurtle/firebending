---
name: firebending
description: Find named things in a picture, measure them from the pointer, or judge a long list of things under one agreed set of questions, through the firebending MCP - plan/crop/view/getmouse for pictures, move/clickreturn for the pointer, decide/categorize for text. Read before locating objects in an image, cutting found things out, aiming the pointer at a measured offset, labelling a batch of items, or asking the local model to score or classify more than a handful of things.
---

# Firebending: pictures, the pointer, and judgements

Nothing is measured until the model's list of things to look for has been shown and agreed. The
Jev rules below come first because both pipelines sit on that same self-hosted judge.

## Jev: the judge underneath both pipelines

- Judge role is the self-hosted System One at `http://localhost:8000`; its `baseUrl` is an
  **origin** - omp appends `/v1/systemone` itself, so never write `/v1` into it.
- Cost is prompt-bound, not request-bound: bundle every question that shares one `state` into one
  request, and keep rubric text short. Conditional sub-questions ride along in the same request and
  cost nothing extra - read them only when the parent answer takes that branch.
- Identical `state` + `questions` replay from a warm cache in about 0 s. Bench with a nonce field in
  `state`, or you are timing cache hits.
- Concurrency: 4 in flight is the ceiling that helps. 8-wide is no faster than serialized and 23-wide
  draws `429 server_overloaded`, because the adapter serializes anyway.
- Firebending's own daemon answers over `http://127.0.0.1:5199` and its inference lane is **1-wide**:
  a second request while one is in flight returns `429 Retry-After: 1` at once. Widen the framework
  (more questions per state), never the fan-out.
- `agreement` and `confidence` describe how concentrated the model's own distribution was. They are
  not accuracy and not calibration: a confident wrong answer is ordinary.

The deep version - wire contract, answer envelope types, 400/429 anatomy, measured throughput - is
the `systemone-judge` skill.

## Pictures

1. `plan { request: "<what the user actually asked>" }` answers with the list it intends to check,
   most likely to be there first. Nothing has been measured yet: **show the list**. More `request`
   text refines it, `count: 1-8` sets how many entries one look checks, `clear` drops it.
2. `plan { confirm: true, image: "/abs/path.png" }` runs it. One line per entry, each with its box,
   weakest edge and agreement. Coordinates are fractions of the picture: `0.00` is its top-left
   corner, `1.00` its bottom-right. Nothing is drawn over the picture and no image comes back unless
   `show: true` is passed.
3. `crop { object: "nvidia logo" }` or `crop { object: "2" }` cuts a found thing out again with no
   second model call; `view { path: "..." }` shows any file, or one region of it.
4. `rerun {}` is the rerun button: the same list asked of the same picture again - no rewrite, no new
   agreement - printed exactly as the look was, one line per thing with its box and agreement. Nothing
   is drawn and no picture comes back unless `show: true`, which is what makes it cheap to press. It
   costs one request per entry, so press it when a thing came back missing or with a weak edge and
   watch whether the box holds or moves. Whatever is agreed right now is what gets asked - after
   `getmouse` that is the pointer's own question - and the newest answer is what `crop`, `getmouse` and
   the walks read afterwards.

An entry the model refuses to locate gets its own "not found" line and no coordinates, while the
others still answer.

## The pointer

- `getmouse {}` works on the picture the last look read. It asks one fixed question, sent unchanged -
  where the box of coordinates is, beside a crosshair or mouse - then prints every thing the last
  look found by how far its centre sits from the cursor, in pixels: `2: Green button, -400i+50j`,
  `i` across and `j` downward.
- The cursor comes from the overlay's coordinate plate: the pointer box is that plate translated by
  `-100i+50j`, `i` across and `j` upward - the frame the circle placer takes, so the vector is fed to
  it as `radius@-100i+50j`-style input. Offsets stay centre-of-box to centre-of-box; the box is moved,
  never resized.
- Trust the across far more than the up-down: the plate's left and right edges land within 3-8 px of
  the real one, while the vertical edge can arrive 130-170 px out. `t == b` means it answered a line
  of text rather than the box, and a low confidence is the same warning.
- `getmouse` replaces the list the picture pipeline was looking for, so re-confirm your own afterwards.
- `move { object: "2" }` and `clickreturn { object: "2" }` hand that vector to the overlay's circle
  placer: `move` leaves the pointer standing on the thing, `clickreturn` clicks it and blinks the
  pointer back to where it started. With more than one thing found, name a title, never guess.
  `offset: "570i-165j"` walks a vector you give; `origin: [x,y]` starts somewhere other than the live
  cursor. A walk costs no model call - it is arithmetic on boxes both answers already hold.

## Text

- `decide { request: "<the task in the user's words>" }` answers with the questions it will ask each
  thing: **show them**. Another `request` refines, `clear` drops, no arguments reports the current set.
- `decide { confirm: true, state: "<one thing>" }` judges one; `states: [...]` judges up to 32.
- `categorize { items: [...], labels: [...] }` drafts the one labelling question, and with
  `confirm: true` labels every item - up to 512 per call, batched on its own - then returns a tally.

Answers come back as words: a label with the share that went to it, a probability that a yes/no
holds, or an expected score with the level named in the model's own words.

## Limits and reports

- A batch holds at most 32 states and at most 256 decisions (states x questions) per request; a state
  it cannot judge gets its own line without losing the ones that answered.
- One framework at a time per MCP process: agreeing to new questions replaces the old ones.
- Reports live as long as the process does - `report_resources` lists them, `report_find` locates a
  name inside one, `report_read` opens a section by number. Pictures and cut-outs are files on disk
  and outlive the conversation.
- When the daemon is not answering, the tool says so and prints how to start it.

## Which one to reach for

| The ask                                         | Tool                                      |
| ----------------------------------------------- | ----------------------------------------- |
| find or mark things in a picture                | `plan`, then confirm with the image       |
| cut a found thing out again, by name or number  | `crop`                                    |
| the same questions again, to see if a box holds | `rerun`                                   |
| how far is each thing from the pointer, in px   | `getmouse` (after a look)                 |
| put the pointer on the thing, or click it       | `move` / `clickreturn`                    |
| show a picture file, or one region of it        | `view`                                    |
| sort a long list into fixed labels              | `categorize`                              |
| rate severity, judge yes/no, score a rubric     | `decide`                                  |
| read back a longer answer without eating context | `report_find` then `report_read`         |
