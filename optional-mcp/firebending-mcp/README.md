# firebending

Look for named things in a picture, and judge long lists of things - with a human agreeing to the
question first, either way.

This is the MCP half of firebending. The daemon (`src/OpenAiBufferDaemon`) does the measuring and
answers in prose written for a person. This process adds what a conversation needs:

- one control point for agreeing on what to measure before anything is measured;
- pictures rendered back as images, so the client shows them instead of describing them;
- a remembered answer, so the next sentence can be "crop the llama logo" or "crop 2";
- lossless sectioned reports, so a long answer costs an index and not a wall of text;
- the same two-step agreement for text: label a list of 4 or 500 things with one agreed question.

Nothing here knows what a bounding box is. Neither does the model: it is asked what the user wants to
find, or how a list should be judged, and gets numbers back.

## Flow

```
user   "could we identify the 4090 text in this image?"
agent  plan { request: "..." }                 -> the list it intends to check, no measuring yet
user   "yes, but also the logo"
agent  plan { request: "and the Nvidia logo" } -> the list refined from where it was
user   "go"
agent  plan { confirm: true, image: "/p/x.png" }
       -> one line per object with its coordinates; the drawn picture only with show: true
agent  crop { object: "logo" }                 -> that thing alone, shown
```

```
user   "sort these support messages into bug, feature request, praise, question, billing"
agent  categorize { items: [...], labels: [...] }        -> the one labelling question, nothing judged
user   "yes, and treat double charges as billing"
agent  categorize { items, labels, note: "double charges count as billing", confirm: true }
       -> one line per item, a tally, and a report id for the full text
agent  decide { request: "rate severity, 1 cosmetic to 5 outage" } -> a different question set
agent  decide { confirm: true, states: [...] }          -> an expected level per item
```

## Tools

| Tool              | What it does                                                                        |
| ----------------- | ----------------------------------------------------------------------------------- |
| `plan`            | The list of things to look for: `request` drafts or refines, `confirm` + `image` looks, `count` sets how many of the list one picture check covers (1-8, most likely to be present first), `clear` starts over, nothing at all reports where things stand. |
| `crop`            | Cut a region out. After a look, name the object by title or number; or pass `region` / up to eight `regions` and a fresh `image`. A cut-out the look already wrote is reused instead of redrawn. |
| `view`            | Show a picture file, or just a region of it.                                          |
| `getmouse`        | One fixed question - where the mouse or crosshair is, centred exactly on it - about the picture the last look read, then the same target again through a window the first answer sized, fitted back over the whole picture. Prints the vectors and the pointer box, nothing drawn: `1: Object, 30i+60j` (i across, j down). |
| `decide`          | One control point for judging things by text: `request` drafts or refines the questions, `confirm` + `state` / `states` (up to 32) judges them, `clear` starts over, nothing at all reports the current questions. |
| `categorize`      | The short route for labelling a list: `items` + `labels` drafts one labelling question, `confirm` labels every item (up to 512 per call, batched automatically) and returns a tally. |
| `report_read`     | Open sections of a stored report by number.                                           |
| `report_find`     | Which sections mention a name, a coordinate, a file — with excerpts.                  |
| `report_outline`  | A report's sections: heading, size, the picture files each one names.                  |
| `report_resources`| Every report still held, its index, and the files it produced.                         |

Reports live only as long as the MCP process runs; the pictures they name are files on disk, and their
paths are what outlive the conversation.

## Requirements

- Bun 1.3+ (the MCP transport), Node's `fetch` is used through it.
- The firebending daemon answering on `http://127.0.0.1:5199` (default), with the vision model it is
  configured for. Start it with `scripts/Start.ps1` on Windows, or:

  ```sh
  OPENAI_BUFFER_Daemon__Url=http://127.0.0.1:5199 \
  OPENAI_BUFFER_Daemon__NinferBaseUrl=http://127.0.0.1:8000 \
  OPENAI_BUFFER_Daemon__OcrPython=<python with openai_buffer_vision installed> \
  dotnet run --project src/OpenAiBufferDaemon/OpenAiBufferDaemon.fsproj -c Release
  ```

## Install

```sh
cd optional-mcp/firebending-mcp
bun install
bun scripts/configure.mjs install --target omp          # writes ~/.omp/agent/mcp.json, backs it up first
bun scripts/configure.mjs status                        # what is registered where
bun scripts/configure.mjs uninstall --target omp        # removes only this entry
```

`--daemon-url <url>` points the registered entry at a daemon on another port; `--dry-run` prints the
change without touching anything. `install` also puts `SKILL.md` in the client's skill directory
(`~/.omp/agent/skills/firebending/SKILL.md`) so the plain-language guide for both pipelines is
available without opening this README, and `uninstall` takes it back. A client restart loads the tools.

## Environment

| Variable                  | Default                 | Meaning                                             |
| ------------------------- | ----------------------- | --------------------------------------------------- |
| `FIREBENDING_DAEMON_URL`  | `http://127.0.0.1:5199` | Where the picture daemon answers.                    |
| `FIREBENDING_TIMEOUT_MS`  | `300000`                | How long one look may take.                          |
| `FIREBENDING_MAX_IMAGES`  | `2`                     | Pictures attached to a reply; the rest stay on disk. |
| `FIREBENDING_SESSION`     | a random id             | Fix the daemon session across restarts of this MCP.  |

## Shape

`bin/firebending.js` holds the tools and their wording; `src/daemon.js` speaks the three routes;
`src/store.js` is the sectioned report store. The store and the configure script's file handling are
copied from [localflame](https://github.com/can1357/oh-my-pi) (AGPL-3.0), which solved the
index-plus-section-1 problem for scraped pages first; see `ATTRIBUTION.md`.
