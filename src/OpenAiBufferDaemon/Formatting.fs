namespace OpenAiBufferDaemon

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

/// One formatter for the standalone pipe and the waiting message endpoint.
/// It extracts text, never pretty-prints an unknown object as an answer.
module Formatting =
    let private parseJson (raw: string) =
        try
            use doc = JsonDocument.Parse(raw.TrimStart('\uFEFF'))
            doc.RootElement.Clone()
        with :? JsonException ->
            // Some captures contain bare multiline/control characters inside a
            // JSON string. Repair only those illegal characters, not syntax,
            // quotes, paths, code escapes, or the surrounding Markdown.
            let repaired = StringBuilder()
            let mutable quoted, escaped = false, false
            for c in raw do
                if quoted && not escaped && Char.IsControl(c) then
                    repaired.Append($"\\u{int c:x4}") |> ignore
                else
                    repaired.Append(c) |> ignore
                    if escaped then escaped <- false
                    elif quoted && c = '\\' then escaped <- true
                    elif c = '"' then quoted <- not quoted
            try
                use doc = JsonDocument.Parse(repaired.ToString().TrimStart('\uFEFF'))
                doc.RootElement.Clone()
            with :? JsonException -> JsonSerializer.SerializeToElement(raw)

    let parse (raw: string) =
        let trimmed = raw.TrimStart()
        if trimmed.StartsWith("data:") || trimmed.StartsWith(":") then
            let values =
                raw.Replace("\r\n", "\n").Split('\n')
                |> Array.filter (fun line -> line.StartsWith("data:"))
                |> Array.map (fun line -> line.Substring(5).TrimStart())
                |> Array.filter (fun line -> line <> "[DONE]")
                |> Array.map parseJson
            if values.Length > 0 && values |> Array.forall (fun value -> value.ValueKind = JsonValueKind.Object) then
                JsonSerializer.SerializeToElement(values)
            else parseJson raw
        else parseJson raw

    let private property = Helpers.tryGetProperty

    let rec private unwrap depth (value: JsonElement) =
        if depth > 16 then invalidOp "Response nesting exceeds 16 formatting layers."
        if value.ValueKind = JsonValueKind.String then
            let raw = value.GetString() |> Option.ofObj |> Option.defaultValue ""
            let nested = parse raw
            let envelope = nested.ValueKind = JsonValueKind.Object &&
                           [ "choices"; "message"; "response"; "output"; "content"; "text"; "output_text" ] |> List.exists (fun key -> property key nested |> Option.isSome)
            let chunks = nested.ValueKind = JsonValueKind.Array && nested.GetArrayLength() > 0
                         && nested.EnumerateArray() |> Seq.forall (fun item -> property "choices" item |> Option.isSome)
            if (nested.ValueKind = JsonValueKind.String && nested.GetString() <> raw) || envelope || chunks then unwrap (depth + 1) nested
            else value
        else value

    let rec private text depth (value: JsonElement) =
        if depth > 16 then invalidOp "Response nesting exceeds 16 formatting layers."
        match value.ValueKind with
        | JsonValueKind.String -> value.GetString() |> Option.ofObj |> Option.defaultValue ""
        | JsonValueKind.Array ->
            value.EnumerateArray() |> Seq.map (text (depth + 1)) |> String.concat ""
        | JsonValueKind.Object ->
            [ "text"; "thinking"; "value"; "summary"; "content" ]
            |> List.tryPick (fun key -> property key value)
            |> Option.map (text (depth + 1)) |> Option.defaultValue ""
        | _ -> ""

    let rec private extract depth (value: JsonElement) : string * string =
        if depth > 16 then invalidOp "Response nesting exceeds 16 formatting layers."
        let next = extract (depth + 1)
        let combine items =
            let reasoning, answer = StringBuilder(), StringBuilder()
            for item in items do
                let r, a = next item
                reasoning.Append(r) |> ignore
                answer.Append(a) |> ignore
            reasoning.ToString(), answer.ToString()
        match value.ValueKind with
        // Once inside a model content field, JSON can be the requested answer.
        // Only unwrap serialized transport layers at the root, not this text.
        | JsonValueKind.String -> "", text depth value
        | JsonValueKind.Array -> combine (value.EnumerateArray())
        | JsonValueKind.Number | JsonValueKind.True | JsonValueKind.False -> "", value.GetRawText()
        | JsonValueKind.Object ->
            match property "error" value with
            | Some error ->
                let detail = property "message" error |> Option.map (text depth) |> Option.defaultValue (text depth error)
                invalidOp ("Model returned an error" + if detail = "" then "." else ": " + detail)
            | None ->
                let kind = Helpers.tryGetString "type" value |> Option.defaultValue ""
                if List.contains kind [ "reasoning"; "thinking"; "reasoning_text"; "summary_text" ] then
                    text depth value, ""
                else
                    let ownReasoning =
                        [ "reasoning_content"; "reasoning"; "thinking"; "reasoning_details" ]
                        |> List.choose (fun key -> property key value |> Option.map (text depth))
                        |> String.concat ""
                    let r, a =
                        match property "choices" value with
                        | Some choices when choices.ValueKind = JsonValueKind.Array && choices.GetArrayLength() > 0 -> next choices[0]
                        | _ ->
                            match [ "message"; "delta"; "response"; "output"; "content"; "output_text"; "text"; "value" ]
                                  |> List.tryPick (fun key -> property key value) with
                            | Some content -> next content
                            | None -> "", ""
                    ownReasoning + r, a
        | _ -> "", ""

    let private repairEscapes mode (value: string) =
        let normalized = value.Replace("\r\n", "\n").Replace("\r", "\n").TrimStart('\uFEFF')
        let ambiguous = normalized.Contains('`') || Regex.IsMatch(normalized, @"[A-Za-z]:\\|\\\\")
        let repair = mode = "literal" || (mode = "auto" && not ambiguous && not (normalized.Contains('\n')))
        if repair then
            normalized.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n").Replace("\\t", "\t")
        else normalized

    let parts mode value =
        if not (List.contains mode [ "auto"; "off"; "literal" ]) then
            invalidArg "escapes" "escapes must be auto, off, or literal."
        let reasoning, answer = extract 0 (unwrap 0 value)
        let mutable reasoning = repairEscapes mode reasoning
        let mutable answer = repairEscapes mode answer
        // A <think> example in code or within the answer stays untouched.
        let leading = answer.TrimStart()
        if leading.StartsWith("<think>", StringComparison.OrdinalIgnoreCase) then
            let finish = leading.IndexOf("</think>", StringComparison.OrdinalIgnoreCase)
            if finish >= 7 then
                let thought = leading.Substring(7, finish - 7).Trim('\n')
                reasoning <- if reasoning = "" then thought else reasoning + "\n\n" + thought
                answer <- leading.Substring(finish + 8).TrimStart('\r', '\n')
        reasoning, answer

    let render mode (value: JsonElement) =
        try
            let reasoning, answer = parts mode value
            if reasoning = "" && answer = "" then
                Error "No textual answer or reasoning was found in this response."
            elif reasoning = "" then Ok answer
            else
                // Keep model Markdown, but do not let it close our disclosure.
                let safeReasoning = Regex.Replace(reasoning, "</?(?:details|summary)(?=[\\s>])", MatchEvaluator(fun m -> "&lt;" + m.Value.Substring(1)), RegexOptions.IgnoreCase)
                Ok ("<details>\n<summary>Reasoning</summary>\n\n" + safeReasoning + "\n\n</details>\n\n" + answer)
        with
        | :? InvalidOperationException as ex -> Error ex.Message
        | :? ArgumentException as ex -> Error ex.Message

    let assistant mode value =
        let reasoning, answer = parts mode value
        // Preserve model text, not the presentation-only <details> wrapper.
        JsonSerializer.SerializeToElement({| role = "assistant"; content = answer; reasoning_content = if reasoning = "" then null else reasoning |})

    let write (ctx: HttpContext) status text =
        task {
            ctx.Response.StatusCode <- status
            ctx.Response.ContentType <- "text/markdown; charset=utf-8"
            ctx.Response.Headers["X-Content-Type-Options"] <- "nosniff"
            ctx.Response.Headers["Cache-Control"] <- "no-store"
            do! ctx.Response.WriteAsync(text, ctx.RequestAborted)
        }

    let handle (ctx: HttpContext) =
        task {
            use reader = new IO.StreamReader(ctx.Request.Body, Text.Encoding.UTF8)
            let! raw = reader.ReadToEndAsync(ctx.RequestAborted)
            let mode = if ctx.Request.Query.ContainsKey("escapes") then string ctx.Request.Query["escapes"] else "auto"
            match render mode (parse raw) with
            | Ok markdown -> do! write ctx 200 markdown
            | Error error -> do! write ctx 400 error
        } :> Task
