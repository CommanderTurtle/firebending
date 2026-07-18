# Architecture

## F# Daemon

`src/OpenAiBufferDaemon` owns HTTP transport, the pending request queue, atomic latest-state files, and append-only JSONL history. Chat bodies are stored as received. A buffer response either passes through as a complete `choices` response or is wrapped as a Chat Completions response.

The vision route validates the stable `vision-observation/1` boundary before writing anything. It stores observations but does not infer targets or execute actions.

## Producers

`integrations/surfingkeys` is the browser producer. It uses DOM roles, labels, states, paths, and CSS viewport boxes.

`optional_vision` is the desktop fallback. Its pipeline is:

1. Capture the physical monitor or explicit region with MSS.
2. Read the foreground native application through Windows UI Automation.
3. Optionally collect text through Tesseract.
4. Optionally collect generic geometry through OpenCV.
5. Remove off-screen and duplicate regions, preferring structured sources.
6. Emit physical and normalized coordinates, ScreenTag text, and timing metadata.

Action execution belongs outside this repository. A future resolver may translate an explicitly selected observation ID into a Macrohelp or Tasket action, but that authority does not belong in perception or transport.

## Files

Latest-state files are replaced atomically. JSONL request and observation histories append one compact record per line. The data directory is configurable with `OPENAI_BUFFER_Daemon__DataDirectory`.
