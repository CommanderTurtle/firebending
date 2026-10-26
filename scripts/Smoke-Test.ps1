[CmdletBinding()]
param(
    [int]$Port = 5097,
    [switch]$SkipCapture
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/OpenAiBufferDaemon/OpenAiBufferDaemon.fsproj'
$dataDirectory = Join-Path ([IO.Path]::GetTempPath()) ("openai-buffer-daemon-smoke-" + [Guid]::NewGuid().ToString('N'))
$url = "http://127.0.0.1:$Port"
$process = $null
$chatJob = $null
$streamJob = $null

try {
    New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add('run')
    $startInfo.ArgumentList.Add('--no-build')
    $startInfo.ArgumentList.Add('-c')
    $startInfo.ArgumentList.Add('Release')
    $startInfo.ArgumentList.Add('--project')
    $startInfo.ArgumentList.Add($project)
    $startInfo.Environment['OPENAI_BUFFER_Daemon__Url'] = $url
    $startInfo.Environment['OPENAI_BUFFER_Daemon__DataDirectory'] = $dataDirectory
    $process = [Diagnostics.Process]::Start($startInfo)

    $healthy = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        try {
            $health = Invoke-RestMethod "$url/health" -TimeoutSec 2
            if ($health.ok) {
                $healthy = $true
                break
            }
        } catch {
            if ($process.HasExited) {
                throw "Daemon exited during startup with code $($process.ExitCode)."
            }
        }
    }
    if (-not $healthy) { throw 'Daemon did not become healthy.' }

    $invalid = Invoke-WebRequest "$url/v1/vision/observations" -Method Post -ContentType 'application/json' -Body '{"source":"incomplete","elements":[]}' -SkipHttpErrorCheck
    if ($invalid.StatusCode -ne 400) { throw "Expected invalid observation HTTP 400, received $($invalid.StatusCode)." }

    $observation = @{
        schema_version = 'vision-observation/1'
        request_id = 'req-smoke-observation'
        source = 'smoke-test'
        captured_at = [DateTimeOffset]::UtcNow.ToString('O')
        coordinate_space = 'physical-screen'
        viewport = @{ width = 1280; height = 720; left = 0; top = 0 }
        elements = @(
            @{
                id = 'E0001'
                role = 'text'
                name = 'Smoke target'
                source = 'smoke-test'
                bbox = @{ x = 20; y = 30; width = 120; height = 24 }
                bbox_normalized = @{ x1 = 16; y1 = 42; x2 = 109; y2 = 75 }
            }
        )
    } | ConvertTo-Json -Depth 20
    $accepted = Invoke-RestMethod "$url/v1/vision/observations" -Method Post -ContentType 'application/json' -Body $observation
    if (-not $accepted.ok -or $accepted.request_id -ne 'req-smoke-observation') { throw 'Observation handshake failed.' }
    $latest = Invoke-RestMethod "$url/v1/vision/observations/latest"
    if ($latest.request_id -ne 'req-smoke-observation') { throw 'Latest observation readback lost request correlation.' }

    $schema = Invoke-RestMethod "$url/v1/vision/schema"
    if ($schema.title -ne 'Vision Observation') { throw 'Bundled schema endpoint failed.' }

    if (-not $SkipCapture) {
        $producerOutput = Join-Path $dataDirectory 'python-producer-observation.json'
        & uv run --project (Join-Path $root 'optional_vision') buffer-vision observe --no-uia --cv --post --daemon-url $url --output $producerOutput
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $producerOutput)) { throw 'Python observation producer did not complete.' }
    }

    $chat = @{ model = 'manual-buffer-model'; messages = @(@{ role = 'user'; content = 'smoke' }) } | ConvertTo-Json -Depth 10
    $chatJob = Start-Job -ScriptBlock {
        param($BaseUrl, $Body)
        Invoke-RestMethod "$BaseUrl/v1/chat/completions" -Method Post -ContentType 'application/json' -Body $Body
    } -ArgumentList $url, $chat

    $pendingId = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        $pending = Invoke-RestMethod "$url/v1/pending"
        if ($pending.pending_count -eq 1) {
            $pendingId = $pending.pending[0].request_id
            break
        }
    }
    if (-not $pendingId) { throw 'Chat request did not enter the pending queue.' }

    $buffer = @{ request_id = $pendingId; content = 'buffer handshake ok' } | ConvertTo-Json
    Invoke-RestMethod "$url/v1/buffer" -Method Post -ContentType 'application/json' -Body $buffer | Out-Null
    $response = Receive-Job $chatJob -Wait
    if ($response.choices[0].message.content -ne 'buffer handshake ok') { throw 'Chat response did not contain the buffer content.' }
    Remove-Job $chatJob -Force
    $chatJob = $null

    $streamBody = @{ model = 'manual-buffer-model'; stream = $true; messages = @(@{ role = 'user'; content = 'stream smoke' }) } | ConvertTo-Json -Depth 10
    $streamJob = Start-Job -ScriptBlock {
        param($BaseUrl, $Body)
        Invoke-WebRequest "$BaseUrl/v1/chat/completions" -Method Post -ContentType 'application/json' -Body $Body
    } -ArgumentList $url, $streamBody

    $streamRequestId = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        $pending = Invoke-RestMethod "$url/v1/pending"
        if ($pending.pending_count -eq 1) {
            $streamRequestId = $pending.pending[0].request_id
            break
        }
    }
    if (-not $streamRequestId) { throw 'Streaming request did not enter the pending queue.' }

    $streamBuffer = @{ request_id = $streamRequestId; content = 'stream handshake ok' } | ConvertTo-Json
    Invoke-RestMethod "$url/v1/buffer" -Method Post -ContentType 'application/json' -Body $streamBuffer | Out-Null
    $streamResponse = Receive-Job $streamJob -Wait
    if ($streamResponse.Content -notmatch 'stream handshake ok' -or $streamResponse.Content -notmatch '\[DONE\]') {
        throw 'Streaming response did not emit content and the terminal marker.'
    }

    Write-Host 'Daemon smoke test passed: validation, schema discovery, buffered chat, and SSE completion.'
    if ($SkipCapture) { Write-Host 'Desktop capture not requested; synthetic observations were used.' }
} finally {
    if ($chatJob) { Remove-Job $chatJob -Force -ErrorAction SilentlyContinue }
    if ($streamJob) { Remove-Job $streamJob -Force -ErrorAction SilentlyContinue }
    if ($process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedData = [IO.Path]::GetFullPath($dataDirectory)
    if ($resolvedData.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedData).StartsWith('openai-buffer-daemon-smoke-')) {
        Remove-Item -LiteralPath $resolvedData -Recurse -Force -ErrorAction SilentlyContinue
    } else { throw 'Refusing cleanup outside the owned smoke-test directory.' }
}
