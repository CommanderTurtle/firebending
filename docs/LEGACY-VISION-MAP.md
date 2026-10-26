# Legacy Vision Map

The old `Refactor.zip` was treated as research input, not merged source.

| Legacy idea | Disposition |
| --- | --- |
| MSS desktop capture | Rewritten in `optional_vision/capture.py` |
| Windows UI Automation | Rewritten with foreground-window, depth, count, and viewport limits |
| ScreenTag representation | Preserved as deterministic observation text |
| OpenCV contours | Preserved as low-confidence `visual-region` geometry |
| Tesseract OCR | Preserved as an optional adapter with a clear native dependency diagnostic |
| Source-priority deduplication | Rewritten and unit tested |
| Labeled overlay image | Preserved as an optional diagnostic artifact |
| Screen-grid/spatial serialization | Referenced by Cereal's full-canvas deterministic raster/stroke/shade pipe; independent of OCR/models |
| PyAutoGUI action loop | Excluded; perception remains read-only |
| Rust capture/UIA/OCR/CV stubs | Excluded; they did not implement those paths |
| Unbuildable TypeScript agent extensions | Excluded |
| Duplicate F# proxy and embedded dashboard | Excluded; maintained daemon remains the transport source of truth |
| PowerShell prototype | Excluded after parser failure; no unique runtime capability was lost |
