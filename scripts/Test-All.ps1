[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot 'Build.ps1')
uv run --project (Join-Path $root 'optional_vision') python -m compileall -q (Join-Path $root 'optional_vision/src')
uv run --project (Join-Path $root 'optional_vision') ruff check (Join-Path $root 'optional_vision')
uv run --project (Join-Path $root 'optional_vision') ruff format --check (Join-Path $root 'optional_vision')
uv run --project (Join-Path $root 'optional_vision') pytest (Join-Path $root 'optional_vision/tests')
& (Join-Path $PSScriptRoot 'Smoke-Test.ps1')
