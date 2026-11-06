---
name: firebending
description: Find named things in a picture, or judge a long list of things under one agreed set of questions, through the firebending MCP - plan/crop/view for pictures, decide/categorize for text decisions. Read before locating objects in an image, cutting found things out, labelling a batch of items, or asking the local model to score or classify more than a handful of things.
---

# Firebending: pictures and judgements

Two pipelines, one daemon behind them, and neither measures anything until a human has agreed to
what it will measure.

## 1. The picture pipeline

"could we identify the 4090 text in this image?"

1. `plan { request: "<what the user actually asked>" }` - the list of things it intends to look for,
   most likely to be present first. Nothing has been measured yet, so show the list.
2. More `request` text refines that list ("also the Nvidia logo"); `count: 1-8` sets how many entries
   of the list one picture is checked against; `clear` drops the list; no arguments at all says where
   things stand.
3. `plan { confirm: true, image: "/abs/path.png" }` - the printed list is the result: one line per
   thing the list asked for, coordinates and agreement, in the same shape `getmouse` prints all of its
   objects, plus a file per cut-out. Nothing is drawn and no picture comes back unless `show: true` is
   passed; `view` is the optional look afterwards, by path or object.
4. Afterwards `crop { object: "llama logo" }` or `crop { object: "2" }` cuts one thing out again with
   no second model call, and `view { path: "..." }` shows any file, or one region of it.
5. `getmouse {}` asks one fixed question, sent unchanged - "Where is the box of coordinates? If it
   exists, it's beside either a crosshair or mouse" - about the picture the last look read, then prints
   each thing it had found by how far its centre sits from the pointer's centre, in pixels:
   `2: Green button, -400i+50j`, with `i` across and `j` downward. The pointer is read out of the
   coordinate tag's geometry: the overlay draws that plate at a fixed margin below and right of the
   cursor whose numbers it prints, so the cursor is the plate's top-left corner shifted up-left by
   `-28i-31j` - measured two ways on one screenshot, at (-34.5, -37.5) from the corner against a mark
   located pixel by pixel, and independently as `-100i+50j` from the plate's centre, which is the same
   point on a 155x50 plate. Both are written here in the print's own frame, i across and j downward, so
   a negative j means up; the circle placer counts j upward and the sign flips at that boundary, which
   is why `561i-165j` goes into it as `585@561i+165j`. The corner rather than the centre, because the
   plate grows with the digits it prints. The reply prints the pointer (`Mouse:`) and the plate it was
   read from (`Coordinate tag:`). One model
   request, one box, nothing drawn, no picture back - the marked-up view of a pointer is a person's,
   seen through the command line, never handed to the model. It only works after a look, because that
   is what cached both the objects and the picture's pixel size. It replaces the list the picture
   pipeline is looking for, so re-confirm your own list afterwards.
   Why the tag rather than the cursor: on that same screenshot, asking for the crosshair itself landed
   136-143 px off over six runs, always on the same wrong cluster - the "Was this helpful?" row and the
   `ESC`/`PrSc` keycaps, about 140 px above the thin grey `+` - while claiming 68-88% confidence, and a
   second look through a crop of that window answered the middle of the crop every single time. Asking
   for the tag puts its left and right edges within 3-8 px of the real plate, twice running, at 28-37%
   confidence. Its vertical edge is the unresolved half: the box arrives 135-170 px too high, so the
   up-left inset, worth about 10-17 px against a correct box, reads worse today (163 px, against 134 px
   unshifted) while the horizontal error stays under 10 px. Read the confidence and the `t == b` tell;
   the shift pays the moment the box does.
6. `move { object: "2" }` and `clickreturn { object: "2" }` carry that printed vector to the Macrohelp
   overlay through the overlay's own MCP: open its circle placer, feed the vector, press one key at
   the end. `move` leaves the pointer standing on the thing; `clickreturn` clicks it and blinks the
   pointer back to where it started. Both choose from the titles the last look printed, so with more
   than one thing found a title is required, never guessed. `offset: "570i-165j"` aims by a vector you
   give instead; `origin: [x,y]` starts from a screen point other than the live cursor. The ask count
   never moves: a look is one request per listed thing, `getmouse` is two, and a walk is none, because
   the walk is arithmetic on the boxes both answers already hold.

One list entry is one request to the model, taken one at a time. A thing the model refuses to locate
gets its own "not found" line and no coordinates at all, while the others still answer. Coordinates
are fractions of the picture: 0.00 is its top-left corner, 1.00 its bottom-right one.

## 2. The text pipeline

"is this a bug, a feature request, or praise?" - one judgement, or ten thousand.

1. `decide { request: "<the task in the user's words>" }` - the questions it will ask each thing.
   Show them. Another `request` refines them, `clear` drops them, no arguments reports the current
   ones.
2. `decide { confirm: true, state: "<one thing>" }` judges a single thing; `states: [ ... ]` judges up
   to 32 of them, one line each.
3. `categorize { items: [...], labels: [...] }` is the short route for labelling a list: the first
   call drafts the one labelling question, and with `confirm: true` it labels every item - up to 512
   per call, split into batches on its own - then gives a tally.

Answers come back as words, not tables of numbers: a label with the share of the distribution that
went to it, a probability that a yes/no holds, or an expected score with the level named in the
model's own words. `agreement` says how concentrated the model's own distribution was - it is not
accuracy and it is not calibration, so a confident wrong answer is still possible.

## Limits worth knowing

- A batch holds at most 32 states, and at most 256 decisions (states x questions) per request.
  `categorize` splits on its own; a state it cannot judge is reported on its own line without losing
  the ones that answered.
- One framework at a time per MCP process: agreeing to new questions replaces the old ones, so
  confirm the task you are about to run rather than the one from three messages ago.
- Reports (a look, a judged list) live as long as the MCP process runs. `report_resources` lists them,
  `report_find` locates a label, object or file inside one, `report_read` opens a section by number.
  The pictures and cut-outs are files on disk and outlive the conversation.
- The daemon has to be answering. When it is not, the tool says so and prints how to start it,
  instead of failing obscurely.

## Which one to reach for

| The ask                                                | Tool                                        |
| ------------------------------------------------------ | ------------------------------------------- |
| find or mark things in a picture                       | `plan` (then confirm with the image)        |
| cut a found thing out, again, by name or number        | `crop`                                      |
| how far is each thing from the pointer, in pixels      | `getmouse` (after a look)                   |
| show a picture file, or one region of it               | `view`                                      |
| sort a long list into fixed labels                     | `categorize`                                |
| rate severity, yes/no each item, judge against a rubric| `decide`                                    |
| read back a longer answer without eating context       | `report_find` then `report_read`            |
