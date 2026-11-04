# REH — Reverse Engineer HyperFrames

Take a composed motion-graphics mp4 (HyperFrames-style: 30 fps, up to ~1440p, DOM/SVG look)
and prune it into a deliberate library — scenes, cuts, motion intervals, and a handful of
lossless keep-frames per scene — so a coding agent can re-author the video as a HyperFrames
`index.html` from near-zero guesswork.

```
pass 1 (REH, programmatic)              pass 2 (human + coding agent)
mp4 → probe → lossless audio → frames   open an agent on work/<slug>/
    → motion/cut inference → scene      with REH/SKILL.md (+ your own hyperframes
    folders → keep-frame PNGs →         SKILL/checkout). It reads meta.json +
    per-scene model descriptions via    description.md, VIEWS the keep PNGs,
    the firebending httpd               writes out/index.html.
```

HyperFrames itself is **not** a dependency — REH never renders anything. See
[DESIGN.md](DESIGN.md) for the full architecture and the `work/<slug>/` JSON contracts.

## Requirements

- `ffmpeg` + `ffprobe` on PATH (hwaccel auto-detected: cuda → vaapi → qsv → none)
- [Bun](https://bun.sh) for `preview/` and `infer/`
- a running [firebending](https://github.com/CommanderTurtle/firebending) httpd (default
  `http://127.0.0.1:5087`) with an active model behind it, for `infer` only

## Use

```sh
cd engine && cargo build --release        # ./target/release/reh
./target/release/reh run ~/videos/demo.mp4
# → work/demo/  probe.json waveform.json frames/ frames.json motion.json
#              scenes.json scenes/00-000000/{meta.json,keep/*.png,notes.md} ...

./target/release/reh scenes review work/demo     # inspect inferred cuts + beat hints
./target/release/reh scenes apply  work/demo --lgtm
./target/release/reh scenes apply  work/demo --cuts "00:00:12, 00:00:39"

bun preview/server.ts work/demo        # or: reh preview work/demo
# browser timeline: waveform + inferred cuts + beats + motion strip, [ADD CUT] → cuts.json

bun infer/infer.ts work/demo           # or: reh infer work/demo
# per scene: stage keep PNGs (POST /v1/file) → guiding prompt (POST /v1/message)
# → wait for the active model's markdown → scenes/*/description.md
# --dry-run does zero HTTP and writes out/PROMPT.md for the inside-agent path

./target/release/reh status work/demo  # what's done, what's next
```

Env: `REH_ROOT` (repo layout override), `REH_WORK` (default work root), `REH_PORT` (preview).

## Layout

| dir | what |
| --- | --- |
| `engine/` | Rust `reh` CLI — the whole programmatic pass 1 |
| `preview/` | Bun cut-editor server (video + scrub + waveform + ADD CUT) |
| `infer/` | Bun firebending poll-loop driver + `prompts/scene.md` guiding prompt |
| `SKILL.md` | pass-2 playbook the coding agent reads |
| `work/` | generated per-video output (gitignored) |
| `JOURNAL.md` | design journal (gitignored) |
