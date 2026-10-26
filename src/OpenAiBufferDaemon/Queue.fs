namespace OpenAiBufferDaemon

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
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

module Daemon =
    let pending = ConcurrentDictionary<string, PendingRequest>()
    // Remove targeted/completed/cancelled entries immediately: the old FIFO
    // retained a tombstone for every explicitly addressed request forever.
    let private pendingOrder = LinkedList<string>()
    let private pendingNodes = Dictionary<string, LinkedListNode<string>>()
    let private queueLock = obj()

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

        lock queueLock (fun () ->
            pending[requestId] <- request
            pendingNodes[requestId] <- pendingOrder.AddLast(requestId)
            writeState())
        request

    let removePending requestId =
        lock queueLock (fun () ->
            let mutable request = Unchecked.defaultof<PendingRequest>
            if pending.TryRemove(requestId, &request) then
                match pendingNodes.TryGetValue(requestId) with
                | true, node ->
                    pendingOrder.Remove(node)
                    pendingNodes.Remove(requestId) |> ignore
                | _ -> ()
                writeState()
                Some request
            else None)

    let completeById requestId responseBody =
        match removePending requestId with
        | Some request ->
            request.Completion.TrySetResult(responseBody) |> ignore
            Some requestId
        | None -> None

    let private completeNext responseBody =
        lock queueLock (fun () ->
            match pendingOrder.First with
            | null -> None
            | first -> completeById first.Value responseBody)

    let completePending (body: JsonElement) =
        let requested =
            Helpers.tryGetString "request_id" body
            |> Option.orElse (Helpers.tryGetString "target_request_id" body)

        match requested with
        | Some requestId -> completeById requestId body
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
                removePending request.Id |> ignore
                do! Responses.writeError ctx StatusCodes.Status504GatewayTimeout "manual_buffer_timeout" $"No /v1/buffer response arrived for request {request.Id} before timeout."
            else
                removePending request.Id |> ignore
        }

    let writeSse (ctx: HttpContext) line =
        task {
            do! ctx.Response.WriteAsync(line, ctx.RequestAborted)
            do! ctx.Response.Body.FlushAsync(ctx.RequestAborted)
        }

    let handleStreamingChat (ctx: HttpContext) heartbeatSeconds (request: PendingRequest) =
        task {
            try
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
                    elif not ctx.RequestAborted.IsCancellationRequested then
                        do! writeSse ctx ($": thinking {Helpers.utcNow():O}\n\n")
            finally
                removePending request.Id |> ignore
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
