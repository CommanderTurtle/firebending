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

The default gate is headless: the desktop capture/real-producer check above is
opt-in via `./scripts/Test-All.ps1 -CaptureDesktop`. The HTTP bench uses synthetic
attachments, a temporary real daemon, isolated session names, and no model
provider, Tasket action, clipboard access, or screenshot capture.

Additional regression coverage includes standalone/new-message formatting,
preservation of intended JSON/code/paths, reasoning disclosures, Unicode/MIME
and BOM-bearing attachments, private callbacks, finite missing-reply timeout,
safe retry and stale-ID rejection, disconnect cleanup, one-use file capture,
multichat off/on races, delimiter extraction/consumption, and new-route browser
guards. The original JSON and SSE contracts are checked alongside them.

The Bun dependency lock is committed, optional MCP code is typechecked, and
external-command failures now fail the gate immediately rather than being
masked by a later successful command.

Reference headless run on 2026-10-25 (.NET SDK 10.0.403, Python 3.13.14,
Bun 1.4.3):

| Check | Result |
| --- | --- |
| F# Release build | Pass, 0 warnings and 0 errors |
| Python compilation and Ruff | Pass |
| Combined Python/HTTP bench | 55 passed |
| Optional MCP frozen install and TypeScript check | Pass |
| Bun dependency audit | No known vulnerabilities reported |
| Python dependency audit | No known vulnerabilities reported; local editable project is not in the advisory index |
| NuGet dependency audit | No known vulnerable packages reported |
| Live HTTP smoke test | Pass, synthetic observations plus legacy JSON/SSE |
| Real desktop capture / live model / native OCR execution | Not performed in this headless run |

Historical desktop reference run on 2026-07-16:

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
