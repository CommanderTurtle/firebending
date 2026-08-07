# Testing

Run the complete local gate with:

```powershell
./scripts/Test-All.ps1
```

The gate requires:

- .NET release build with zero warnings.
- Python bytecode compilation.
- Ruff lint and format checks.
- Unit tests for coordinate normalization, schema generation, deduplication, CV semantics, and OCR line grouping.
- Live F# validation rejection and acceptance.
- Live schema discovery.
- A real MSS and OpenCV desktop observation posted through the Python client.
- Pending Chat Completions fulfillment through `/v1/buffer`.
- Streaming Chat Completions content and `[DONE]` completion.

Reference run on 2026-07-16:

| Check | Result |
| --- | --- |
| F# release build | Pass, 0 warnings and 0 errors |
| Ruff | Pass |
| Python tests | 7 passed |
| Live UIA foreground-window observation | 14 inspected, 6 emitted, 0 off-screen |
| Live OpenCV desktop observation | 237 emitted in about 210 ms |
| Surfingkeys browser bundle | Pass |
| Native Tesseract executable | Not installed; adapter grouping is unit tested and `doctor` reports the missing executable |

Machine-specific element counts and timings are informational, not assertions.
