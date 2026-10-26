[CmdletBinding()]
param(
    [switch]$SkipVision
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet build (Join-Path $root 'src/OpenAiBufferDaemon/OpenAiBufferDaemon.fsproj') -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'F# build failed.' }
if (-not $SkipVision) {
    uv sync --frozen --project (Join-Path $root 'optional_vision') --extra all --group dev
    if ($LASTEXITCODE -ne 0) { throw 'Vision dependency sync failed.' }
}
