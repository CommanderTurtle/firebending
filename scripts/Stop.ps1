[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Split-Path -Parent $PSScriptRoot).ToLowerInvariant()
$matches = Get-CimInstance Win32_Process | Where-Object {
    $_.CommandLine -and
    $_.CommandLine.ToLowerInvariant().Contains($root) -and
    ($_.CommandLine -match 'OpenAiBufferDaemon|dotnet\s+run')
}

foreach ($process in $matches) {
    Stop-Process -Id $process.ProcessId -Force
    Write-Host "Stopped OpenAI Buffer Daemon process $($process.ProcessId)."
}

if (-not $matches) {
    Write-Host 'No daemon process from this repository is running.'
}
