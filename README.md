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
POST /v1/rgd1 ... /v1/rgd5        replace one saved rgd command
POST /v1/zuko                     write %TEMP%\tempreg.txt, then call Tasket
POST /v1/iroh                     run one saved rgd command, wrap clipboard, call Tasket
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

## Macro, Zuko, and Iroh

These routes share one replaceable Regedited output file:

```text
%TEMP%\tempreg.txt
```

Every Zuko or Iroh call rewrites that file as a canonical one-index Regedited document. Its submitted or captured multiline text is available as:

```powershell
rgd load $env:TEMP\tempreg.txt
rgd rg i1z1 --clip
```

### Select a Tasket macro

Set the Tasket macro pointer with a literal filename. Posting another filename replaces it persistently:

```powershell
Invoke-RestMethod 'http://127.0.0.1:5087/v1/macro' -Method Post -ContentType 'text/plain' -Body 'something.scht'
```

After Zuko or Iroh writes `tempreg.txt`, the daemon posts this saved filename to Tasket at `http://127.0.0.1:7777/run`.

To generate `tempreg.txt` without invoking Tasket, persist the literal value `off`:

```powershell
Invoke-RestMethod 'http://127.0.0.1:5087/v1/macro' -Method Post -ContentType 'text/plain' -Body 'off'
```

`off` affects only the final Tasket dispatch. Zuko and Iroh still perform their normal Regedited conversion and replace `%TEMP%\tempreg.txt`.

### Send text through Zuko

Send plain multiline Markdown to Zuko:

```powershell
$text = @'
# Work item

Keep this multiline body exactly as written.
'@
Invoke-RestMethod 'http://127.0.0.1:5087/v1/zuko' -Method Post -ContentType 'text/plain' -Body $text
```

Zuko preserves the multiline Markdown apart from newline normalization. A JSON string or a JSON object containing `content`, `text`, or `markdown` is accepted as well.

### Configure Iroh commands

Each numbered endpoint persistently stores one literal shell command beginning with `rgd`:

```powershell
Invoke-RestMethod 'http://127.0.0.1:5087/v1/rgd1' -Method Post -ContentType 'text/plain' -Body 'rgd rg C:\path\to\registry.txt i4z2 --clip'
```

Use `/v1/rgd2`, `/v1/rgd3`, `/v1/rgd4`, and `/v1/rgd5` identically. Posting a new command to a slot replaces only that slot. Commands may use any native Regedited operation, but the stored text must begin with `rgd`.

### Run an Iroh slot

Posting its number to Iroh runs that saved command, reads the current clipboard, replaces `%TEMP%\tempreg.txt` with the clipboard as `i1z1`, and invokes the macro currently designated by `/v1/macro`:

```powershell
Invoke-RestMethod 'http://127.0.0.1:5087/v1/iroh' -Method Post -ContentType 'text/plain' -Body '1'
```

Iroh accepts only the literal selector `1`, `2`, `3`, `4`, or `5`. The selected command is run first; the clipboard content present afterward becomes the complete `i1z1` payload. With `/v1/macro` set to `off`, the same file is produced and Tasket is skipped.

## Streaming

If the chat request contains `"stream": true`, the daemon returns an SSE stream. While waiting, it emits SSE comments like `: thinking 2026-...` so compatible clients keep the connection alive without receiving fake assistant text. When `/v1/buffer` receives the response, the daemon sends the final content chunk and `data: [DONE]`.

## Notes

- Binds to `127.0.0.1` by default.
- No API key is required.
- Unsupported `/v1/*` POST endpoints are logged and return a structured `501` JSON body.
- Multimodal Chat Completions bodies are preserved exactly, including `image_url` content parts.
- Vision producers remain separate from the daemon. See `integrations/vision` for the observation contract and Surfingkeys DOM producer.
- The older `PiProxyVLLM` proxy is reference material only; it is intentionally not bundled into this daemon.
