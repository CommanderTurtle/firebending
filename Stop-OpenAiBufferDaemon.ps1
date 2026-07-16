param(
    [int]$Port = 5087
)

$listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
foreach ($listener in $listeners) {
    $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    if ($process) {
        Stop-Process -Id $process.Id -Force
        [pscustomobject]@{
            StoppedPid = $process.Id
            ProcessName = $process.ProcessName
            Port = $Port
        }
    }
}
