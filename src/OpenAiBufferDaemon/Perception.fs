namespace OpenAiBufferDaemon

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

module Perception =
    let mutable private ocrEngine = 0
    let mutable private cerealLevel = 1

    let private runImageHelper options (arguments: string list) (bytes: byte[]) (ct: CancellationToken) = task {
        let info = ProcessStartInfo()
        info.FileName <- options.OcrPython
        info.UseShellExecute <- false
        info.CreateNoWindow <- true
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.StandardOutputEncoding <- UTF8Encoding(false)
        info.StandardErrorEncoding <- UTF8Encoding(false)
        for arg in [ "-m"; "openai_buffer_vision.oneshot" ] @ arguments do info.ArgumentList.Add(arg)
        info.Environment["PYTHONUTF8"] <- "1"
        // No socket/warm daemon is started, including when Paddle is selected.
        info.Environment["OMARCHY_PPOCR_DAEMON"] <- "0"
        use child = new Process(StartInfo = info)
        if not (child.Start()) then Inference.fail 503 "Could not start the one-shot image helper."
        use cancel = ct.Register(fun () ->
            try if not child.HasExited then child.Kill(true)
            with :? InvalidOperationException -> ())
        let readBounded limit (reader: StreamReader) = task {
            let output = StringBuilder()
            let buffer = Array.zeroCreate<char> 4096
            let mutable reading = true
            while reading do
                let! count = reader.ReadAsync(buffer.AsMemory(), ct)
                if count = 0 then reading <- false
                else
                    if output.Length + count > limit then
                        try if not child.HasExited then child.Kill(true)
                        with :? InvalidOperationException -> ()
                        Inference.fail 502 "Image helper output exceeded its bounded pipe limit."
                    output.Append(buffer, 0, count) |> ignore
            return output.ToString()
        }
        let outputTask = readBounded (8 * 1024 * 1024) child.StandardOutput
        let errorTask = readBounded (256 * 1024) child.StandardError
        let! outcome = task {
            try
                try do! child.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(), ct)
                with :? IOException when not ct.IsCancellationRequested -> () // Read early-exit diagnostics below.
                try child.StandardInput.Close()
                with :? IOException when not ct.IsCancellationRequested -> ()
                do! child.WaitForExitAsync(ct)
                let! output = outputTask
                let! error = errorTask
                if child.ExitCode <> 0 then
                    let diagnostic = if error.Length > 2048 then error.Substring(0, 2048) else error
                    Inference.fail 503 ("One-shot image helper failed. Install the selected optional_vision extra (cereal/ocr/paddle) and configure OcrPython. " + diagnostic.Trim())
                try
                    use doc = JsonDocument.Parse(output)
                    match Helpers.tryGetProperty "text" doc.RootElement with
                    | Some text when text.ValueKind = JsonValueKind.String -> return Ok(doc.RootElement.Clone())
                    | _ -> return Inference.fail 502 "Image helper returned no text field."
                with :? JsonException -> return Inference.fail 502 "Image helper returned invalid JSON."
            with ex -> return Error ex
        }
        // Reap even after cancellation; cleanup has its own short deadline.
        try if not child.HasExited then child.Kill(true)
        with :? InvalidOperationException -> ()
        use cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
        try do! child.WaitForExitAsync(cleanup.Token)
        with :? OperationCanceledException -> Inference.fail 503 "Image helper did not exit after cancellation."
        // Observe both pipe tasks, including failures racing process cancellation.
        try let! _ = Task.WhenAll(outputTask, errorTask) in ()
        with _ -> ()
        match outcome with
        | Ok text -> return text
        | Error error -> return raise error
    }

    let private recognize options engine bytes ct = task {
        let! result = runImageHelper options [ "--engine"; engine ] bytes ct
        return Inference.stringValue (result.GetProperty("text"))
    }

    let private resolution level = match level with 1 -> 32 | 2 -> 128 | _ -> 256

    let cereal options stopping ctx =
        Inference.runUnlocked options stopping (fun ct context -> (task {
            if context.Request.Method = "GET" then
                let level = Volatile.Read(&cerealLevel)
                context.Response.ContentType <- "text/plain; charset=utf-8"
                context.Response.Headers.CacheControl <- "no-store"
                do! context.Response.WriteAsync($"{level}: {resolution level}p (local raster-to-text; no inference)\n", ct)
            else
                let selected = Volatile.Read(&cerealLevel)
                let level =
                    match string context.Request.Query["level"] with
                    | "" -> selected
                    | "1" -> 1 | "2" -> 2 | "3" -> 3
                    | _ -> Inference.fail 400 "Cereal level must be 1, 2 or 3."
                let style = match string context.Request.Query["style"] with "" -> "ascii" | value -> value
                let format = match string context.Request.Query["format"] with "" -> "raw" | value -> value
                if not (List.contains style [ "ascii"; "braille"; "block" ]) then Inference.fail 400 "Cereal style must be ascii, braille or block."
                if not (List.contains format [ "raw"; "markdown"; "json" ]) then Inference.fail 400 "Cereal format must be raw, markdown or json."
                let render() = task {
                    let! image = Inference.image ct context
                    // A cold CPU renderer only. No model discovery, tool calls,
                    // OCR adapters, URL forwarding or stored image/result state.
                    let! result = runImageHelper options [ "--operation"; "cereal"; "--level"; string level; "--style"; style ] image ct
                    let text = Inference.stringValue (result.GetProperty("text"))
                    context.Response.Headers.CacheControl <- "no-store"
                    context.Response.Headers["X-Cereal-Level"] <- string level
                    if format = "json" then
                        context.Response.ContentType <- "application/json; charset=utf-8"
                        do! context.Response.WriteAsync(Json.serialize result, ct)
                    elif format = "markdown" then
                        do! Formatting.write context 200 (Inference.codeBlock "text" text)
                    else
                        context.Response.ContentType <- "text/plain; charset=utf-8"
                        do! context.Response.WriteAsync(text, ct)
                }
                let contentType = string context.Request.ContentType
                if contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) then
                    do! render()
                else
                    let! bytes = Inference.readBytes (32 * 1024 * 1024) ct context
                    let raw = Encoding.UTF8.GetString(bytes).Trim()
                    let setting =
                        match raw with
                        | "1" -> Some 1 | "2" -> Some 2 | "3" -> Some 3
                        | "\"1\"" when contentType.Contains("json", StringComparison.OrdinalIgnoreCase) -> Some 1
                        | "\"2\"" when contentType.Contains("json", StringComparison.OrdinalIgnoreCase) -> Some 2
                        | "\"3\"" when contentType.Contains("json", StringComparison.OrdinalIgnoreCase) -> Some 3
                        | _ -> None
                    match setting with
                    | Some value ->
                        Interlocked.Exchange(&cerealLevel, value) |> ignore
                        context.Response.ContentType <- "text/plain; charset=utf-8"
                        context.Response.Headers.CacheControl <- "no-store"
                        do! context.Response.WriteAsync($"{value}: {resolution value}p\n", ct)
                    | None ->
                        let original = context.Request.Body
                        use input = new MemoryStream(bytes)
                        context.Request.Body <- input
                        try do! render()
                        finally context.Request.Body <- original
        } :> Task)) ctx

    let ocr options stopping ctx =
        Inference.runUnlocked options stopping (fun ct context -> (task {
            if context.Request.Method = "GET" then
                do! Formatting.write context 200 (if Volatile.Read(&ocrEngine) = 0 then "0: tesseract (cold per image)\n" else "1: paddle (cold per image)\n")
            else
                // Do not touch /v1/file: bytes/path and switch belong to this request only.
                let contentType = string context.Request.ContentType
                if not (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase)) then
                    // Buffer the one-use input so switches do not open files/start helpers.
                    let! bytes = Inference.readBytes (32 * 1024 * 1024) ct context
                    let raw = Encoding.UTF8.GetString(bytes).Trim()
                    let setting =
                        if raw = "0" || raw = "1" then Some(int raw)
                        elif contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && (raw = "\"0\"" || raw = "\"1\"") then Some(int(raw.Trim('"')))
                        else None
                    match setting with
                    | Some engine ->
                        Interlocked.Exchange(&ocrEngine, engine) |> ignore
                        do! Formatting.write context 200 (if engine = 0 then "Tesseract selected; no model loaded.\n" else "Paddle selected; no model loaded.\n")
                    | None ->
                        use buffered = new IO.MemoryStream(bytes)
                        context.Request.Body <- buffered
                        let engine = if Volatile.Read(&ocrEngine) = 0 then "tesseract" else "paddle"
                        do! Inference.inLane (fun () -> task {
                            let! image = Inference.image ct context
                            let! text = recognize options engine image ct
                            do! Formatting.write context 200 text
                        })
                else
                    let engine = if Volatile.Read(&ocrEngine) = 0 then "tesseract" else "paddle"
                    do! Inference.inLane (fun () -> task {
                        let! image = Inference.image ct context
                        let! text = recognize options engine image ct
                        do! Formatting.write context 200 text
                    })
        } :> Task)) ctx
