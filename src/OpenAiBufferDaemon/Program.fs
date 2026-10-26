namespace OpenAiBufferDaemon

open System
open System.Collections.Concurrent
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

        // Independent of the legacy chat timeout; a finite safety deadline
        // returns a clear failure rather than pretending the model replied.
        let messageTimeoutSeconds =
            builder.Configuration.["Daemon:MessageTimeoutSeconds"]
            |> Option.ofObj |> Option.map int |> Option.defaultValue 900
        if messageTimeoutSeconds < 1 || messageTimeoutSeconds > 86400 then
            invalidArg "MessageTimeoutSeconds" "Must be between 1 and 86400 seconds."

        let dataDirectory =
            builder.Configuration.["Daemon:DataDirectory"]
            |> Option.ofObj
            |> Option.defaultValue Files.dataDirectory

        let tasketRunUrl =
            builder.Configuration.["Daemon:TasketRunUrl"]
            |> Option.ofObj
            |> Option.defaultValue "http://127.0.0.1:7777/run"

        let setting key fallback = builder.Configuration.["Daemon:" + key] |> Option.ofObj |> Option.defaultValue fallback
        let ocrPython =
            let roots = [ Directory.GetCurrentDirectory(); AppContext.BaseDirectory ]
            let candidates = seq {
                for root in roots do
                    let mutable directory = Some(DirectoryInfo(root))
                    for _ in 0..6 do
                        match directory with
                        | Some current ->
                            yield Path.Combine(current.FullName, "optional_vision", ".venv", (if OperatingSystem.IsWindows() then "Scripts/python.exe" else "bin/python"))
                            directory <- Option.ofObj current.Parent
                        | None -> () }
            candidates |> Seq.tryFind File.Exists |> Option.defaultValue (if OperatingSystem.IsWindows() then "python" else "python3")
        let inference = {
            BaseUrl = setting "NinferBaseUrl" "http://127.0.0.1:8000"
            DaemonUrl = url
            Model = setting "NinferModel" ""
            ApiKey = setting "NinferApiKey" ""
            TimeoutSeconds = int (setting "PerceptionTimeoutSeconds" "120")
            MaxTokens = int (setting "NinferMaxTokens" "8192")
            OcrPython = setting "OcrPython" ocrPython }
        if inference.TimeoutSeconds < 1 || inference.TimeoutSeconds > 900 || inference.MaxTokens < 1 then
            invalidArg "perception" "PerceptionTimeoutSeconds must be 1-900 and NinferMaxTokens must be positive."
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

        app.MapGet("/v1/vision/schema", RequestDelegate(Vision.handleSchema)) |> ignore

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

        app.MapPost("/v1/format", RequestDelegate(Messages.guard Formatting.handle)) |> ignore
        app.MapMethods("/v1/jevset", [| "GET"; "POST" |], RequestDelegate(Messages.guard (Jev.set inference app.Lifetime.ApplicationStopping))) |> ignore
        app.MapPost("/v1/jevask", RequestDelegate(Messages.guard (Jev.ask inference app.Lifetime.ApplicationStopping))) |> ignore
        app.MapMethods("/v1/ocr", [| "GET"; "POST" |], RequestDelegate(Messages.guard (Perception.ocr inference app.Lifetime.ApplicationStopping))) |> ignore
        app.MapMethods("/v1/cereal", [| "GET"; "POST" |], RequestDelegate(Messages.guard (Perception.cereal inference app.Lifetime.ApplicationStopping))) |> ignore
        app.MapMethods("/v1/file", [| "GET"; "POST"; "DELETE" |], RequestDelegate(Messages.guard Messages.files)) |> ignore
        app.MapMethods("/v1/multichat", [| "GET"; "POST" |], RequestDelegate(Messages.guard Messages.multichat)) |> ignore
        app.MapPost("/v1/delimiter", RequestDelegate(Messages.guard Messages.delimiter)) |> ignore
        app.MapMethods("/v1/check", [| "GET"; "POST" |], RequestDelegate(Messages.guard Messages.check)) |> ignore
        app.MapPost("/v1/message", RequestDelegate(Messages.guard (Messages.message modelId messageTimeoutSeconds app.Lifetime.ApplicationStopping))) |> ignore
        app.MapGet("/v1/requests/{requestId}", Func<HttpContext, string, Task>(fun ctx requestId ->
            Messages.guard (fun c -> Messages.requestById c requestId) ctx)) |> ignore
        app.MapPost("/v1/requests/{requestId}", Func<HttpContext, string, Task>(fun ctx requestId ->
            Messages.guard (fun c -> Messages.replyById c requestId) ctx)) |> ignore

        app.MapPost("/v1/macro", RequestDelegate(Zuko.handleMacro)) |> ignore

        app.MapPost("/v1/rgd1", RequestDelegate(Zuko.handleRgd 1)) |> ignore
        app.MapPost("/v1/rgd2", RequestDelegate(Zuko.handleRgd 2)) |> ignore
        app.MapPost("/v1/rgd3", RequestDelegate(Zuko.handleRgd 3)) |> ignore
        app.MapPost("/v1/rgd4", RequestDelegate(Zuko.handleRgd 4)) |> ignore
        app.MapPost("/v1/rgd5", RequestDelegate(Zuko.handleRgd 5)) |> ignore

        app.MapPost("/v1/zuko", RequestDelegate(Zuko.handleZuko tasketRunUrl)) |> ignore

        app.MapPost("/v1/iroh", RequestDelegate(Zuko.handleIroh tasketRunUrl)) |> ignore

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
        Console.WriteLine($"  Message endpoint: {url}/v1/message")
        Console.WriteLine($"  Format endpoint: {url}/v1/format")
        Console.WriteLine($"  Macro endpoint: {url}/v1/macro")
        Console.WriteLine($"  Zuko endpoint: {url}/v1/zuko")
        Console.WriteLine($"  Iroh endpoint: {url}/v1/iroh")
        Console.WriteLine($"  Latest request: {Files.latestRequestPath}")
        Console.WriteLine($"  Request log: {Files.requestLogPath}")
        Console.WriteLine($"  Latest buffer: {Files.latestBufferPath}")
        Console.WriteLine($"  Vision observations: {url}/v1/vision/observations")
        Console.WriteLine($"  Data directory: {Files.dataDirectory}")
        Console.WriteLine()

        app.Run()
        0
