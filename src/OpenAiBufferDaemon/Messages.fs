namespace OpenAiBufferDaemon

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.StaticFiles

type StagedFile = { Id: Guid; Path: string; Mime: string option }

type FileSession = {
    Files: ResizeArray<StagedFile>
    History: ResizeArray<JsonElement>
    mutable Preserve: bool
    mutable Epoch: int64
    mutable Delimiter: string option
    mutable DelimiterEpoch: int64
    mutable Captured: string option
    Sync: obj
    Sending: SemaphoreSlim
}

module Messages =
    let private sessions = Dictionary<string, FileSession>()
    let private sessionLock = obj()
    let private mimeProvider = FileExtensionContentTypeProvider()
    let private strictUtf8 = UTF8Encoding(false, true)
    let private maxFileBytes = 64L * 1024L * 1024L

    // New endpoints can read local files. Do not inherit the legacy API's
    // wildcard CORS, and reject browser CSRF and DNS-rebinding hostnames.
    let private trusted (ctx: HttpContext) =
        let host = ctx.Request.Host.Host.Trim('[', ']')
        let localHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        || host = "127.0.0.1" || host = "::1"
        let peer = ctx.Connection.RemoteIpAddress
        let origin = string ctx.Request.Headers.Origin
        let sameOrigin = String.Equals(origin, $"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase)
        let localPeer = match peer with null -> true | address -> IPAddress.IsLoopback(address)
        localHost && localPeer
        && (origin = "" || sameOrigin)
        && string ctx.Request.Headers["Sec-Fetch-Site"] <> "cross-site"

    let guard (handler: HttpContext -> Task) (ctx: HttpContext) =
        task {
            if trusted ctx then
                // Markdown and callback bodies are not output-truncated by an
                // HTTP body cap. Files have their own explicit 64 MiB budget.
                let bodyLimit = ctx.Features.Get<Features.IHttpMaxRequestBodySizeFeature>()
                match bodyLimit with
                | null -> ()
                | limit when not limit.IsReadOnly -> limit.MaxRequestBodySize <- Nullable()
                | _ -> ()
                do! handler ctx
            else do! Formatting.write ctx 403 "This endpoint accepts loopback CLI or same-origin requests only."
        } :> Task

    let private session (ctx: HttpContext) =
        let name = if ctx.Request.Query.ContainsKey("session") then string ctx.Request.Query["session"] else "default"
        if name.Length = 0 || name.Length > 128 || name |> Seq.exists Char.IsControl then
            invalidArg "session" "session must contain 1-128 non-control characters."
        lock sessionLock (fun () ->
            match sessions.TryGetValue(name) with
            | true, value -> value
            | _ ->
                // Bound idle client identifiers without silently evicting files.
                if sessions.Count >= 128 then invalidOp "The 128 attachment sessions are full; restart the daemon to reset them."
                let value = { Files = ResizeArray(); History = ResizeArray(); Preserve = false; Epoch = 0L;
                              Delimiter = None; DelimiterEpoch = 0L; Captured = None;
                              Sync = obj(); Sending = new SemaphoreSlim(1, 1) }
                sessions.Add(name, value)
                value)

    let private stringValue key body =
        match Helpers.tryGetProperty key body with
        | Some value when value.ValueKind = JsonValueKind.String -> Some(value.GetString() |> Option.ofObj |> Option.defaultValue "")
        | _ -> None

    let private read (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body, Encoding.UTF8)
            let! raw = reader.ReadToEndAsync(ctx.RequestAborted)
            // Curl's default content type is form-urlencoded. Literal quoted
            // Windows paths must not be parsed as JSON \n / \f / \b escapes.
            let json = (string ctx.Request.ContentType).Contains("json", StringComparison.OrdinalIgnoreCase)
            return if json then Formatting.parse raw else JsonSerializer.SerializeToElement(raw)
        }

    let private literalPath (raw: string) =
        let value = raw.Trim()
        let value =
            if value.Length >= 2 && ((value[0] = '"' && value[value.Length - 1] = '"')
                                    || (value[0] = '\'' && value[value.Length - 1] = '\'')) then
                value.Substring(1, value.Length - 2)
            else value
        if String.IsNullOrWhiteSpace(value) || value |> Seq.exists Char.IsControl then
            invalidArg "path" "A literal file path without control characters is required."
        // Resolve at staging time; never interpolate into a shell command.
        let expanded = if value.StartsWith("~/") || value.StartsWith("~\\") then
                           Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.Substring(2))
                       else value
        let path = Path.GetFullPath(expanded)
        if not (File.Exists(path)) then raise (FileNotFoundException("Attachment file does not exist.", path))
        path

    let private isTextMime (mime: string) =
        mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || List.contains mime [ "application/json"; "application/xml"; "application/javascript"; "application/yaml"; "application/toml" ]

    let private decodeText (bytes: byte[]) =
        let starts (magic: byte[]) = bytes.AsSpan().StartsWith(magic.AsSpan())
        if starts [| 255uy; 254uy; 0uy; 0uy |] then UTF32Encoding(false, true, true).GetString(bytes, 4, bytes.Length - 4)
        elif starts [| 0uy; 0uy; 254uy; 255uy |] then UTF32Encoding(true, true, true).GetString(bytes, 4, bytes.Length - 4)
        elif starts [| 255uy; 254uy |] then UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2)
        elif starts [| 254uy; 255uy |] then UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2)
        else strictUtf8.GetString(bytes).TrimStart('\uFEFF')

    let private mimeType path (bytes: byte[]) explicitMime =
        match explicitMime with
        | Some mime -> mime
        | None ->
            let starts (magic: byte[]) = bytes.AsSpan().StartsWith(magic.AsSpan())
            if starts [| 137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy |] then "image/png"
            elif starts [| 255uy; 216uy; 255uy |] then "image/jpeg"
            elif starts (Encoding.ASCII.GetBytes("GIF8")) then "image/gif"
            elif bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) = "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) = "WEBP" then "image/webp"
            elif bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) = "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) = "WAVE" then "audio/wav"
            elif starts (Encoding.ASCII.GetBytes("%PDF-")) then "application/pdf"
            else
                let mutable mime = ""
                if mimeProvider.TryGetContentType(path, &mime) then mime
                else
                    try
                        let value = decodeText bytes
                        if value |> Seq.forall (fun c -> not (Char.IsControl c) || c = '\n' || c = '\r' || c = '\t') then "text/plain"
                        else "application/octet-stream"
                    with :? DecoderFallbackException -> "application/octet-stream"

    let private contentPart (file: StagedFile) (remaining: int64) cancellationToken =
        task {
            use input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous)
            if input.Length > remaining then invalidOp "Attachments exceed the 64 MiB per-message limit."
            use output = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 65536
            let mutable reading = true
            while reading do
                let! count = input.ReadAsync(chunk.AsMemory(), cancellationToken)
                if count = 0 then reading <- false
                else
                    if output.Length + int64 count > remaining then invalidOp "Attachments exceed the 64 MiB per-message limit."
                    output.Write(chunk, 0, count)
            let bytes = output.ToArray()
            let mime = mimeType file.Path bytes file.Mime
            let filename = Path.GetFileName(file.Path)
            let data = lazy (Convert.ToBase64String(bytes))
            let part: objnull =
                if isTextMime mime then
                    let value = decodeText bytes
                    box {| ``type`` = "text"; text = $"Attachment: {filename} ({mime})\n\n{value}" |}
                elif mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) then
                    box {| ``type`` = "image_url"; image_url = {| url = $"data:{mime};base64,{data.Value}" |} |}
                elif mime = "audio/wav" || mime = "audio/mpeg" then
                    box {| ``type`` = "input_audio"; input_audio = {| data = data.Value; format = if mime = "audio/wav" then "wav" else "mp3" |} |}
                else
                    box {| ``type`` = "file"; file = {| filename = filename; file_data = $"data:{mime};base64,{data.Value}" |} |}
            return part, int64 bytes.Length
        }

    let files (ctx: HttpContext) =
        task {
            try
                let state = session ctx
                if ctx.Request.Method = "GET" then
                    let names = lock state.Sync (fun () -> state.Files |> Seq.map (fun f -> {| path = f.Path; mime_type = f.Mime |}) |> Seq.toArray)
                    ctx.Response.ContentType <- "application/json; charset=utf-8"
                    ctx.Response.Headers["Cache-Control"] <- "no-store"
                    do! ctx.Response.WriteAsync(Json.serialize {| files = names |}, ctx.RequestAborted)
                elif ctx.Request.Method = "DELETE" then
                    if not (state.Sending.Wait(0)) then
                        do! Formatting.write ctx 409 "A message is using this file buffer."
                    else
                        try
                            lock state.Sync state.Files.Clear
                            do! Formatting.write ctx 200 "File buffer cleared.\n"
                        finally state.Sending.Release() |> ignore
                else
                    let! body = read ctx
                    let raw = if body.ValueKind = JsonValueKind.String then body.GetString() |> Option.ofObj
                              else stringValue "path" body
                    let path = raw |> Option.defaultWith (fun () -> invalidArg "path" "Send a literal path, JSON string, or {path, mime_type?} object.") |> literalPath
                    let mime = stringValue "mime_type" body
                    if mime |> Option.exists (fun m -> not (Text.RegularExpressions.Regex.IsMatch(m, @"^[\w.+-]+/[\w.+-]+$"))) then
                        invalidArg "mime_type" "mime_type must be a media type such as image/png."
                    lock state.Sync (fun () ->
                        if state.Files.Count >= 64 then invalidOp "The file buffer already contains 64 paths."
                        state.Files.Add({ Id = Guid.NewGuid(); Path = path; Mime = mime }))
                    do! Formatting.write ctx 200 "File staged for the next message.\n"
            with
            | :? ArgumentException as ex -> do! Formatting.write ctx 400 ex.Message
            | :? IOException as ex -> do! Formatting.write ctx 400 ex.Message
            | :? UnauthorizedAccessException as ex -> do! Formatting.write ctx 403 ex.Message
            | :? InvalidOperationException as ex -> do! Formatting.write ctx 409 ex.Message
        } :> Task

    let multichat (ctx: HttpContext) =
        task {
            try
                let state = session ctx
                if ctx.Request.Method = "POST" then
                    let! body = read ctx
                    let value = if body.ValueKind = JsonValueKind.String then body.GetString() |> Option.ofObj |> Option.defaultValue ""
                                else body.GetRawText()
                    match value.Trim() with
                    | "0" ->
                        lock state.Sync (fun () ->
                            state.Preserve <- false
                            state.Epoch <- state.Epoch + 1L
                            state.History.Clear())
                    | "1" -> lock state.Sync (fun () -> state.Preserve <- true)
                    | _ -> invalidArg "value" "Multichat accepts only 0 (off and clear) or 1 (preserve)."
                let value, count = lock state.Sync (fun () -> (if state.Preserve then 1 else 0), state.History.Count)
                do! Formatting.write ctx 200 $"multichat: {value}\nmessages: {count}\n"
            with
            | :? ArgumentException as ex -> do! Formatting.write ctx 400 ex.Message
            | :? InvalidOperationException as ex -> do! Formatting.write ctx 409 ex.Message
        } :> Task

    let delimiter (ctx: HttpContext) =
        task {
            try
                let state = session ctx
                let! body = read ctx
                if body.ValueKind <> JsonValueKind.String then invalidArg "delimiter" "Send a plaintext delimiter, or 0 to disable it."
                let marker = (body.GetString() |> Option.ofObj |> Option.defaultValue "").Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n')
                if String.IsNullOrWhiteSpace(marker) || marker.Length > 4096 then invalidArg "delimiter" "Delimiter must contain 1-4096 characters."
                lock state.Sync (fun () ->
                    state.Delimiter <- if marker = "0" then None else Some marker
                    state.DelimiterEpoch <- state.DelimiterEpoch + 1L
                    state.Captured <- None)
                do! Formatting.write ctx 200 (if marker = "0" then "Delimiter disabled; capture cleared.\n" else "Delimiter set; previous capture cleared.\n")
            with
            | :? ArgumentException as ex -> do! Formatting.write ctx 400 ex.Message
            | :? InvalidOperationException as ex -> do! Formatting.write ctx 409 ex.Message
        } :> Task

    let check (ctx: HttpContext) =
        task {
            try
                let state = session ctx
                let! body =
                    if ctx.Request.Method = "POST" then
                        task {
                            use reader = new StreamReader(ctx.Request.Body, Encoding.UTF8)
                            return! reader.ReadToEndAsync(ctx.RequestAborted)
                        }
                    else Task.FromResult("")
                let result =
                    lock state.Sync (fun () ->
                        match state.Delimiter with
                        | None -> Error (409, "Delimiter capture is disabled. Set /v1/delimiter first.")
                        | Some marker ->
                            let selected = if body = "" then state.Captured else Delimiters.extract marker body
                            match selected with
                            | None -> Error (404, "No complete delimited output is available.")
                            | Some value ->
                                state.Captured <- None
                                Ok value)
                match result with
                | Ok value -> do! Formatting.write ctx 200 value
                | Error (status, error) -> do! Formatting.write ctx status error
            with
            | :? ArgumentException as ex -> do! Formatting.write ctx 400 ex.Message
            | :? InvalidOperationException as ex -> do! Formatting.write ctx 409 ex.Message
        } :> Task

    let message defaultModel timeoutSeconds (stopping: CancellationToken) (ctx: HttpContext) =
        task {
            try
                let! body = read ctx
                let prompt =
                    if body.ValueKind = JsonValueKind.String then body.GetString() |> Option.ofObj |> Option.defaultValue ""
                    else [ "message"; "text"; "content" ] |> List.tryPick (fun key -> stringValue key body) |> Option.defaultValue ""
                if String.IsNullOrWhiteSpace(prompt) then invalidArg "message" "Send a nonempty text message or a JSON message/text/content string."
                let mode = if ctx.Request.Query.ContainsKey("escapes") then string ctx.Request.Query["escapes"] else "auto"
                if not (List.contains mode [ "auto"; "literal"; "off" ]) then invalidArg "escapes" "escapes must be auto, off, or literal."
                let model = stringValue "model" body |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.defaultValue defaultModel
                let state = session ctx
                if not (state.Sending.Wait(0)) then
                    do! Formatting.write ctx 409 "This attachment session already has a waiting message. Use another ?session= name."
                else
                    try
                        let snapshot, history, preserve, epoch, delimiterEpoch =
                            lock state.Sync (fun () -> state.Files.ToArray(), state.History.ToArray(), state.Preserve, state.Epoch, state.DelimiterEpoch)
                        let parts = ResizeArray<objnull>()
                        parts.Add(box {| ``type`` = "text"; text = prompt |})
                        let mutable remaining = maxFileBytes
                        for file in snapshot do
                            let! part, count = contentPart file remaining ctx.RequestAborted
                            parts.Add(part)
                            remaining <- remaining - count
                        let content = if snapshot.Length = 0 then box prompt else box (parts.ToArray())
                        let userMessage = JsonSerializer.SerializeToElement({| role = "user"; content = content |})
                        let envelope = JsonSerializer.SerializeToElement({| model = model; stream = false; messages = Array.append history [| userMessage |] |})
                        let requestId = Helpers.newId "msg"
                        // The legacy latest route is wildcard-CORS readable. Log a
                        // pointer there, not file bytes, paths, or the private body.
                        let publicRecord = JsonSerializer.SerializeToElement({| model = model; request_url = $"/v1/requests/{requestId}"; attachment_count = snapshot.Length |})
                        Daemon.logRequest requestId ctx "" publicRecord
                        let request = Daemon.registerPending requestId (string ctx.Request.Path) model envelope
                        ctx.Response.Headers["X-Request-Id"] <- requestId
                        use timeout = new CancellationTokenSource()
                        if timeoutSeconds > 0 then timeout.CancelAfter(TimeSpan.FromSeconds(float timeoutSeconds))
                        use linked = CancellationTokenSource.CreateLinkedTokenSource([| ctx.RequestAborted; timeout.Token; stopping |])
                        try
                            try
                                let! reply = request.Completion.Task.WaitAsync(linked.Token)
                                match Formatting.render mode reply with
                                | Error error -> do! Formatting.write ctx 502 error
                                | Ok markdown ->
                                    let assistantMessage = Formatting.assistant mode reply
                                    do! Formatting.write ctx 200 markdown
                                    // Keep paths added while this message was waiting.
                                    let consumed = snapshot |> Seq.map (fun f -> f.Id) |> Set.ofSeq
                                    lock state.Sync (fun () ->
                                        state.Files.RemoveAll(fun f -> consumed.Contains f.Id) |> ignore
                                        if state.DelimiterEpoch = delimiterEpoch then
                                            state.Captured <- state.Delimiter |> Option.bind (fun marker ->
                                                let answer = stringValue "content" assistantMessage |> Option.defaultValue ""
                                                Delimiters.extract marker answer)
                                        if preserve && state.Preserve && state.Epoch = epoch then
                                            state.History.Add(userMessage)
                                            state.History.Add(assistantMessage))
                            with :? OperationCanceledException ->
                                if not ctx.RequestAborted.IsCancellationRequested then
                                    ctx.Response.Headers["Retry-After"] <- "1"
                                    if stopping.IsCancellationRequested then
                                        do! Formatting.write ctx 503 "The daemon is stopping before a model reply was delivered."
                                    else
                                        do! Formatting.write ctx 504 "No model reply arrived before the configured message timeout. Files remain staged."
                        finally
                            Daemon.removePending requestId |> ignore
                    finally state.Sending.Release() |> ignore
            with
            | :? ArgumentException as ex -> do! Formatting.write ctx 400 ex.Message
            | :? IOException as ex -> if not ctx.RequestAborted.IsCancellationRequested then do! Formatting.write ctx 400 ex.Message
            | :? UnauthorizedAccessException as ex -> do! Formatting.write ctx 403 ex.Message
            | :? InvalidOperationException as ex -> do! Formatting.write ctx 400 ex.Message
            | :? OperationCanceledException -> ()
        } :> Task

    let requestById (ctx: HttpContext) (requestId: string) =
        task {
            match Daemon.pending.TryGetValue(requestId) with
            | true, request when request.Path = "/v1/message" ->
                ctx.Response.ContentType <- "application/json; charset=utf-8"
                ctx.Response.Headers["Cache-Control"] <- "no-store"
                do! ctx.Response.WriteAsync(Json.serialize {| request_id = request.Id; reply_url = $"/v1/requests/{request.Id}"; body = request.Body |}, ctx.RequestAborted)
            | _ -> do! Formatting.write ctx 404 "No pending message has that request ID."
        } :> Task

    /// Private callback for new messages. The legacy /v1/buffer remains usable,
    /// but this route does not copy potentially private replies to its CORS log.
    let replyById (ctx: HttpContext) (requestId: string) =
        task {
            match Daemon.pending.TryGetValue(requestId) with
            | true, request when request.Path = "/v1/message" ->
                let! body = read ctx
                match Daemon.completeById requestId body with
                | Some _ -> do! Formatting.write ctx 202 "Reply accepted.\n"
                | None -> do! Formatting.write ctx 404 "This message is no longer waiting."
            | _ -> do! Formatting.write ctx 404 "No pending message has that request ID."
        } :> Task
