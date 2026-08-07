# Contributing

Keep the daemon small and explicit. New compatibility endpoints must implement their actual contract or remain clearly labeled stubs. Observation collectors stay read-only, preserve coordinate-space metadata, and prefer structured sources over pixels.

Before submitting a change:

```powershell
./scripts/Test-All.ps1
```

Do not commit `bin`, `obj`, `.venv`, captured screens, request logs, or generated runtime state.
