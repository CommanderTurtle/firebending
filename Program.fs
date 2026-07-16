namespace OpenAiBufferDaemon

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type PendingRequest = {
    Id: string
    Created: DateTimeOffset
    Path: string
    Model: string
    Body: JsonElement
    Completion: TaskCompletionSource<JsonElement>
}

module Json =
    let options =
        let o = JsonSerializerOptions(WriteIndented = true)
        o.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        o

    let compactOptions = JsonSerializerOptions(WriteIndented = false)

    let serialize value = JsonSerializer.Serialize(value, options)
    let serializeCompact value = JsonSerializer.Serialize(value, compactOptions)

module Helpers =
    let utcNow() = DateTimeOffset.UtcNow
    let unixNow() = utcNow().ToUnixTimeSeconds()
    let newId prefix = $"{prefix}-{Guid.NewGuid():N}"

    let tryGetProperty (name: string) (element: JsonElement) =
        let mutable prop = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &prop) then
            Some prop
        else
            None

    let tryGetString name element =
        match tryGetProperty name element with
        | Some prop when prop.ValueKind = JsonValueKind.String -> prop.GetString() |> Option.ofObj
        | Some prop when prop.ValueKind <> JsonValueKind.Null && prop.ValueKind <> JsonValueKind.Undefined -> Some (prop.ToString())
        | _ -> None

    let tryGetBool name element =
        match tryGetProperty name element with
        | Some prop when prop.ValueKind = JsonValueKind.True -> Some true
        | Some prop when prop.ValueKind = JsonValueKind.False -> Some false
        | _ -> None

    let modelFromBody fallback body =
        tryGetString "model" body |> Option.defaultValue fallback

    let rec contentFromBuffer (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.String -> element.GetString() |> Option.ofObj |> Option.defaultValue ""
        | JsonValueKind.Object ->
            match tryGetString "content" element with
            | Some content -> content
            | None ->
                match tryGetProperty "message" element with
                | Some msg -> contentFromBuffer msg
                | None ->
                    match tryGetProperty "response" element with
                    | Some response -> contentFromBuffer response
                    | None ->
                        match tryGetProperty "text" element with
                        | Some text -> contentFromBuffer text
                        | None ->
                            match tryGetProperty "choices" element with
                            | Some choices when choices.ValueKind = JsonValueKind.Array && choices.GetArrayLength() > 0 ->
                                let first = choices[0]
                                match tryGetProperty "message" first with
                                | Some message -> contentFromBuffer message
                                | None ->
                                    match tryGetProperty "delta" first with
                                    | Some delta -> contentFromBuffer delta
                                    | None ->
                                        tryGetString "text" first |> Option.defaultValue (element.GetRawText())
                            | _ -> element.GetRawText()
        | _ -> element.GetRawText()

    let isOpenAiResponse (element: JsonElement) =
        match tryGetProperty "choices" element with
        | Some choices when choices.ValueKind = JsonValueKind.Array -> true
        | _ -> false

module Files =
    let private defaultDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)

    let mutable dataDirectory = defaultDirectory
    let mutable requestLogPath = Path.Combine(dataDirectory, "openai-buffer-daemon-requests.jsonl")
    let mutable latestRequestPath = Path.Combine(dataDirectory, "openai-buffer-daemon-latest-request.json")
    let mutable latestBufferPath = Path.Combine(dataDirectory, "openai-buffer-daemon-latest-buffer.json")
    let mutable statePath = Path.Combine(dataDirectory, "openai-buffer-daemon-state.json")
    let mutable observationLogPath = Path.Combine(dataDirectory, "openai-buffer-daemon-vision-observations.jsonl")
    let mutable latestObservationPath = Path.Combine(dataDirectory, "openai-buffer-daemon-latest-vision-observation.json")
    let mutable latestObservationTextPath = Path.Combine(dataDirectory, "openai-buffer-daemon-latest-vision-observation.txt")

    let private fileLock = obj()
    let utf8NoBom = UTF8Encoding(false)

    let configure directory =
        let resolved =
            if String.IsNullOrWhiteSpace(directory) then defaultDirectory
            else Path.GetFullPath(directory)

        dataDirectory <- resolved
        requestLogPath <- Path.Combine(dataDirectory, "openai-buffer-daemon-requests.jsonl")
        latestRequestPath <- Path.Combine(dataDirectory, "openai-buffer-daemon-latest-request.json")
        latestBufferPath <- Path.Combine(dataDirectory, "openai-buffer-daemon-latest-buffer.json")
        statePath <- Path.Combine(dataDirectory, "openai-buffer-daemon-state.json")
        observationLogPath <- Path.Combine(dataDirectory, "openai-buffer-daemon-vision-observations.jsonl")
        latestObservationPath <- Path.Combine(dataDirectory, "openai-buffer-daemon-latest-vision-observation.json")
        latestObservationTextPath <- Path.Combine(dataDirectory, "openai-buffer-daemon-latest-vision-observation.txt")
        Directory.CreateDirectory(dataDirectory) |> ignore

    let ensureDataDirectory() =
        Directory.CreateDirectory(dataDirectory) |> ignore

    let writeText (path: string) (text: string) =
        lock fileLock (fun () ->
            ensureDataDirectory()
            let temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
            try
                File.WriteAllText(temporaryPath, text, utf8NoBom)
                File.Move(temporaryPath, path, true)
            finally
                if File.Exists(temporaryPath) then File.Delete(temporaryPath)
        )

    let appendLine (path: string) (text: string) =
        lock fileLock (fun () ->
            ensureDataDirectory()
            File.AppendAllText(path, text + Environment.NewLine, utf8NoBom)
        )

