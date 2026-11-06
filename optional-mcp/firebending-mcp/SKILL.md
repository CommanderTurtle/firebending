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
3. `plan { confirm: true, image: "/abs/path.png" }` - one line per thing with its coordinates, the
   picture with the findings drawn on it returned as an image, and a file per cut-out.
4. Afterwards `crop { object: "llama logo" }` or `crop { object: "2" }` cuts one thing out again with
   no second model call, and `view { path: "..." }` shows any file, or one region of it.
5. `getmouse {}` asks one fixed question - "Where is the mouse or crosshair in this image? This would
   be a perfect center on on the mouse/crosshair itself" - about the picture the last look read, then
   prints each thing it had found by how far its centre sits from the pointer's centre, in pixels:
   `2: Green button, -400i+50j`, with `i` across and `j` downward, and the pointer's own box and
   confidence underneath. Nothing is drawn and no picture comes back - the marked-up view of the
   pointer is a person's, seen through the command line, never handed to the model. It only works
   after a look, because that is what cached both the objects and the picture's pixel size, and the
   question is always sent unchanged. It replaces the list the picture pipeline is looking for, so
   re-confirm your own list afterwards.

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
