[CmdletBinding()]
param(
    [string]$Url = 'http://127.0.0.1:5087',
    [string]$DataDirectory = '',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:OPENAI_BUFFER_Daemon__Url = $Url
if ($DataDirectory) {
    $env:OPENAI_BUFFER_Daemon__DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
}

dotnet run --project (Join-Path $root 'src/OpenAiBufferDaemon/OpenAiBufferDaemon.fsproj') -c $Configuration
