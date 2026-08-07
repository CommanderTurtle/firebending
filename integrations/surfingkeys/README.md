# Surfingkeys DOM Producer

Paste `surfingkeys-dom-observer.js` into Surfingkeys advanced settings. Press `Alt+V` to capture visible semantic DOM elements and post them to `http://127.0.0.1:5087/v1/vision/observations`.

The producer collects roles, accessible labels, state, DOM paths, CSS viewport boxes, normalized boxes, and a ScreenTag projection. If the local endpoint is unavailable, it copies the observation JSON to the clipboard instead.

Set `globalThis.OPENAI_BUFFER_VISION_ENDPOINT` before installing the snippet to override the endpoint.
