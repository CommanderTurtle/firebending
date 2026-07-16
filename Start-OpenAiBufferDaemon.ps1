param(
    [string]$Url = "http://127.0.0.1:5087",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "OpenAiBufferDaemon.fsproj"
$log = Join-Path $root "openai-buffer-daemon.log"
$err = Join-Path $root "openai-buffer-daemon.err.log"

$env:OPENAI_BUFFER_Daemon__Url = $Url
$args = @("run", "--project", $project)
if ($NoBuild) {
    $args += "--no-build"
}

$process = Start-Process -FilePath "dotnet" -ArgumentList $args -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError $err -PassThru
Start-Sleep -Seconds 2

[pscustomobject]@{
    Pid = $process.Id
    Url = $Url
    Health = "$Url/health"
    Log = $log
    ErrorLog = $err
}
