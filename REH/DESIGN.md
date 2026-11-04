# REH — Reverse Engineer HyperFrames: architecture & contracts

REH turns a composed motion-graphics video (HyperFrames-style, 30 fps, up to 1440p) into a
pruned, deliberate library of what matters, so a coding agent can re-author the video as a
HyperFrames `index.html` from near-zero guesswork.

Two passes, one contract directory:

```
pass 1 (REH, programmatic)      pass 2 (outside REH, human/agent)
──────────────────────────      ─────────────────────────────────
mp4 ──probe──audio──frames      user opens coding agent (omp et al.)
    ──motion/cut analysis──     in work/<slug>/ (or points it there),
    scene inference + folders   reads REH/SKILL.md + hyperframes
    keep-frame pruning (PNG)    SKILL/checkout → writes out/index.html
    infer loop via firebending  (optional render/verify with their
    httpd → per-scene .md       own hyperframes install — not REH's)
```

HyperFrames itself is **not** a REH dependency: the user checks it out separately.
REH's only network dependency is a presumed-running firebending httpd
(`http://127.0.0.1:5087`) for the inference loop.

## Components

| Path | Runtime | Role |
| --- | --- | --- |
| `engine/` | Rust (release: LTO, codegen-units=1) | `reh` CLI: probe, decode pipeline, audio split, waveform/silence/beat hints, motion + cut inference, scene folders, keep-frame library, cuts application |
| `preview/` | Bun + TS + vanilla canvas | `bun preview/server.ts <workdir>`: video + scrub + waveform timeline + [ADD CUT] → `POST /api/cuts` |
| `infer/` | Bun + TS | poll loop: per scene, stage keep PNGs (`POST /v1/file`), send guiding prompt (`POST /v1/message`, waits for the active model's markdown via firebending), write `scenes/*/description.md`. `--dry-run` emits the prompt bundle instead (the inside-omp path). |
| `SKILL.md` | — | the skill document a coding agent reads to do pass 2 |

## Pipeline (engine)

1. `reh probe <mp4>` → `probe.json` (ffprobe, rational fps kept).
2. Audio: lossless codec-copy → `audio.*`; PCM s16 mono peaks @50 Hz → `waveform.json`;
   silence via ffmpeg `silencedetect` (parsed); beat/onset hints via 50 Hz spectral flux
   (rustfft). All audio cut marks are **hints only**, never authoritative.
3. Video: ONE hwaccel decode (cuda → vaapi → qsv → none) via ffmpeg with **two outputs from
   the same decode**: (a) full-res JPEG q90 image2 sequence `frames/%06d.jpg`, (b) `gray`
   downscaled (default `--scale 640`) rawvideo piped to Rust at ~4 MB/s. Rust analyzes the
   stream: blockwise (8px grid) absolute luma delta — the "small delta in an otherwise
   identical color" detector — emitting `frames.json` + `motion.json`.
   `--png-all` additionally dumps every frame lossless.
4. Scene inference: hard cuts (full-frame delta spikes) + dissolve classification
   (uniform high activity sustained) → inferred cut list → `scenes.json`.
5. `reh run` = 1-4 + `scenes export` (inferred folders immediately, per spec).
6. Correction (anytime, optional):
   `reh scenes apply <slug> --lgtm | --cuts "00:00:12,..." | --file cuts.json`,
   or the preview server writing `cuts.json` (`source:"user"` always wins on conflict).
   Apply re-exports scene folders deterministically.
7. Inference loop (pass 2 entry): `bun infer/infer.ts <workdir>` (or `reh infer` shelling to
   it) — sequential, paced, POST-waiting on firebending httpd.

## work/<slug>/ layout — THE CONTRACT

`<slug>` = video filename without extension (collision-safe suffix on clash).
All JSON is UTF-8, LF, one record per line ONLY where noted; times are seconds (float,
derived from frame index × rational fps — never wall-clock), frames are 0-based integers.

```
work/<slug>/
  probe.json
  audio.<ext>                  # lossless remux, no transcode
  waveform.json
  frames/%06d.jpg              # every frame, 1-based name = frame index+1
  frames.pngall/%06d.png       # only with --png-all
  frames.json                  # analysis record per frame
  motion.json                  # motion/cut intervals
  scenes.json                  # authoritative scene table (see below)
  cuts.json                    # user-provided cuts (written by preview/CLI; may be absent)
  scenes/<NN>-<hhmmss>/        # NN = 2-digit scene idx; hhmmss = start time
    meta.json
    keep/%06d.png              # lossless pre-movement + post-movement + head/tail frames,
                               # deduped (name = original frame index+1)
    notes.md                   # REH-authored scaffold (facts, empty prose)
    description.md             # authored by the model loop (pass 2); absent before infer
  out/
    PROMPT.md                  # final assembly prompt bundle (dry-run / infer --bundle)
    index.html                 # written by the coding agent, not by REH
```

### probe.json
```json
{"path":"...","vcodec":"h264","acodec":"aac","width":2876,"height":1814,
 "fps":[30,1],"duration_s":107.533,"nb_frames":3226}
```

### frames.json — JSONL, one object per frame, n ascending
```json
{"n":0,"t":0.0,"act":0.0,"cut":null,"cut_conf":0.0,"state":"still"}
```
`act`: mean active-block delta (0..1). `cut`: `null|"hard"|"dissolve"`. `state`: `still|motion|transition`.

### motion.json
```json
{"intervals":[{"kind":"motion|dissolve|hard","f0":120,"f1":150,"t0":4.0,"t1":5.0,
               "bbox":[x0,y0,x1,y1],"peak_f":133,"scene":2}]}
```
bbox in **native** pixel coords (scaled coords × scale-back).

### scenes.json — authoritative
```json
{"slug":"...","fps":[30,1],"width":2876,"height":1814,"nb_frames":3226,
 "inferred":[[0,231],[232,810],[811,3225]],
 "cuts":[{"f":232,"t":7.7333,"source":"infer","kind":"hard","conf":0.91},
         {"f":410,"t":13.667,"source":"audio","kind":"beat","conf":0.4},
         {"f":235,"t":7.8333,"source":"user","kind":"manual","conf":1.0}],
 "scenes":[{"i":0,"f0":0,"f1":231,"t0":0.0,"t1":7.7,"dir":"00-000000"}]}
```
`dir` is the folder name under `scenes/` (engine writes the bare `NN-hhmmss`;
consumers MUST resolve `scenes/<dir>` and tolerate a `scenes/`-prefixed value).

`cuts` sorted by `f`; precedence `user > infer > audio`. Final boundaries = user cuts if
any user cut exists, else infer cuts, plus audio hints listed alongside for the human.
Every scene boundary MUST equal a cut frame; scene `[f0,f1]` inclusive.

### waveform.json
```json
{"rate":50,"peaks":[0.02,0.01,...],           # max |s16|/32767 per bucket, length=ceil(dur*rate)
 "silence":[{"t0":12.2,"t1":13.9}],
 "beats":[{"t":4.02,"strength":0.71}]}
```

### cuts.json — written by preview UI or `--cuts`/`--file`; source always `"user"`
```json
{"cuts":[{"f":null,"t":12.0},{"f":null,"t":39.0}],"confirmed":true}
```
Engine snaps `t` → nearest frame, drops cuts within `max(2, 0.0666·fps)` frames of each
other or of 0/last (0 and first-frame are implicit), then rewrites scenes.json.

### scenes/NN-hhmmss/meta.json
```json
{"i":0,"dir":"scenes/00-000000","f0":0,"f1":231,"t0":0.0,"t1":7.7,
 "in":[null,{"f":232,"kind":"hard"}],"out":{"f":232,"kind":"hard"},
 "motions":[{"kind":"motion","f0":12,"f1":40,"bbox":[..],"peak_f":25}],
 "audio_hints":{"silence_in":[],"beats_in":[7.73]},
 "keep_f":[1,13,41,232]}
```

### Keep-frame selection (deterministic; per scene)
- scene head `f0`, scene tail `f1`
- per motion interval: `f0-1` (pre-movement), `f1+1` (post-movement), clamped to scene,
  plus the interval's settle frame is `f1+1`; `peak_f` only if kind=`dissolve`
- dedupe: drop a candidate if its JPEG luma MAE vs the already-kept previous candidate
  (same scene) < 1.5/255
- PNGs are extracted from the ORIGINAL video (select=`eq(n,...)` single ffmpeg pass,
  `-vsync 0`) — never re-encoded from JPEG.

## firebending inference contract (pass 2)

Endpoints (default `http://127.0.0.1:5087`, from firebending docs — see repo README/MESSAGES.md):

1. per scene: `DELETE /v1/file` (clear), then `POST /v1/file` per keep PNG (absolute path,
   `image/png`); ≤64 files & ≤64 MiB per message — engine keep sets are ~2-12 PNGs; if >64
   or >64 MiB, chunk across sequential messages into one `description.md` (`## part k` sections).
2. `POST /v1/message` with the guiding prompt (template `infer/prompts/scene.md`, scene
   meta.json + probe + adjacent scene boundary facts embedded). Reply is the model's full
   markdown → `scenes/NN-*/description.md`.
3. `504` → retry same message ≤2 times (staged files retained by daemon). Never blind-resend
   after success/partial. Busy 429 → honor `Retry-After`.
4. Pacing: sequential only, `--rate` seconds between scenes (default 1.0). The loop MUST be
   resumable: existing `description.md` skipped unless `--force`.
5. `--dry-run`: no HTTP; write `out/PROMPT.md` containing every scene's prompt + keep-paths
   manifest (this is exactly what an omp agent consumes instead).

## SKILL.md (pass 2 playbook, REH-authored)

Guides the coding agent: read `probe.json` + `scenes.json`, per scene read `meta.json`,
`description.md`, then VIEW the `keep/*.png` files directly; reconstruct layout as absolute
px on `data-width×data-height`; author one `<section class="clip">` per scene, one paused
GSAP timeline, all geometry from constants, `window.__timelines["main"] = tl`; emit
`out/index.html`. Reference the user's own hyperframes SKILL/checkout as the authority on
rendering rules.

## Engine non-negotiables

- single decode pass per video (analysis + jpeg share it); audio remux never transcodes
- all analysis math in Rust on downscaled gray; ffmpeg child processes do decode/encode only
- deterministic: same input + same cuts ⇒ byte-identical JSON contracts (paths aside)
- release profile: `opt-level=3, lto="fat", codegen-units=1, panic="abort", strip`
- no network I/O in the Rust engine at all
