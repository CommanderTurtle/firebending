[CmdletBinding()]
param(
    [switch]$SkipVision,
    [switch]$PaddleOcr
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet build (Join-Path $root 'src/OpenAiBufferDaemon/OpenAiBufferDaemon.fsproj') -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'F# build failed.' }
if (-not $SkipVision) {
    $visionArgs = @('sync', '--frozen', '--project', (Join-Path $root 'optional_vision'), '--extra', 'all', '--group', 'dev')
    if ($PaddleOcr) { $visionArgs += @('--extra', 'paddle') }
    uv @visionArgs
    if ($LASTEXITCODE -ne 0) { throw 'Vision dependency sync failed.' }
}
