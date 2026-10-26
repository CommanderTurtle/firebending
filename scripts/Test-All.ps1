[CmdletBinding()]
param([switch]$CaptureDesktop)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot 'Build.ps1')
uv run --project (Join-Path $root 'optional_vision') python -m compileall -q (Join-Path $root 'optional_vision/src')
if ($LASTEXITCODE -ne 0) { throw 'Python compilation failed.' }
uv run --project (Join-Path $root 'optional_vision') ruff check (Join-Path $root 'optional_vision') (Join-Path $root 'tests')
if ($LASTEXITCODE -ne 0) { throw 'Ruff lint failed.' }
uv run --project (Join-Path $root 'optional_vision') ruff format --check (Join-Path $root 'optional_vision') (Join-Path $root 'tests')
if ($LASTEXITCODE -ne 0) { throw 'Ruff format check failed.' }
uv run --project (Join-Path $root 'optional_vision') pytest (Join-Path $root 'optional_vision/tests') (Join-Path $root 'tests')
if ($LASTEXITCODE -ne 0) { throw 'Python/HTTP regression bench failed.' }
Push-Location (Join-Path $root 'optional-mcp')
try {
    bun install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw 'MCP dependency install failed.' }
    bun run check
    if ($LASTEXITCODE -ne 0) { throw 'MCP typecheck failed.' }
    bun audit
    if ($LASTEXITCODE -ne 0) { throw 'MCP dependency audit failed.' }
} finally { Pop-Location }
& (Join-Path $PSScriptRoot 'Smoke-Test.ps1') -SkipCapture:(-not $CaptureDesktop)