module Responses =
    let private writeCors (ctx: HttpContext) =
        ctx.Response.Headers["Access-Control-Allow-Origin"] <- "*"
        ctx.Response.Headers["Access-Control-Allow-Headers"] <- "authorization,content-type"
        ctx.Response.Headers["Access-Control-Allow-Methods"] <- "GET,POST,OPTIONS"
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] <- "true"

    let writeJson (ctx: HttpContext) status value =
        task {
            writeCors ctx
            ctx.Response.StatusCode <- status
            ctx.Response.ContentType <- "application/json; charset=utf-8"
            let json = Json.serialize value
            do! ctx.Response.WriteAsync(json)
        }

    let writeJsonElement (ctx: HttpContext) status (value: JsonElement) =
        task {
            writeCors ctx
            ctx.Response.StatusCode <- status
            ctx.Response.ContentType <- "application/json; charset=utf-8"
            do! JsonSerializer.SerializeAsync(ctx.Response.Body, value, Json.options)
        }

    let writeError (ctx: HttpContext) status code message =
        writeJson ctx status
            {| error =
                {| message = message
                   ``type`` = code
                   code = code |} |}

    let chatCompletion requestId model content =
        {| id = "chatcmpl-" + requestId
           ``object`` = "chat.completion"
           created = Helpers.unixNow()
           model = model
           choices =
            [| {| index = 0
                  message = {| role = "assistant"; content = content |}
                  finish_reason = "stop" |} |]
           usage =
            {| prompt_tokens = 0
               completion_tokens = 0
               total_tokens = 0 |} |}

    let chatChunk requestId model role content finishReason =
        let delta =
            match role, content with
            | Some r, Some c -> box {| role = r; content = c |}
            | Some r, None -> box {| role = r |}
            | None, Some c -> box {| content = c |}
            | None, None -> box {| |}

        {| id = "chatcmpl-" + requestId
           ``object`` = "chat.completion.chunk"
           created = Helpers.unixNow()
           model = model
           choices =
            [| {| index = 0
                  delta = delta
                  finish_reason = finishReason |} |] |}

