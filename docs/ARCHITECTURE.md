# Architecture

## F# Daemon

The old single-file implementation is separated along its existing boundaries:
`Core.fs` (JSON/files/compatibility response shapes), `Queue.fs` (waiting requests
and legacy chat/buffer), `Automation.fs` (unchanged Zuko/Iroh workflows),
`Vision.fs` (read-only observation contract), and `Program.fs` (composition/routes).
New code lives in `Formatting.fs`, `Delimiters.fs`, and `Messages.fs`, not in the
compatibility response normalizer. This keeps Markdown presentation from
changing existing OpenAI-compatible JSON responses.

`Inference.fs` supplies a separate on-demand NInfer client, bounded input/response
pipes, deadlines and one shared heavy-operation lane. `Jev.fs` owns in-memory
draft/refinement/activated frameworks and validates native SystemOne responses.
`Perception.fs` owns the raw Cereal pipe/resolution selector and independently
selected cold OCR helper. Cereal uses local raster/stroke/shade calculations in
`optional_vision/cereal.py`, never NInfer or an OCR model. Its full-canvas grid
retains blank rows/columns and alpha; only Jev interprets it when explicitly
called. None of these enters the manual pending queue, consumes staged files,
persists request bodies, captures a screen or executes model-generated tools.

`src/OpenAiBufferDaemon` owns HTTP transport, the pending request queue, atomic latest-state files, and append-only JSONL history. Chat bodies are stored as received. A buffer response either passes through as a complete `choices` response or is wrapped as a Chat Completions response.

The vision route validates the stable `vision-observation/1` boundary before writing anything. It stores observations but does not infer targets or execute actions.

`/v1/message` shares the pending queue, but has its own finite wait deadline and
private body/reply lookup. Its formatter is exactly `/v1/format`'s formatter.
One per-session send lock protects attachment/history ordering. Successful
output commits only that send's file snapshot and conversation pair. Epochs
prevent in-flight replies from restoring history/capture cleared by a switch.
Delimiter capture stores only the extracted final-answer section and is disabled
by default. See [MESSAGES.md](MESSAGES.md) for the complete lifecycle.

## Producers

`integrations/surfingkeys` is the browser producer. It uses DOM roles, labels, states, paths, and CSS viewport boxes.

`optional_vision` is the desktop fallback. Its pipeline is:

1. Capture the physical monitor or explicit region with MSS.
2. Read the foreground native application through Windows UI Automation.
3. Optionally collect text through Tesseract (default) or opt-in Paddle.
4. Optionally collect generic geometry through OpenCV.
5. Remove off-screen and duplicate regions, preferring structured sources.
6. Emit physical and normalized coordinates, ScreenTag text, and timing metadata.

Action execution belongs outside this repository. A future resolver may translate an explicitly selected observation ID into a Macrohelp or Tasket action, but that authority does not belong in perception or transport.

## Files

Latest-state files are replaced atomically. JSONL request and observation histories append one compact record per line. The data directory is configurable with `OPENAI_BUFFER_Daemon__DataDirectory`.
