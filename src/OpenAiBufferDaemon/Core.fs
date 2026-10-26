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
        let desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        if String.IsNullOrWhiteSpace(desktop) then
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "firebending")
        else desktop

    let mutable dataDirectory = defaultDirectory
    let mutable requestLogPath = Path.Combine(dataDirectory, "openai-buffer-daemon-requests.jsonl")
    let mutable latestRequestPath = Path.Combine(dataDirectory, "openai-buffer-daemon-latest-request.json")
    let mutable latestBufferPath = Path.Combine(dataDirectory, "openai-buffer-daemon-latest-buffer.json")
    let mutable statePath = Path.Combine(dataDirectory, "openai-buffer-daemon-state.json")
    let mutable observationLogPath = Path.Combine(dataDirectory, "openai-buffer-daemon-vision-observations.jsonl")
    let mutable latestObservationPath = Path.Combine(dataDirectory, "openai-buffer-daemon-latest-vision-observation.json")
    let mutable latestObservationTextPath = Path.Combine(dataDirectory, "openai-buffer-daemon-latest-vision-observation.txt")
    let mutable macroPointerPath = Path.Combine(dataDirectory, "openai-buffer-daemon-macro.txt")

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
        macroPointerPath <- Path.Combine(dataDirectory, "openai-buffer-daemon-macro.txt")
        Directory.CreateDirectory(dataDirectory) |> ignore

    let ensureDataDirectory() =
        Directory.CreateDirectory(dataDirectory) |> ignore

    let rgdCommandPath slot =
        Path.Combine(dataDirectory, $"openai-buffer-daemon-rgd{slot}.txt")

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
