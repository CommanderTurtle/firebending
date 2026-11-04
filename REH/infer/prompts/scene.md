You are reverse-engineering ONE scene of a composed motion-graphics video (HyperFrames-style:
absolute-positioned DOM/SVG elements animated on a paused timeline). You are looking at a small,
deliberately chosen set of lossless PNG frames for this scene. Machine vision metrics from the
original video are attached — they are hints, the pixels are ground truth.

{{FACTS}}

Frames arrive in ascending frame order. The first is the scene head (state before any motion or
the incoming transition's aftermath), then pre-movement / post-movement pairs for each detected
motion interval, then the scene tail. A pair that looks identical means the detector fired on
something subtle — look closely at its reported bbox.

Produce EXACTLY these markdown sections, and nothing before the first heading:

## Elements
One bullet per visible element: `id-like-name — kind (text|box|bar|line|svg-shape|image|badge),
approx bbox [x,y,w,h] px, fill/stroke colors as hex estimates, border-radius, opacity if <1`.
Infer stable ids from visual role (title, subtitle, card-bg, bar-2023, arrow-line). Group nested
elements under their parent with indentation.

## Static layout
What never changes during this scene, and at what coordinates. If most of the scene is static,
say so explicitly and list the static furniture first.

## Motion
A timeline table for everything that moves or fades:

    | t_start–t_end (s) | frames f..f | element | change | from → to | easing (best guess) |

Times may be approximate (±0.2s). Easing guesses: linear, ease-out, ease-in, back-out,
dissolve-fade. If two elements move in lockstep, merge rows. If motion within a detected
interval is actually a scene-wide dissolve, say so here and in Transition.

## Text
Every text string verbatim, one per line, with font-size px estimate, weight, color, and the
element id it belongs to. Preserve capitalization and punctuation exactly. Do not paraphrase.

## Transition
in:  what happens at the scene start (hard cut in / fade from black / dissolve from previous /
     none-observed).
out: same for the scene end, referencing the boundary frame.

## Uncertainty
Anything you could not determine, as bullets ("bar heights ±5px", "can't tell if element X
slides or pops between f12/f13"). Empty section allowed; never invent certainty you don't have.