module Daemon =
    let pending = ConcurrentDictionary<string, PendingRequest>()
    let pendingQueue = ConcurrentQueue<string>()

    let mutable latestBufferText = ""
    let mutable latestBufferAt = DateTimeOffset.MinValue
    let latestBufferLock = obj()

    let readJsonBody (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body, Encoding.UTF8)
            let! bodyText = reader.ReadToEndAsync()
            if String.IsNullOrWhiteSpace(bodyText) then
                return Error "Request body is empty."
            else
                try
                    use doc = JsonDocument.Parse(bodyText)
                    return Ok(bodyText, doc.RootElement.Clone())
                with ex ->
                    return Error $"Invalid JSON: {ex.Message}"
        }

    let logRequest requestId (ctx: HttpContext) bodyText (bodyJson: JsonElement) =
        let record =
            {| timestamp = Helpers.utcNow()
               request_id = requestId
               method = ctx.Request.Method
               path = string ctx.Request.Path
               query = string ctx.Request.QueryString
               body = bodyJson |}

        Files.appendLine Files.requestLogPath (Json.serializeCompact record)
        Files.writeText Files.latestRequestPath (Json.serialize record)

    let logBuffer bufferId matchedRequestId (bodyJson: JsonElement) =
        let record =
            {| timestamp = Helpers.utcNow()
               buffer_id = bufferId
               matched_request_id = matchedRequestId
               body = bodyJson |}
        let text = Json.serialize record
        Files.writeText Files.latestBufferPath text
        lock latestBufferLock (fun () ->
            latestBufferText <- text
            latestBufferAt <- Helpers.utcNow()
        )

    let writeState() =
        let active =
            pending.Values
            |> Seq.map (fun p ->
                {| request_id = p.Id
                   created = p.Created
                   path = p.Path
                   model = p.Model |})
            |> Seq.toArray

        Files.writeText Files.statePath
            (Json.serialize
                {| timestamp = Helpers.utcNow()
                   pending_count = active.Length
                   pending = active
                   latest_buffer_at = if latestBufferAt = DateTimeOffset.MinValue then null else latestBufferAt.ToString("O") |})

    let registerPending requestId path model body =
        let completion =
            TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously)

        let request =
            { Id = requestId
              Created = Helpers.utcNow()
              Path = path
              Model = model
              Body = body
              Completion = completion }

        pending[requestId] <- request
        pendingQueue.Enqueue(requestId)
        writeState()
        request

    let private completeById requestId responseBody =
        let mutable pendingRequest = Unchecked.defaultof<PendingRequest>
        if pending.TryRemove(requestId, &pendingRequest) then
            pendingRequest.Completion.TrySetResult(responseBody) |> ignore
            writeState()
            Some requestId
        else
            None

    let rec private completeNext responseBody =
        let mutable requestId = ""
        if pendingQueue.TryDequeue(&requestId) then
            match completeById requestId responseBody with
            | Some id -> Some id
            | None -> completeNext responseBody
        else
            None

    let completePending (body: JsonElement) =
        let requested =
            Helpers.tryGetString "request_id" body
            |> Option.orElse (Helpers.tryGetString "target_request_id" body)

        match requested with
        | Some requestId ->
            match completeById requestId body with
            | Some id -> Some id
            | None -> completeNext body
        | None -> completeNext body

    let normalizeResponse requestId model (buffer: JsonElement) =
        if Helpers.isOpenAiResponse buffer then
            Choice1Of2 buffer
        else
            let content = Helpers.contentFromBuffer buffer
            Choice2Of2 (Responses.chatCompletion requestId model content)

    let handleNonStreamingChat (ctx: HttpContext) timeoutSeconds (request: PendingRequest) =
        task {
            use timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds))
            use linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ctx.RequestAborted)

            let delayTask = Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token)
            let! completed = Task.WhenAny(request.Completion.Task, delayTask)

            if Object.ReferenceEquals(completed, request.Completion.Task) then
                let responseBody = request.Completion.Task.Result
                match normalizeResponse request.Id request.Model responseBody with
                | Choice1Of2 json -> do! Responses.writeJsonElement ctx StatusCodes.Status200OK json
                | Choice2Of2 response -> do! Responses.writeJson ctx StatusCodes.Status200OK response
            elif timeoutCts.IsCancellationRequested then
                pending.TryRemove(request.Id) |> ignore
                writeState()
                do! Responses.writeError ctx StatusCodes.Status504GatewayTimeout "manual_buffer_timeout" $"No /v1/buffer response arrived for request {request.Id} before timeout."
            else
                pending.TryRemove(request.Id) |> ignore
                writeState()
        }

    let writeSse (ctx: HttpContext) line =
        task {
            do! ctx.Response.WriteAsync(line)
            do! ctx.Response.Body.FlushAsync()
        }

    let handleStreamingChat (ctx: HttpContext) heartbeatSeconds (request: PendingRequest) =
        task {
            Responses.writeJson |> ignore
            ctx.Response.Headers["Access-Control-Allow-Origin"] <- "*"
            ctx.Response.Headers["Cache-Control"] <- "no-cache"
            ctx.Response.ContentType <- "text/event-stream; charset=utf-8"
            ctx.Response.StatusCode <- StatusCodes.Status200OK

            let first = Responses.chatChunk request.Id request.Model (Some "assistant") None null
            do! writeSse ctx ("data: " + Json.serializeCompact first + "\n\n")

            let mutable finished = false
            while not finished && not ctx.RequestAborted.IsCancellationRequested do
                let delay = Task.Delay(TimeSpan.FromSeconds(float heartbeatSeconds), ctx.RequestAborted)
                let! completed = Task.WhenAny(request.Completion.Task, delay)
                if Object.ReferenceEquals(completed, request.Completion.Task) then
                    let responseBody = request.Completion.Task.Result
                    let content = Helpers.contentFromBuffer responseBody
                    let contentChunk = Responses.chatChunk request.Id request.Model None (Some content) null
                    let stopChunk = Responses.chatChunk request.Id request.Model None None "stop"
                    do! writeSse ctx ("data: " + Json.serializeCompact contentChunk + "\n\n")
                    do! writeSse ctx ("data: " + Json.serializeCompact stopChunk + "\n\n")
                    do! writeSse ctx "data: [DONE]\n\n"
                    finished <- true
                else
                    do! writeSse ctx ($": thinking {Helpers.utcNow():O}\n\n")
        }

    let handleChatCompletion defaultModel timeoutSeconds heartbeatSeconds (ctx: HttpContext) =
        task {
            match! readJsonBody ctx with
            | Error error ->
                do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_json" error
            | Ok(bodyText, bodyJson) ->
                let requestId = Helpers.newId "req"
                let model = Helpers.modelFromBody defaultModel bodyJson
                let stream = Helpers.tryGetBool "stream" bodyJson |> Option.defaultValue false
                logRequest requestId ctx bodyText bodyJson

                let pendingRequest = registerPending requestId (string ctx.Request.Path) model bodyJson
                if stream then
                    do! handleStreamingChat ctx heartbeatSeconds pendingRequest
                else
                    do! handleNonStreamingChat ctx timeoutSeconds pendingRequest
        } :> Task

    let handleBuffer (ctx: HttpContext) =
        task {
            match! readJsonBody ctx with
            | Error error ->
                do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_json" error
            | Ok(_, bodyJson) ->
                let bufferId = Helpers.newId "buf"
                let matched = completePending bodyJson
                logBuffer bufferId matched bodyJson
                do! (Responses.writeJson ctx StatusCodes.Status200OK
                    {| ok = true
                       buffer_id = bufferId
                       matched_request_id = matched
                       pending_count = pending.Count
                       latest_buffer_file = Files.latestBufferPath |})
        } :> Task

    let handleLoggedOnly endpointName statusCode (ctx: HttpContext) =
        task {
            match! readJsonBody ctx with
            | Ok(bodyText, bodyJson) ->
                let requestId = Helpers.newId "req"
                logRequest requestId ctx bodyText bodyJson
                do! (Responses.writeJson ctx statusCode
                    {| ok = false
                       request_id = requestId
                       endpoint = endpointName
                       message = "Request was logged, but this endpoint is a compatibility stub in the manual buffer daemon."
                       latest_request_file = Files.latestRequestPath |})
            | Error error ->
                do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_json" error
        } :> Task

