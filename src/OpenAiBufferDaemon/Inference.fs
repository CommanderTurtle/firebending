namespace OpenAiBufferDaemon

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

exception PipeFailure of int * string

type InferenceOptions = {
    BaseUrl: string
    DaemonUrl: string
    Model: string
    ApiKey: string
    TimeoutSeconds: int
    MaxTokens: int
    OcrPython: string
}

/// On-demand inference only. Independent of the manual response queue and file buffer.
module Inference =
    let private handler = new HttpClientHandler(UseProxy = false, AllowAutoRedirect = false)
    let private client = new HttpClient(handler, Timeout = Timeout.InfiniteTimeSpan)
    let private lane = new SemaphoreSlim(1, 1)
    let private utf8 = UTF8Encoding(false, true)
    let json value = JsonSerializer.SerializeToElement(value)
    let fail status message = raise (PipeFailure(status, message))
    let stringValue (value: JsonElement) = value.GetString() |> Option.ofObj |> Option.defaultValue ""
    let tryString name value =
        Helpers.tryGetProperty name value
        |> Option.filter (fun field -> field.ValueKind = JsonValueKind.String)
        |> Option.map stringValue

    let codeBlock language (text: string) =
        let longest = Regex.Matches(text, "`+") |> Seq.cast<Match> |> Seq.map (fun m -> m.Length) |> Seq.fold max 0
        let fence = String('`', max 3 (longest + 1))
        $"{fence}{language}\n{text.TrimEnd('\r', '\n')}\n{fence}\n"

    let readBytes limit (ct: CancellationToken) (ctx: HttpContext) =
        task {
            if ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > int64 limit then
                fail 413 "The one-shot request exceeds its size limit."
            use output = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 65536
            let mutable reading = true
            while reading do
                let! count = ctx.Request.Body.ReadAsync(buffer.AsMemory(), ct)
                if count = 0 then reading <- false
                else
                    if output.Length + int64 count > int64 limit then fail 413 "The one-shot request exceeds its size limit."
                    output.Write(buffer, 0, count)
            return output.ToArray()
        }

    let readText limit ct ctx = task {
        let! bytes = readBytes limit ct ctx
        return utf8.GetString(bytes).TrimStart('\uFEFF')
    }

    let textBody (ctx: HttpContext) (raw: string) =
        if (string ctx.Request.ContentType).Contains("json", StringComparison.OrdinalIgnoreCase) then
            use doc = JsonDocument.Parse(raw)
            if doc.RootElement.ValueKind = JsonValueKind.String then stringValue doc.RootElement
            else
                [ "message"; "text"; "content" ]
                |> List.tryPick (fun key -> Helpers.tryGetProperty key doc.RootElement)
                |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
                |> Option.map stringValue
                |> Option.defaultWith (fun () -> fail 400 "Send text, a JSON string, or a message/text/content string.")
        else raw

    let image ct (ctx: HttpContext) = task {
        let! input = readBytes (32 * 1024 * 1024) ct ctx
        let contentType = string ctx.Request.ContentType
        let! bytes = task {
            if contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) then
                return input
            else
                let raw = utf8.GetString(input).Trim()
                let value =
                    if contentType.Contains("json", StringComparison.OrdinalIgnoreCase) then
                        use doc = JsonDocument.Parse(raw)
                        if doc.RootElement.ValueKind = JsonValueKind.String then stringValue doc.RootElement
                        else
                            [ "path"; "image_data_url" ] |> List.tryPick (fun key -> Helpers.tryGetProperty key doc.RootElement)
                            |> Option.filter (fun item -> item.ValueKind = JsonValueKind.String)
                            |> Option.map stringValue
                            |> Option.defaultWith (fun () -> fail 400 "Send an image path, image_data_url, or raw image bytes.")
                    else raw
                if value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) then
                    let comma = value.IndexOf(',')
                    if comma < 0 || not (value.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) then
                        fail 400 "Image data URLs must contain base64 bytes."
                    try return Convert.FromBase64String(value.Substring(comma + 1))
                    with :? FormatException -> return fail 400 "Invalid base64 image."
                else
                    let path =
                        if value.Length >= 2 && ((value[0] = '"' && value[value.Length-1] = '"') || (value[0] = '\'' && value[value.Length-1] = '\'')) then value.Substring(1, value.Length-2)
                        else value
                    if String.IsNullOrWhiteSpace(path) || path |> Seq.exists Char.IsControl then fail 400 "Send one literal local image path."
                    if path.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https:", StringComparison.OrdinalIgnoreCase) then fail 400 "Remote image URLs are not fetched. Send local bytes or a path."
                    let expanded = if path.StartsWith("~/") || path.StartsWith("~\\") then Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2)) else path
                    if OperatingSystem.IsWindows() && expanded.StartsWith("\\\\", StringComparison.Ordinal) then fail 400 "UNC/device paths are not accepted; send local image bytes instead."
                    use file = new FileStream(Path.GetFullPath(expanded), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous)
                    if file.Length > 20L * 1024L * 1024L then fail 413 "Image exceeds 20 MiB."
                    use output = new MemoryStream()
                    let buffer = Array.zeroCreate<byte> 65536
                    let mutable reading = true
                    while reading do
                        let! count = file.ReadAsync(buffer.AsMemory(), ct)
                        if count = 0 then reading <- false
                        else
                            if output.Length + int64 count > 20L * 1024L * 1024L then fail 413 "Image exceeds 20 MiB."
                            output.Write(buffer, 0, count)
                    return output.ToArray()
        }
        if bytes.Length = 0 || bytes.Length > 20 * 1024 * 1024 then fail 413 "Image must contain 1 byte through 20 MiB."
        return bytes
    }

    let imageUrl (bytes: byte[]) =
        let starts (magic: byte[]) = bytes.AsSpan().StartsWith(magic.AsSpan())
        let mime =
            if starts [| 137uy;80uy;78uy;71uy;13uy;10uy;26uy;10uy |] then "image/png"
            elif starts [| 255uy;216uy;255uy |] then "image/jpeg"
            elif starts (Encoding.ASCII.GetBytes("GIF8")) then "image/gif"
            elif bytes.Length >= 12 && Encoding.ASCII.GetString(bytes,0,4) = "RIFF" && Encoding.ASCII.GetString(bytes,8,4) = "WEBP" then "image/webp"
            else fail 415 "NInfer vision input must be PNG, JPEG, GIF or WebP. OCR accepts other Pillow-supported images."
        $"data:{mime};base64,{Convert.ToBase64String(bytes)}"

    let origin (options: InferenceOptions) =
        let mutable uri = Unchecked.defaultof<Uri>
        if not (Uri.TryCreate(options.BaseUrl.TrimEnd('/'), UriKind.Absolute, &uri))
           || not (List.contains uri.Scheme [ "http"; "https" ])
           || uri.UserInfo <> "" || uri.Query <> "" || uri.Fragment <> ""
           || not (List.contains uri.AbsolutePath [ "/"; "/v1" ]) then
            fail 503 "Configure Daemon:NinferBaseUrl as an HTTP(S) origin or /v1 URL without credentials."
        // Validate only when called: a legacy broker on port 8000 must still start.
        for address in options.DaemonUrl.Split(';', StringSplitOptions.RemoveEmptyEntries) do
            let mutable own = Unchecked.defaultof<Uri>
            if Uri.TryCreate(address, UriKind.Absolute, &own) && uri.IsLoopback && own.Port = uri.Port
               && (own.IsLoopback || List.contains own.Host [ "0.0.0.0"; "[::]"; "::" ]) then
                fail 503 "NInfer points back to this manual buffer daemon; configure a separate NinferBaseUrl."
        uri.GetLeftPart(UriPartial.Authority)

    let request options path (body: JsonElement option) (ct: CancellationToken) = task {
        let mutable result = None
        let mutable attempt = 0
        while result.IsNone do
            ct.ThrowIfCancellationRequested()
            use message = new HttpRequestMessage((if body.IsSome then HttpMethod.Post else HttpMethod.Get), origin options + path)
            if options.ApiKey <> "" then message.Headers.Authorization <- AuthenticationHeaderValue("Bearer", options.ApiKey)
            match body with
            | Some value -> message.Content <- new StringContent(value.GetRawText(), Encoding.UTF8, "application/json")
            | None -> ()
            use! response = client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
            let busy = int response.StatusCode = 429 || int response.StatusCode = 423
            if busy && attempt < 2 then
                let requested =
                    match response.Headers.RetryAfter with
                    | null -> 0.0
                    | retry when retry.Delta.HasValue -> retry.Delta.Value.TotalMilliseconds
                    | retry when retry.Date.HasValue -> (retry.Date.Value - DateTimeOffset.UtcNow).TotalMilliseconds
                    | _ -> 0.0
                // The linked operation deadline bounds queueing, retries and all model calls.
                let delay = max (100.0 * (2.0 ** float attempt)) requested
                do! Task.Delay(TimeSpan.FromMilliseconds(min delay 86400000.0), ct)
                attempt <- attempt + 1
            elif not response.IsSuccessStatusCode then
                let status = if busy then 429 else 502
                fail status $"NInfer {path} returned HTTP {int response.StatusCode}. Check the configured model and --jev support."
            else
                use! stream = response.Content.ReadAsStreamAsync(ct)
                use output = new MemoryStream()
                let buffer = Array.zeroCreate<byte> 65536
                let mutable reading = true
                while reading do
                    let! count = stream.ReadAsync(buffer.AsMemory(), ct)
                    if count = 0 then reading <- false
                    else
                        if output.Length + int64 count > 8L * 1024L * 1024L then fail 502 "NInfer response exceeds 8 MiB."
                        output.Write(buffer, 0, count)
                try
                    use doc = JsonDocument.Parse(output.ToArray())
                    result <- Some(doc.RootElement.Clone())
                with :? JsonException -> fail 502 "NInfer returned invalid JSON."
        return result.Value
    }

    let model options ct = task {
        let! id = task {
            if not (String.IsNullOrWhiteSpace options.Model) then return options.Model
            else
                let! catalog = request options "/v1/models" None ct
                match Helpers.tryGetProperty "data" catalog with
                | Some data when data.ValueKind = JsonValueKind.Array && data.GetArrayLength() = 1 ->
                    return tryString "id" data[0] |> Option.filter (String.IsNullOrWhiteSpace >> not)
                           |> Option.defaultWith (fun () -> fail 502 "NInfer returned no model id.")
                | _ -> return fail 503 "Set Daemon:NinferModel when NInfer does not advertise exactly one model."
        }
        if Encoding.UTF8.GetByteCount(id) > 4096 || id |> Seq.exists Char.IsControl then fail 503 "Configured/advertised NInfer model id must be printable and at most 4096 UTF-8 bytes."
        return id
    }

    let submitTool options modelId toolName description parameterName messages ct = task {
        let schema =
            json {| ``type`` = "object"; properties = dict [ parameterName, box {| ``type`` = "string" |} ]; required = [| parameterName |]; additionalProperties = false |}
        let body = json {| model = modelId; messages = messages; stream = false; temperature = 0;
                           max_tokens = options.MaxTokens; chat_template_kwargs = {| enable_thinking = false |};
                           tools = [| {| ``type`` = "function"; ``function`` = {| name = toolName; description = description; strict = true; parameters = schema |} |} |];
                           tool_choice = {| ``type`` = "function"; ``function`` = {| name = toolName |} |}; parallel_tool_calls = false |}
        let! response = request options "/v1/chat/completions" (Some body) ct
        let choices = Helpers.tryGetProperty "choices" response |> Option.defaultWith (fun () -> fail 502 "NInfer returned no choices.")
        if choices.ValueKind <> JsonValueKind.Array || choices.GetArrayLength() <> 1 then fail 502 "NInfer must return one completion."
        let finish = tryString "finish_reason" choices[0] |> Option.defaultValue ""
        if not (List.contains finish [ "stop"; "tool_calls" ]) then fail 502 "NInfer output was incomplete or token-limited; increase NinferMaxTokens/context."
        let message = Helpers.tryGetProperty "message" choices[0] |> Option.defaultWith (fun () -> fail 502 "Missing assistant message.")
        let calls = Helpers.tryGetProperty "tool_calls" message |> Option.defaultWith (fun () -> invalidArg "tool_call" "Return exactly one required tool call, not prose.")
        if calls.ValueKind <> JsonValueKind.Array || calls.GetArrayLength() <> 1 then invalidArg "tool_call" "Return exactly one tool call."
        if tryString "type" calls[0] <> Some "function" then invalidArg "tool_call" "Return a function tool call."
        let fn = Helpers.tryGetProperty "function" calls[0] |> Option.defaultWith (fun () -> invalidArg "tool_call" "Missing function.")
        if tryString "name" fn <> Some toolName then invalidArg "tool_call" "Unexpected tool name."
        let args = Helpers.tryGetProperty "arguments" fn |> Option.defaultWith (fun () -> invalidArg "tool_call" "Missing tool arguments.")
        if args.ValueKind <> JsonValueKind.String then invalidArg "tool_call" "Tool arguments must be JSON text."
        use doc = JsonDocument.Parse(stringValue args)
        if doc.RootElement.ValueKind <> JsonValueKind.Object || doc.RootElement.EnumerateObject() |> Seq.length <> 1 then invalidArg "tool_call" "Tool must return only the requested property."
        match Helpers.tryGetProperty parameterName doc.RootElement with
        | Some value when value.ValueKind = JsonValueKind.String -> return stringValue value
        | _ -> return invalidArg "tool_call" $"Tool must return a {parameterName} string."
    }

    let inLane (operation: unit -> Task<'T>) = task {
        if not (lane.Wait(0)) then fail 429 "A perception/decision request is active; retry when it completes."
        try return! operation()
        finally lane.Release() |> ignore
    }

    let runUnlocked options (stopping: CancellationToken) (operation: CancellationToken -> HttpContext -> Task) (ctx: HttpContext) : Task = task {
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping)
        deadline.CancelAfter(TimeSpan.FromSeconds(float options.TimeoutSeconds))
        try
            do! operation deadline.Token ctx
        with
        | PipeFailure(status, message) ->
            if status = 429 then ctx.Response.Headers.RetryAfter <- "1"
            do! Formatting.write ctx status message
        | :? OperationCanceledException when ctx.RequestAborted.IsCancellationRequested -> ()
        | :? OperationCanceledException -> do! Formatting.write ctx (if stopping.IsCancellationRequested then 503 else 504) "Operation cancelled or its bounded deadline expired."
        | :? JsonException -> do! Formatting.write ctx 400 "Invalid JSON."
        | :? ArgumentException as ex -> do! Formatting.write ctx 400 ex.Message
        | :? FileNotFoundException -> do! Formatting.write ctx 404 "Image file or OCR helper was not found."
        | :? UnauthorizedAccessException -> do! Formatting.write ctx 403 "The supplied image file is not readable."
        | :? IOException -> do! Formatting.write ctx 400 "Could not read the supplied image."
        | :? HttpRequestException -> do! Formatting.write ctx 503 "Cannot reach configured NInfer."
        | :? System.ComponentModel.Win32Exception -> do! Formatting.write ctx 503 "OCR Python is unavailable; configure Daemon:OcrPython and install optional_vision OCR extras."
    }

    let run options stopping (operation: CancellationToken -> HttpContext -> Task) ctx =
        runUnlocked options stopping (fun ct context -> (inLane (fun () -> task { do! operation ct context }) :> Task)) ctx
