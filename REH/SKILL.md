# SKILL: Re-author a video as HyperFrames from a REH prune

Use when a `work/<slug>/` directory exists (produced by `reh run` + `reh scenes` +
`bun infer/infer.ts`) and the job is to write the composition `out/index.html` that
re-renders the source video as HyperFrames html.

## What you have

| file | authority |
| --- | --- |
| `probe.json` | ground truth: canvas size, rational fps, frame count, duration |
| `scenes.json` | authoritative scene table (user-confirmed cuts win); `[f0,f1]` inclusive |
| `scenes/NN-*/meta.json` | per-scene facts: boundaries, motion intervals (native px), audio hints, keep-frame list |
| `scenes/NN-*/keep/*.png` | **ground-truth pixels** — lossless, full-res; name = source frame + 1 |
| `scenes/NN-*/description.md` | model-authored guess (from the infer loop). Untrusted: use as an index, verify against the PNGs you actually view |
| `out/PROMPT.md` | the same prompts + keep manifests the loop used; useful as a task list |
| your hyperframes checkout | authority on the exact composition contract — read its SKILL/docs before writing html. The skeleton below is a fallback sketch, not the contract. |

## Rules of engagement

1. **View the keep frames with your eyes** (image read), scene by scene, before writing any
   html. `description.md` accelerates orientation; it does not replace looking.
2. Every coordinate is **absolute px on the probe canvas** (`data-width`×`data-height`).
   Keep-frame PNGs are full-res — measure directly from them.
3. Times come from frame numbers: `t = f * fps_den / fps_num` (rational fps in probe.json).
   Never use wall-clock, `Math.random`, `Date.now`, or DOM measurement at tween time in the
   composition — all geometry is precomputed from constants.
4. Audio hints (beats/silence) are flavor for timing feel only. Cuts are settled; do not
   re-cut scenes to match beats.
5. If an asset is a photo/illustration you cannot author in css/svg, leave a placeholder
   `div` with the right bbox + a `<!-- ASSET: describe + from where -->` comment.

## Workflow

1. Read `probe.json` + `scenes.json`. Note scene count, durations, boundary kinds.
2. For each scene `NN` in order:
   a. read `meta.json` (boundaries, motions, `keep_f`) and `description.md`;
   b. **view** every `keep/*.png` in frame order; correct anything the description got wrong;
   c. build the element inventory (ids, bboxes, colors, text verbatim) and the motion table
      (from→to, window, easing); a pre/post pair that differ only in one region = one tween
      (or a per-item stagger if it repeats across items at ~0.1–0.3s offsets — beats hint at
      the offset rhythm);
   d. cross-check: every motion interval in `meta.json` must be explained (tween, dissolve,
      or "detector noise — bbox identical on both frames").
3. Author `out/index.html` (see skeleton). One `<section class="clip">` per scene with
   `data-start = t0`, `data-duration = (f1-f0+1)/fps`, adjacent `data-track-index`
   (or overlap starts by ~0.3s if the transition is a cross-dissolve).
4. One paused GSAP timeline: replay the motion tables in frame order, positions = absolute
   scene-local or global seconds consistently.
5. Verify with your hyperframes checkout: `hyperframes check`, then `hyperframes render`,
   then compare the rendered output against `frames/%06d.jpg` at the same timestamps
   (frame `n` ↔ `<n+1>.jpg`). Iterate until the diffs are only sub-pixel/font-rendering.

## HyperFrames skeleton (fallback sketch — your checkout's SKILL wins)

```html
<!doctype html><!-- line 1, no comment before it -->
<html lang="en">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=1920, height=1080" />
<title>REH reconstruction</title>
<script src="https://cdn.jsdelivr.net/npm/gsap@3.14.2/dist/gsap.min.js"></script>
<style>
  body { margin:0; background:#0b0f14; color:#e8eef5; font-family: system-ui, sans-serif; }
  #root { position:relative; width:100%; height:100%; overflow:hidden; }
  .clip { position:absolute; inset:0; }
</style>
</head>
<body>
<div id="root" data-composition-id="main" data-start="0"
     data-width="1920" data-height="1080" data-duration="TOTAL_S">

  <section class="clip" id="scene-00" data-start="0" data-duration="7.7" data-track-index="1">
    <!-- absolutely positioned elements from the scene-00 inventory -->
  </section>

  <!-- one section per scene, ... -->
</div>
<script>
  // precompute all geometry from constants here (no random, no Date.now)
  var tl = gsap.timeline({ paused: true });
  // tl.to("#title", { opacity: 1, duration: 0.6, ease: "power3.out" }, 0.1);
  // ...one entry per motion row, absolute times...
  window.__timelines["main"] = tl;
</script>
</body>
</html>
```

## Motion translation recipes

| evidence | composition |
| --- | --- |
| head frame lacks element, post frame has it | `tl.to(el, {opacity:1, duration, ease})` |
| pre/post pair, same element shifted | `tl.fromTo(el, {x:x0,y:y0}, {x:x1,y:y1, duration}, t)` |
| repeated items shifted in sequence | same tween in a loop, `t + i * stagger` |
| bars/lines growing from a baseline | animate `attr:{height,y}` or `scaleY` with transformOrigin bottom |
| line drawing along a path | `strokeDasharray/offset` set at build time, tween offset→0 |
| full-frame mid-motion with high bbox | incoming/outgoing clip opacity crossfade at the boundary |
| identical pre/post pair | detector noise — do not author a tween |
