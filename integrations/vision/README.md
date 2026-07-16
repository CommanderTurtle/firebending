# Vision Observation Bridge

This integration keeps screen perception separate from model serving. Producers post a structured observation to the daemon, which preserves the exact JSON and a compact plaintext projection. Chat requests remain ordinary OpenAI-compatible JSON.

## Endpoint

```text
POST /v1/vision/observations
GET  /v1/vision/observations
GET  /v1/vision/observations/latest
```

A valid observation requires a non-empty `source` and an `elements` array. The canonical browser shape is documented by `vision-observation.schema.json`.

## Surfingkeys Producer

Add `surfingkeys-dom-observer.js` to Surfingkeys advanced settings. Press `Alt+V` to collect visible interactive and labeled DOM elements and post them to the local daemon.

The producer uses browser DOM geometry instead of Surfingkeys Visual mode. Visual mode is optimized for text selection; DOM geometry provides exact roles, labels, states, and bounding boxes. Surfingkeys remains the convenient keyboard trigger and notification surface.

If the endpoint is unavailable, the observation is copied to the clipboard instead of being discarded.

## Coordinate Contract

Browser observations use `css-viewport` coordinates. They are exact within the page viewport but are not claimed to be physical screen coordinates. A future Macrohelp/MCP adapter should translate them using browser-window calibration before executing cursor actions.

Native UIA and OCR producers can post the same envelope with `physical-screen` coordinates. OCR should remain a fallback for pixels that do not expose DOM or accessibility structure.