module Vision =
    let private tryElementCount (body: JsonElement) =
        match Helpers.tryGetProperty "elements" body with
        | Some elements when elements.ValueKind = JsonValueKind.Array -> Some(elements.GetArrayLength())
        | _ -> None

    let private observationText source elementCount (body: JsonElement) =
        let screenTag = Helpers.tryGetString "screen_tag" body
        let header = $"source: {source}{Environment.NewLine}elements: {elementCount}{Environment.NewLine}"
        match screenTag with
        | Some text when not (String.IsNullOrWhiteSpace(text)) -> header + Environment.NewLine + text
        | _ -> header + Environment.NewLine + Json.serialize body

    let handleObservation (ctx: HttpContext) =
        task {
            match! Daemon.readJsonBody ctx with
            | Error error ->
                do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_json" error
            | Ok(_, bodyJson) when bodyJson.ValueKind <> JsonValueKind.Object ->
                do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_vision_observation" "Vision observation must be a JSON object."
            | Ok(_, bodyJson) ->
                match Helpers.tryGetString "source" bodyJson, tryElementCount bodyJson with
                | None, _ ->
                    do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_vision_observation" "Vision observation requires a non-empty source string."
                | Some source, _ when String.IsNullOrWhiteSpace(source) ->
                    do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_vision_observation" "Vision observation requires a non-empty source string."
                | _, None ->
                    do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_vision_observation" "Vision observation requires an elements array."
                | Some source, Some elementCount ->
                    let observationId = Helpers.newId "obs"
                    let record =
                        {| observation_id = observationId
                           received_at = Helpers.utcNow()
                           source = source
                           element_count = elementCount
                           body = bodyJson |}

                    Files.appendLine Files.observationLogPath (Json.serializeCompact record)
                    Files.writeText Files.latestObservationPath (Json.serialize record)
                    Files.writeText Files.latestObservationTextPath (observationText source elementCount bodyJson)

                    do! (Responses.writeJson ctx StatusCodes.Status202Accepted
                        {| ok = true
                           observation_id = observationId
                           source = source
                           element_count = elementCount
                           latest_observation_file = Files.latestObservationPath
                           latest_observation_text_file = Files.latestObservationTextPath |})
        } :> Task

