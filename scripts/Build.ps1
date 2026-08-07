[CmdletBinding()]
param(
    [switch]$SkipVision
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet build (Join-Path $root 'src/OpenAiBufferDaemon/OpenAiBufferDaemon.fsproj') -c Release
if (-not $SkipVision) {
    uv sync --project (Join-Path $root 'optional_vision') --extra all --group dev
}
