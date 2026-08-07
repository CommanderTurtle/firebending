# OpenAI Buffer Daemon

A tiny F# localhost daemon that accepts OpenAI-style `/v1/chat/completions` requests, writes every request to plaintext files on the Desktop, and waits until `/v1/buffer` receives the manual response JSON.

It is intentionally not a model. It is a harness: clients can talk to it like an OpenAI-compatible server while a person or local tool edits/inspects the latest request and posts the final answer back.

## Run

Requires the .NET 10 SDK/runtime on this machine.

```powershell
cd C:\path\to\openai-buffer-daemon
dotnet run
```

Or use the helper:

```powershell
.\Start-OpenAiBufferDaemon.ps1
.\Stop-OpenAiBufferDaemon.ps1
```

Default base URL:

```text
http://127.0.0.1:5087/v1
```

Override at runtime:

```powershell
$env:OPENAI_BUFFER_Daemon__Url = 'http://127.0.0.1:5091'
$env:OPENAI_BUFFER_Daemon__RequestTimeoutSeconds = '1800'
$env:OPENAI_BUFFER_Daemon__DataDirectory = "$env:TEMP\openai-buffer-daemon"
dotnet run
```

## Desktop Files

```text
Desktop\openai-buffer-daemon-requests.jsonl
Desktop\openai-buffer-daemon-latest-request.json
Desktop\openai-buffer-daemon-latest-buffer.json
Desktop\openai-buffer-daemon-state.json
Desktop\openai-buffer-daemon-vision-observations.jsonl
Desktop\openai-buffer-daemon-latest-vision-observation.json
Desktop\openai-buffer-daemon-latest-vision-observation.txt
```

The JSONL file appends every request. The latest request file is overwritten each time so it is easy to open in Notepad++.
Latest-state files are replaced atomically so local readers do not observe partially written JSON.

## Manual Response Flow

Start a request that waits:

```powershell
$body = @{
  model = 'manual-buffer-model'
  messages = @(
    @{ role = 'system'; content = 'You are a local manual harness.' },
    @{ role = 'user'; content = 'Say hello from the buffer.' }
  )
} | ConvertTo-Json -Depth 20

$job = Start-Job -ScriptBlock {
  param($body)
  Invoke-RestMethod 'http://127.0.0.1:5087/v1/chat/completions' -Method Post -ContentType 'application/json' -Body $body
} -ArgumentList $body
```

Post the response:

```powershell
Invoke-RestMethod 'http://127.0.0.1:5087/v1/buffer' -Method Post -ContentType 'application/json' -Body '{"content":"Hello from the manual buffer."}'
Receive-Job $job -Wait -AutoRemoveJob
```

The daemon also accepts a full OpenAI-shaped response in `/v1/buffer`. If the buffer JSON has a `choices` array, it is returned as-is.

## Useful Endpoints

```text
GET  /health
GET  /v1/models
GET  /v1/models/{model}
GET  /v1/buffer
GET  /v1/buffer/latest
GET  /v1/pending
GET  /v1/requests/latest
POST /v1/buffer
POST /v1/macro                    replace the saved Tasket .scht pointer
POST /v1/zuko                     write %TEMP%\tempreg.txt, then call Tasket
POST /v1/chat/completions
GET  /v1/vision/observations
GET  /v1/vision/observations/latest
POST /v1/vision/observations
POST /v1/completions              logged stub
POST /v1/responses                logged stub
POST /v1/embeddings               logged stub
POST /v1/audio/transcriptions     logged stub
POST /v1/audio/translations       logged stub
POST /tokenize                    logged stub
POST /detokenize                  logged stub
POST /rerank                      logged stub
```

## Zuko Macro Bridge

Set the Tasket macro pointer with a literal filename. Posting another filename replaces it persistently:

```powershell
Invoke-RestMethod 'http://127.0.0.1:5087/v1/macro' -Method Post -ContentType 'text/plain' -Body 'something.scht'
```

Send plain multiline Markdown to Zuko:

```powershell
$text = @'
# Work item

Keep this multiline body exactly as written.
'@
Invoke-RestMethod 'http://127.0.0.1:5087/v1/zuko' -Method Post -ContentType 'text/plain' -Body $text
```

Zuko replaces `%TEMP%\tempreg.txt` with a one-index Regedited document whose payload is `i1z1`, then posts the saved macro filename to Tasket at `http://127.0.0.1:7777/run`. JSON strings and JSON objects containing `content`, `text`, or `markdown` are accepted as well.

## Streaming

If the chat request contains `"stream": true`, the daemon returns an SSE stream. While waiting, it emits SSE comments like `: thinking 2026-...` so compatible clients keep the connection alive without receiving fake assistant text. When `/v1/buffer` receives the response, the daemon sends the final content chunk and `data: [DONE]`.

## Notes

- Binds to `127.0.0.1` by default.
- No API key is required.
- Unsupported `/v1/*` POST endpoints are logged and return a structured `501` JSON body.
- Multimodal Chat Completions bodies are preserved exactly, including `image_url` content parts.
- Vision producers remain separate from the daemon. See `integrations/vision` for the observation contract and Surfingkeys DOM producer.
- The older `PiProxyVLLM` proxy is reference material only; it is intentionally not bundled into this daemon.