module Program =
    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)

        builder.Configuration
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional = true, reloadOnChange = true)
            .AddEnvironmentVariables("OPENAI_BUFFER_")
            |> ignore

        let url =
            builder.Configuration.["Daemon:Url"]
            |> Option.ofObj
            |> Option.defaultValue "http://127.0.0.1:5087"

        let modelId =
            builder.Configuration.["Daemon:ModelId"]
            |> Option.ofObj
            |> Option.defaultValue "manual-buffer-model"

        let timeoutSeconds =
            builder.Configuration.["Daemon:RequestTimeoutSeconds"]
            |> Option.ofObj
            |> Option.map int
            |> Option.defaultValue 900

        let heartbeatSeconds =
            builder.Configuration.["Daemon:HeartbeatSeconds"]
            |> Option.ofObj
            |> Option.map int
            |> Option.defaultValue 2

        let dataDirectory =
            builder.Configuration.["Daemon:DataDirectory"]
            |> Option.ofObj
            |> Option.defaultValue Files.dataDirectory

        Files.configure dataDirectory

        builder.WebHost.UseUrls(url) |> ignore
        builder.Logging.ClearProviders() |> ignore
        builder.Logging.AddConsole() |> ignore
        builder.Logging.SetMinimumLevel(LogLevel.Information) |> ignore

        let app = builder.Build()

        app.MapMethods("/{**path}", [| "OPTIONS" |], RequestDelegate(fun ctx ->
            task {
                ctx.Response.Headers["Access-Control-Allow-Origin"] <- "*"
                ctx.Response.Headers["Access-Control-Allow-Headers"] <- "authorization,content-type"
                ctx.Response.Headers["Access-Control-Allow-Methods"] <- "GET,POST,OPTIONS"
                ctx.Response.Headers["Access-Control-Allow-Private-Network"] <- "true"
                ctx.Response.StatusCode <- StatusCodes.Status204NoContent
            } :> Task
        )) |> ignore

        app.MapGet("/health", Func<HttpContext, Task>(fun ctx ->
            Responses.writeJson ctx StatusCodes.Status200OK
                {| ok = true
                   service = "openai-buffer-daemon"
                   time = Helpers.utcNow()
                   pending_count = Daemon.pending.Count |}
            :> Task
        )) |> ignore

        app.MapGet("/v1/models", Func<HttpContext, Task>(fun ctx ->
            Responses.writeJson ctx StatusCodes.Status200OK
                {| ``object`` = "list"
                   data =
                    [| {| id = modelId
                          ``object`` = "model"
                          created = Helpers.unixNow()
                          owned_by = "local-manual-buffer" |} |] |}
            :> Task
        )) |> ignore

        app.MapGet("/v1/models/{model}", Func<HttpContext, string, Task>(fun ctx model ->
            Responses.writeJson ctx StatusCodes.Status200OK
                {| id = model
                   ``object`` = "model"
                   created = Helpers.unixNow()
                   owned_by = "local-manual-buffer" |}
            :> Task
        )) |> ignore

        app.MapGet("/v1/buffer", Func<HttpContext, Task>(fun ctx ->
            Responses.writeJson ctx StatusCodes.Status200OK
                {| ok = true
                   pending_count = Daemon.pending.Count
                   latest_buffer_file = Files.latestBufferPath
                   latest_request_file = Files.latestRequestPath
                   request_log_file = Files.requestLogPath |}
            :> Task
        )) |> ignore

        app.MapGet("/v1/pending", Func<HttpContext, Task>(fun ctx ->
            let active =
                Daemon.pending.Values
                |> Seq.map (fun p ->
                    {| request_id = p.Id
                       created = p.Created
                       path = p.Path
                       model = p.Model |})
                |> Seq.toArray

            Responses.writeJson ctx StatusCodes.Status200OK
                {| ok = true
                   pending_count = active.Length
                   pending = active |}
            :> Task
        )) |> ignore

        app.MapGet("/v1/requests/latest", Func<HttpContext, Task>(fun ctx ->
            task {
                if File.Exists(Files.latestRequestPath) then
                    ctx.Response.Headers["Access-Control-Allow-Origin"] <- "*"
                    ctx.Response.ContentType <- "application/json; charset=utf-8"
                    let text = File.ReadAllText(Files.latestRequestPath, Encoding.UTF8)
                    do! ctx.Response.WriteAsync(text)
                else
                    do! Responses.writeError ctx StatusCodes.Status404NotFound "not_found" "No latest request file exists yet."
            } :> Task
        )) |> ignore

        app.MapGet("/v1/buffer/latest", Func<HttpContext, Task>(fun ctx ->
            task {
                if File.Exists(Files.latestBufferPath) then
                    ctx.Response.Headers["Access-Control-Allow-Origin"] <- "*"
                    ctx.Response.ContentType <- "application/json; charset=utf-8"
                    let text = File.ReadAllText(Files.latestBufferPath, Encoding.UTF8)
                    do! ctx.Response.WriteAsync(text)
                else
                    do! Responses.writeError ctx StatusCodes.Status404NotFound "not_found" "No latest buffer file exists yet."
            } :> Task
        )) |> ignore

        app.MapGet("/v1/vision/observations", Func<HttpContext, Task>(fun ctx ->
            Responses.writeJson ctx StatusCodes.Status200OK
                {| ok = true
                   latest_observation_file = Files.latestObservationPath
                   latest_observation_text_file = Files.latestObservationTextPath
                   observation_log_file = Files.observationLogPath
                   has_observation = File.Exists(Files.latestObservationPath) |}
            :> Task
        )) |> ignore

        app.MapGet("/v1/vision/observations/latest", Func<HttpContext, Task>(fun ctx ->
            task {
                if File.Exists(Files.latestObservationPath) then
                    ctx.Response.Headers["Access-Control-Allow-Origin"] <- "*"
                    ctx.Response.ContentType <- "application/json; charset=utf-8"
                    let text = File.ReadAllText(Files.latestObservationPath, Encoding.UTF8)
                    do! ctx.Response.WriteAsync(text)
                else
                    do! Responses.writeError ctx StatusCodes.Status404NotFound "not_found" "No vision observation exists yet."
            } :> Task
        )) |> ignore

        app.MapPost("/v1/buffer", RequestDelegate(Daemon.handleBuffer)) |> ignore

        app.MapPost("/v1/vision/observations", RequestDelegate(Vision.handleObservation)) |> ignore

        app.MapPost("/v1/chat/completions", RequestDelegate(Daemon.handleChatCompletion modelId timeoutSeconds heartbeatSeconds)) |> ignore

        app.MapPost("/v1/completions", RequestDelegate(Daemon.handleLoggedOnly "/v1/completions" StatusCodes.Status501NotImplemented)) |> ignore
        app.MapPost("/v1/responses", RequestDelegate(Daemon.handleLoggedOnly "/v1/responses" StatusCodes.Status501NotImplemented)) |> ignore
        app.MapPost("/v1/embeddings", RequestDelegate(Daemon.handleLoggedOnly "/v1/embeddings" StatusCodes.Status501NotImplemented)) |> ignore
        app.MapPost("/v1/audio/transcriptions", RequestDelegate(Daemon.handleLoggedOnly "/v1/audio/transcriptions" StatusCodes.Status501NotImplemented)) |> ignore
        app.MapPost("/v1/audio/translations", RequestDelegate(Daemon.handleLoggedOnly "/v1/audio/translations" StatusCodes.Status501NotImplemented)) |> ignore
        app.MapPost("/tokenize", RequestDelegate(Daemon.handleLoggedOnly "/tokenize" StatusCodes.Status501NotImplemented)) |> ignore
        app.MapPost("/detokenize", RequestDelegate(Daemon.handleLoggedOnly "/detokenize" StatusCodes.Status501NotImplemented)) |> ignore
        app.MapPost("/rerank", RequestDelegate(Daemon.handleLoggedOnly "/rerank" StatusCodes.Status501NotImplemented)) |> ignore

        app.MapMethods("/v1/{**tail}", [| "GET"; "POST"; "PUT"; "PATCH"; "DELETE" |], RequestDelegate(fun ctx ->
            task {
                if ctx.Request.Method = "POST" || ctx.Request.Method = "PUT" || ctx.Request.Method = "PATCH" then
                    do! Daemon.handleLoggedOnly (string ctx.Request.Path) StatusCodes.Status501NotImplemented ctx
                else
                    do! Responses.writeError ctx StatusCodes.Status404NotFound "not_found" $"Endpoint {ctx.Request.Path} is not implemented by the manual buffer daemon."
            } :> Task
        )) |> ignore

        Files.writeText Files.statePath
            (Json.serialize
                {| service = "openai-buffer-daemon"
                   started = Helpers.utcNow()
                   url = url
                   pending_count = 0 |})

        Console.WriteLine()
        Console.WriteLine("OpenAI Buffer Daemon")
        Console.WriteLine($"  Listening: {url}")
        Console.WriteLine($"  Chat endpoint: {url}/v1/chat/completions")
        Console.WriteLine($"  Buffer endpoint: {url}/v1/buffer")
        Console.WriteLine($"  Latest request: {Files.latestRequestPath}")
        Console.WriteLine($"  Request log: {Files.requestLogPath}")
        Console.WriteLine($"  Latest buffer: {Files.latestBufferPath}")
        Console.WriteLine($"  Vision observations: {url}/v1/vision/observations")
        Console.WriteLine($"  Data directory: {Files.dataDirectory}")
        Console.WriteLine()

        app.Run()
        0
