namespace OpenAiBufferDaemon

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

type BboxPlan = {
    Sync: obj
    History: ResizeArray<JsonElement>
    mutable Draft: JsonElement option
    mutable Active: JsonElement option
    mutable Epoch: int64
}

module Bbox =
    let private sessions = Dictionary<string, BboxPlan>()
    let private sync = obj()
    let mutable private objectCount = 1

    /// The plain-integer control setting: how many of the planned names bboxask takes, most likely first.
    let private count() = Volatile.Read(&objectCount)
    let private setCount value = Volatile.Write(&objectCount, value)

    let private plan (ctx: HttpContext) =
        let name = if ctx.Request.Query.ContainsKey("session") then string ctx.Request.Query["session"] else "default"
        if name.Length = 0 || name.Length > 128 || name |> Seq.exists Char.IsControl then invalidArg "session" "Invalid session name."
        lock sync (fun () ->
            match sessions.TryGetValue(name) with
            | true, state -> state
            | false, _ ->
                if sessions.Count >= 128 then Inference.fail 409 "The 128 picture sessions are full; restart to reset them."
                let state = { Sync = obj(); History = ResizeArray(); Draft = None; Active = None; Epoch = 0L }
                sessions.Add(name, state)
                state)

    let private require condition message = if not condition then invalidArg "plan" message
    let private memberValue name value = Helpers.tryGetProperty name value |> Option.defaultWith (fun () -> invalidArg "plan" $"Missing {name}.")
    let private keys allowed (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Object) "Expected a JSON object."
        let seen = HashSet<string>(StringComparer.Ordinal)
        for property in value.EnumerateObject() do
            require (seen.Add property.Name) $"Duplicate member: {property.Name}."
            require (List.contains property.Name allowed) $"Unsupported member: {property.Name}."
    let private phrase (value: string) =
        require (not (String.IsNullOrWhiteSpace(value)) && Encoding.UTF8.GetByteCount(value) <= 4096 && not (value |> Seq.exists Char.IsControl))
            "Names and instructions must be nonempty, printable and at most 4096 UTF-8 bytes."

    /// A model's double-encoded tool argument is repaired here instead of costing a retry; text
    /// that is not a JSON string is handed back untouched so the real parser reports the reason.
    let private unwrap (text: string) =
        if not (text.StartsWith("\"")) then text
        else
            try
                use doc = JsonDocument.Parse text
                let inner = Inference.stringValue doc.RootElement
                if String.IsNullOrWhiteSpace inner then text else inner
            with :? JsonException -> text

    /// A plan is a list of named targets; the picture and the coordinates arrive only at ask time.
    type private target = { Name: string; Kind: string; Instructions: string }

    let private targets (plan: JsonElement) =
        keys [ "objects" ] plan
        let items = memberValue "objects" plan
        require (items.ValueKind = JsonValueKind.Array && items.GetArrayLength() >= 1 && items.GetArrayLength() <= 8)
            "A plan names 1-8 objects; the object count is 1-8."
        items.EnumerateArray()
        |> Seq.map (fun item ->
            keys [ "name"; "kind"; "instructions" ] item
            let name = memberValue "name" item
            let kind = memberValue "kind" item
            let instructions = memberValue "instructions" item
            require (name.ValueKind = JsonValueKind.String) "An object name must be a string."
            require (kind.ValueKind = JsonValueKind.String && List.contains (kind.GetString()) [ "where"; "isdoes" ])
                "An object kind must be where (a box) or isdoes (a presence gate plus its region)."
            require (instructions.ValueKind = JsonValueKind.String) "An object instruction must be a string."
            let nameText = Inference.stringValue name
            phrase nameText
            phrase (Inference.stringValue instructions)
            { Name = nameText; Kind = Inference.stringValue kind; Instructions = Inference.stringValue instructions })
        |> Seq.toArray

    let private planPrompt = """Look for things in one picture that is supplied later, working from the user's request. Reply with one valid JSON object: {"objects":[{"name":"short handle","kind":"where|isdoes","instructions":"Where is ... in the picture?"}]}. Use 1-8 objects, the ones most likely to be present first, each with a different name; a name is a short readable label the user reads back, never coordinates. Use kind where for identify / where / what-is-it requests, one object per thing asked for. Use kind isdoes for is / does / can requests: the answer is how likely the thing is to be there, and the region names the part of the picture the claim is about. Write each instruction as one isolated question about that one object; the picture is the only evidence. When the request asks for several things of one kind (every logo, each price tag), name them separately and concretely. Never invent coordinates, and never ask about the picture's quality, how confident an answer is, or the list itself. The user confirms the list before anything is measured. When the user changes their mind, change only what they asked for - rename a label, drop one, name one more - and keep every other entry and its order; the turn before you holds the previous list."""

    let private message role content = Inference.json {| role = role; content = content |}

    /// The plan read back as the numbered labels the marked-up picture will carry.
    let private listing (plan: JsonElement) =
        let described item =
            if item.Kind = "isdoes" then "yes/no + region" else "region"
        plan
        |> targets
        |> Array.mapi (fun index item -> sprintf "%d: %s (%s) \u2014 %s" (index + 1) item.Name (described item) item.Instructions)
        |> Array.toList

    let set options stopping (ctx: HttpContext) =
        Inference.runUnlocked options stopping (fun ct context -> (task {
            let state = plan context
            if context.Request.Method = "GET" then
                let status = lock state.Sync (fun () ->
                    Inference.json {|
                        draft = state.Draft
                        active = state.Active
                        objects = count()
                        turns = state.History.Count / 2
                        ask = "POST the picture to /v1/bboxask; POST an integer here to change how many of these one look checks." |})
                do! Formatting.write context 200 (Inference.codeBlock "json" (status.GetRawText()))
            else
                let! raw = Inference.readText (1024 * 1024) ct context
                let input = Inference.textBody context raw
                if String.IsNullOrWhiteSpace input then invalidArg "message" "Send a request to plan, an object count 1-8, Y to activate, or N to clear this session."
                let trimmed = input.Trim()
                if trimmed |> Seq.forall Char.IsDigit then
                    let parsed =
                        match Int32.TryParse(trimmed) with
                        | true, value when value >= 1 && value <= 8 -> value
                        | _ -> Inference.fail 400 "The number of things one look checks is an integer 1-8."
                    setCount parsed
                    do! Formatting.write context 200 $"One look will check the first {parsed} of these, most likely first. POST the picture to /v1/bboxask.\n"
                else
                    match trimmed.ToUpperInvariant() with
                    | "N" ->
                        lock state.Sync (fun () ->
                            state.Epoch <- state.Epoch + 1L
                            state.Draft <- None
                            state.Active <- None
                            state.History.Clear())
                        do! Formatting.write context 200 "This list of things to look for is dropped, with its history and what was running. Describe the picture and what to find again.\n"
                    | "Y" ->
                        let saved = lock state.Sync (fun () ->
                            match state.Draft with
                            | None -> Inference.fail 409 "Nothing to confirm yet: describe what to look for first."
                            | Some draft ->
                                targets draft |> ignore
                                state.Active <- Some draft
                                state.Epoch <- state.Epoch + 1L
                                draft)
                        let lines = listing saved
                        let taken = min (count()) lines.Length
                        do! Formatting.write context 200 ($"One look per picture will check these, in this order (the first {taken}):\n" + String.concat "\n" lines[0..taken-1] + "\n\n" + Inference.codeBlock "json" (saved.GetRawText()))
                    | text when text.StartsWith "{" || text.StartsWith "[" ->
                        // A caller may supply the list itself - a fixed question nobody should reword.
                        // It is validated exactly as a drafted plan would be, then held for review.
                        try
                            // The matched text is upper-cased for the Y/N comparison; the list itself is
                            // read from the request unchanged, or its member names would lie.
                            use doc = JsonDocument.Parse(input.Trim())
                            targets doc.RootElement |> ignore
                            let saved = doc.RootElement.Clone()
                            lock state.Sync (fun () -> state.Draft <- Some saved)
                            let lines = listing saved
                            let taken = min (count()) lines.Length
                            do! Formatting.write context 200 ("What I will look for, most likely to be there first:\n"
                                + String.concat "\n" lines
                                + $"\n\nY starts looking; N drops this. One look checks the first {taken} of these.\n")
                        with
                        | :? JsonException ->
                            do! Formatting.write context 400 "That is not valid JSON. Send {\"objects\":[{\"name\":\"the cursor\",\"kind\":\"where\",\"instructions\":\"Where is the cursor?\"}]}.\n"
                        | :? ArgumentException as ex ->
                            do! Formatting.write context 400 $"That list is not usable: {ex.Message}\n"
                    | _ ->
                        do! Inference.inLane (fun () -> task {
                            let epoch, history = lock state.Sync (fun () -> state.Epoch, state.History.ToArray())
                            let! modelId = Inference.model options ct
                            let turns = ResizeArray<JsonElement>()
                            turns.Add(message "system" planPrompt)
                            turns.AddRange(history)
                            turns.Add(message "user" input)
                            if (turns |> Seq.sumBy (fun turn -> Encoding.UTF8.GetByteCount(turn.GetRawText()))) > 1024 * 1024 then
                                Inference.fail 413 "The refinement history exceeds 1 MiB; send N to start over."
                            let mutable candidate = None
                            let mutable error = ""
                            let mutable attempt = 0
                            while candidate.IsNone && attempt < 3 do
                                let messages =
                                    if attempt = 0 then turns.ToArray()
                                    else Array.append (turns.ToArray()) [| message "user" ("That was not JSON I could use - fix it and reply again: " + error) |]
                                try
                                    let! serialized =
                                        Inference.submitTool options modelId "submit_look_plan" "Return the complete list of things to look for as JSON text." "look_json" messages ct
                                    let text = unwrap serialized
                                    use doc = JsonDocument.Parse text
                                    targets doc.RootElement |> ignore
                                    candidate <- Some (doc.RootElement.Clone())
                                with
                                | :? ArgumentException as ex -> error <- ex.Message
                                | :? JsonException -> error <- "The reply was not valid JSON."
                                attempt <- attempt + 1
                            let valid = candidate |> Option.defaultWith (fun () -> Inference.fail 502 ("NInfer did not produce a usable list after three attempts: " + error))
                            lock state.Sync (fun () ->
                                if state.Epoch <> epoch then Inference.fail 409 "The list changed while this was being worked out; the late answer was discarded."
                                state.Draft <- Some valid
                                state.History.Add(message "user" input)
                                state.History.Add(message "assistant" (valid.GetRawText())))
                            let taken = min (count()) (targets valid |> Array.length)
                            let head = "What I will look for, most likely to be there first:\n" + String.concat "\n" (listing valid) + "\n\n"
                            do! Formatting.write context 200 (head + Inference.codeBlock "json" (Json.serialize valid) + $"\nY starts looking; N drops this. An integer 1-8 changes how many of these one look checks (now {taken}), or send more text to change the list - \"call the badge SM 120\", for example.\n")
                        })
        } :> Task)) ctx

    type private located = {
        Index: int
        Name: string
        Kind: string
        Aggregate: string
        Coordinates: (string * float) array
        Confidence: float
        Edge: float
        Presence: float option
        Error: string
    }

    let private number (value: JsonElement) =
        let mutable number = 0.0
        if value.ValueKind = JsonValueKind.Number && value.TryGetDouble(&number) && Double.IsFinite number then number else Double.NaN

    let private integer (value: JsonElement) =
        let mutable number = 0
        if value.TryGetInt32(&number) then number else 0

    let private escape (value: string) = value.Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ")

    /// One located object as the caller reads it. A failed object has no scores: they are
    /// omitted, never a NaN the JSON writer would have to invent a name for.
    let private describe (entry: located) =
        {| index = entry.Index
           name = entry.Name
           kind = entry.Kind
           aggregate = entry.Aggregate
           box = dict [ for (edge, value) in entry.Coordinates -> edge, box value ]
           confidence = if Double.IsNaN entry.Confidence then None else Some entry.Confidence
           weakest_edge = if Double.IsNaN entry.Edge then None else Some entry.Edge
           presence = entry.Presence
           error = if entry.Error = "" then None else Some entry.Error |}

    /// The helper's own argument: named boxes, in the shape a Box answer already has.
    let private boxesOf (found: located array) =
        found
        |> Array.map (fun entry ->
            {| name = entry.Name
               aggregate = entry.Aggregate
               box = dict [ for (edge, value) in entry.Coordinates -> edge, box value ]
               confidence = entry.Confidence |})

    /// A file the picture helper wrote: the crop itself, not its bytes.
    type private cutout = { Name: string; Path: string; Width: int; Height: int; Bytes: int64 }

    let private cropOf (inlineBytes: bool) (item: cutout) =
        {| name = item.Name
           path = item.Path
           width = item.Width
           height = item.Height
           bytes = item.Bytes
           image_base64 = if inlineBytes then Some(File.ReadAllBytes item.Path |> Convert.ToBase64String) else None |}

    let private digest (bytes: byte[]) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let private magic (bytes: byte[]) =
        match bytes with
        | b when b.Length >= 4 && b[0] = 0x89uy && b[1] = 0x50uy && b[2] = 0x4Euy && b[3] = 0x47uy -> ".png"
        | b when b.Length >= 3 && b[0] = 0xFFuy && b[1] = 0xD8uy && b[2] = 0xFFuy -> ".jpg"
        | b when b.Length >= 4 && b[0] = 0x47uy && b[1] = 0x49uy && b[2] = 0x46uy && b[3] = 0x38uy -> ".gif"
        | b when b.Length >= 12 && b[0] = 0x52uy && b[1] = 0x49uy && b[2] = 0x46uy && b[3] = 0x46uy && b[8] = 0x57uy && b[9] = 0x45uy && b[10] = 0x42uy && b[11] = 0x50uy -> ".webp"
        | b when b.Length >= 2 && b[0] = 0x42uy && b[1] = 0x4Duy -> ".bmp"
        | _ -> ".img"

    let private padOf (query: string -> string) =
        match Double.TryParse(query "pad") with
        | true, value when Double.IsFinite value && value >= 0.0 && value <= 1.0 -> value
        | _ -> 0.0

    /// Magnification of a cut-out. The answer comes back as a fraction of the picture it was measured
    /// over, so growing that picture cannot sharpen the arithmetic - what it can do is make a small mark
    /// big enough for the model to place, rather than guess at.
    let private scaleOf (query: string -> string) =
        match Double.TryParse(query "scale") with
        | true, value when Double.IsFinite value && value >= 1.0 && value <= 4.0 -> value
        | _ -> 1.0

    /// Every picture and box print lands in the data directory under a content hash, so a repeat ask
    /// reuses the same file - and the NInfer picture cache keyed on that path.
    let private save name (bytes: byte[]) =
        Files.ensureDataDirectory()
        let path = Path.Combine(Files.dataDirectory, name)
        if not (File.Exists path) then
            let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
            try
                File.WriteAllBytes(temporary, bytes)
                File.Move(temporary, path, true)
            with :? IOException -> ()
        path

    let private prune keep =
        try
            let prints = Directory.GetFiles(Files.dataDirectory, "bbox-*") |> Array.sortBy File.GetLastWriteTimeUtc |> Array.rev
            if prints.Length > keep then
                prints[keep..] |> Array.iter (fun path -> try File.Delete path with :? IOException -> ())
        with :? IOException -> ()

    /// One crop run: the helper writes the files and reports them, so no megabyte rides the pipe.
    let private cut options (boxes: string) pad scale (bytes: byte[]) ct = task {
        let material = boxes + $"|pad{pad:R}|scale{scale:R}"
        let key = (digest (Encoding.UTF8.GetBytes material)).[0..15]
        let! result =
            Perception.runImageHelper options
                [ "--operation"; "crop"; "--boxes"; boxes; "--pad"; sprintf "%g" pad; "--scale"; sprintf "%g" scale
                  "--out"; Path.Combine(Files.dataDirectory, $"bbox-{key}-crop") ] bytes ct
        prune 32
        return
            [ for entry in result.GetProperty("crops").EnumerateArray() ->
                let path = Inference.stringValue (entry.GetProperty "path")
                { Name = Inference.stringValue (entry.GetProperty "name")
                  Path = path
                  Width = integer (entry.GetProperty "width")
                  Height = integer (entry.GetProperty "height")
                  Bytes = FileInfo(path).Length } ]
    }

    let private print options (boxes: string) (bytes: byte[]) ct = task {
        let key = (digest (Encoding.UTF8.GetBytes boxes)).[0..15]
        let path = Path.Combine(Files.dataDirectory, $"bbox-{key}-boxes.png")
        let! result = Perception.runImageHelper options [ "--operation"; "bbox"; "--boxes"; boxes; "--out"; path ] bytes ct
        prune 32
        return {| path = path
                  width = integer (result.GetProperty "width")
                  height = integer (result.GetProperty "height")
                  bytes = FileInfo(path).Length |}
    }

    /// The picture is the only input: every question and every title comes from the activated plan.
    let ask options stopping (ctx: HttpContext) =
        Inference.run options stopping (fun ct context -> (task {
            let state = plan context
            let query key = string context.Request.Query.[key]
            if context.Request.Method = "GET" then
                let active = lock state.Sync (fun () -> state.Active)
                match active with
                | None -> do! Formatting.write context 200 "Nothing is set up to look for yet. Describe the picture and what to find at /v1/bboxset, confirm with Y, then POST the picture here: each of the first N things becomes one look.\n"
                | Some saved ->
                    let planned = targets saved
                    let taken = min (count()) planned.Length
                    let titles = String.Join(", ", planned |> Array.map (fun item -> item.Name))
                    do! Formatting.write context 200 ($"Ready to look for: {titles}. One look per picture checks the first {taken} of them; POST an integer 1-8 to /v1/bboxset to change that.\n")
            else
                let epoch, active =
                    lock state.Sync (fun () ->
                        state.Epoch,
                        state.Active |> Option.defaultWith (fun () -> Inference.fail 409 "Describe what to look for at /v1/bboxset and confirm with Y first."))
                let ensureCurrent() =
                    lock state.Sync (fun () -> if state.Epoch <> epoch then Inference.fail 409 "The list of things to look for changed while this was running; the stale answer was discarded.")
                let format = if query "format" = "" then "markdown" else (query "format").ToLowerInvariant()
                if not (List.contains format [ "markdown"; "json"; "png" ]) then Inference.fail 400 "format must be markdown, json or png."
                let skipPrint = query "annotate" = "0"
                if format = "png" && skipPrint then Inference.fail 400 "There is no marked-up picture to return: annotate=0 turns the drawing off."
                let wantCrops = query "crop" = "1"
                let inlineBytes = query "inline" = "1"
                let pad = padOf query
                let shared =
                    let text = (query "state").Trim()
                    if String.IsNullOrWhiteSpace text then "Nothing is known about this picture in advance; judge it from its own pixels." else text
                let! bytes = Inference.image ct context
                let planned = targets active
                let chosen = planned |> Array.take (min (count()) planned.Length)
                let shortHash = (digest bytes).[0..31]
                let source = save ($"bbox-{shortHash}{magic bytes}") bytes
                let! modelId = Inference.model options ct
                // One object per native request, serialized on purpose: a box is four independently
                // scored grid questions, so fanning them over states would only repeat the picture.
                let! results = task {
                    let collected = ResizeArray<located>()
                    for (index, item) in chosen |> Array.indexed do
                        let questions =
                            dict [
                                "box", box {| ``type`` = "box"; instructions = item.Instructions |}
                                if item.Kind = "isdoes" then
                                    "presence",
                                    box {| ``type`` = "noul"
                                           instructions = $"Is {item.Name} present anywhere in this picture?"
                                           criteria = dict [ "true", box $"The picture shows {item.Name}."; "false", box $"The picture shows no {item.Name}." ] |}
                            ]
                        let body = Inference.json {| model = modelId; state = $"{shared}\nLook for: {item.Name}"; questions = questions; images = [| source |] |}
                        let! outcome = task {
                            try
                                let validated = Jev.validate body
                                let! response = Inference.request options "/v1/systemone" (Some validated) ct
                                ensureCurrent()
                                do Jev.summarize validated response |> ignore
                                return Ok response
                            with
                            | PipeFailure(status, message) -> return Error $"NInfer returned HTTP {status}: {message}"
                            | :? ArgumentException -> return Error "NInfer returned an answer that failed the Jev box invariants."
                        }
                        match outcome with
                        | Error reason ->
                            collected.Add { Index = index + 1; Name = item.Name; Kind = item.Kind; Aggregate = ""; Coordinates = [||]; Confidence = Double.NaN; Edge = Double.NaN; Presence = None; Error = reason }
                        | Ok response ->
                            let answers = memberValue "answers" response
                            let answer = memberValue "box" answers
                            let edges = memberValue "edges" answer
                            collected.Add {
                                Index = index + 1
                                Name = item.Name
                                Kind = item.Kind
                                Aggregate = Inference.stringValue (memberValue "aggregate" answer)
                                Coordinates = [| for edge in [ "top"; "bottom"; "left"; "right" ] -> edge, number (memberValue edge (memberValue "box" answer)) |]
                                Confidence = number (memberValue "confidence" answer)
                                Edge = [ for edge in [ "top"; "bottom"; "left"; "right" ] -> number (memberValue "probability" (memberValue edge edges)) ] |> List.min
                                Presence = Helpers.tryGetProperty "presence" answers |> Option.map (fun gate -> number (memberValue "noul" gate))
                                Error = ""
                            }
                    return collected.ToArray()
                }
                let found = results |> Array.filter (fun entry -> entry.Error = "")
                let boxesText = (Inference.json (boxesOf found)).GetRawText()
                let! annotated = task {
                    if Array.isEmpty found || skipPrint then return None
                    else
                        let! art = print options boxesText bytes ct
                        return Some art
                }
                let! crops = task {
                    if Array.isEmpty found || not wantCrops then return []
                    else return! cut options boxesText pad 1.0 bytes ct
                }
                let base64 path = if inlineBytes then Some(File.ReadAllBytes path |> Convert.ToBase64String) else None
                let answered, failed = found.Length, results.Length - found.Length
                let status = if answered = 0 && failed > 0 then 502 else 200
                match format with
                | "png" ->
                    match annotated with
                    | None -> Inference.fail 503 (if answered = 0 then "Nothing was located, so there is no box print to return." else "The box print was skipped.")
                    | Some art ->
                        context.Response.ContentType <- "image/png"
                        context.Response.Headers.CacheControl <- "no-store"
                        let payload = File.ReadAllBytes art.path
                        do! context.Response.Body.WriteAsync(ReadOnlyMemory payload, ct)
                | "json" ->
                    context.Response.StatusCode <- status
                    context.Response.ContentType <- "application/json; charset=utf-8"
                    context.Response.Headers.CacheControl <- "no-store"
                    let payload =
                        {| asked = results.Length
                           answered = answered
                           failed = failed
                           objects = Array.map describe results
                           picture = box {| path = source; bytes = int64 bytes.Length |}
                           annotated =
                               annotated
                               |> Option.map (fun art -> box {| path = art.path; width = art.width; height = art.height; bytes = art.bytes; image_base64 = base64 art.path |})
                           crops = List.map (cropOf inlineBytes) crops |}
                    do! context.Response.WriteAsync(Json.serialize payload, ct)
                | _ ->
                    let lines = ResizeArray<string>()
                    lines.Add($"## What was found in the picture: {answered} of {results.Length}\n")
                    lines.Add("Each thing is one native request to the vision model, one at a time inside this one hold. Coordinates are normalized: 0.00 is the picture's top-left corner, 1.00 its bottom-right one. `Weakest edge` is the least certain of the four coordinate answers; `Presence` only appears for yes/no questions. Both describe the model's own spread, not calibrated accuracy.\n")
                    if answered > 0 then
                        lines.Add("| # | Object | top | bottom | left | right | Weakest edge | Confidence | Presence |")
                        lines.Add("| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
                        for entry in found do
                            let coord edge =
                                match entry.Coordinates |> Array.tryFind (fun (name, _) -> name = edge) with
                                | Some(_, value) -> $"{value:F2}"
                                | None -> "\u2014"
                            let t, b, l, r = coord "top", coord "bottom", coord "left", coord "right"
                            let gate = match entry.Presence with Some value -> $"{value:F4}" | None -> "\u2014"
                            lines.Add($"| {entry.Index} | {escape entry.Name} | {t} | {b} | {l} | {r} | {entry.Edge:F4} | {entry.Confidence:F4} | {gate} |")
                    lines.Add("")
                    match annotated with
                    | Some art -> lines.Add($"Marked-up picture: {art.path} ({art.width}x{art.height}, {art.bytes / 1024L} KiB) \u2014 not shown in the conversation. Open that file, or ask again with ?format=png for its bytes.")
                    | None -> ()
                    for crop in crops do
                        lines.Add($"Cut-out: {escape crop.Name} \u2192 {crop.Path} ({crop.Width}x{crop.Height}, {crop.Bytes / 1024L} KiB)")
                    for entry in results do
                        if entry.Error <> "" then lines.Add($"Failed: {entry.Index}: {escape entry.Name} \u2014 {escape entry.Error}")
                    do! Formatting.write context status (String.concat "\n" lines + "\n")
        } :> Task)) ctx

    /// A reusable crop: coordinates in, the object out. No model, no plan, no lane.
    let crop options stopping (ctx: HttpContext) =
        Inference.runUnlocked options stopping (fun ct context -> (task {
            let query key = string context.Request.Query.[key]
            if context.Request.Method = "GET" then
                do! Formatting.write context 200 "POST a picture to /v1/bboxcrop with the box in the query: ?top=0.69&bottom=0.82&left=0.09&right=0.32 (a fraction 0-1 or a pixel count), or ?boxes=[{\"name\":\"cta\",\"box\":{\"top\":0.1,\"bottom\":0.4,\"left\":0.2,\"right\":0.8}}] for up to 8 at once. ?pad=0.05 widens each side by that fraction of itself; ?scale=2 magnifies the cut-out, which is how a small mark becomes big enough to place rather than guess at; format=png (default) returns the first crop, ?format=json writes every crop to a file and lists them.\n"
            else
                let format = if query "format" = "" then "png" else (query "format").ToLowerInvariant()
                if not (List.contains format [ "png"; "json" ]) then Inference.fail 400 "format must be png or json."
                let coordinate key (value: JsonElement) =
                    let parsed = number value
                    if Double.IsNaN parsed || parsed < 0.0 then Inference.fail 400 $"{key} must be a fraction 0-1 or a pixel count, got {value.GetRawText()}."
                    parsed
                let named name = if String.IsNullOrWhiteSpace name then "object" else name.Trim()
                // Coordinates arrive as model-authored JSON, so the ordinary slips are repaired
                // here - one object instead of an array, a double-encoded string, a number written
                // as a string - and anything left over names the field and shows what was found.
                let amount key (value: JsonElement) =
                    let parsed =
                        match value.ValueKind with
                        | JsonValueKind.String ->
                            match Double.TryParse((Inference.stringValue value).Trim()) with
                            | true, value when Double.IsFinite value -> value
                            | _ -> Double.NaN
                        | _ -> number value
                    if Double.IsNaN parsed || parsed < 0.0 then
                        Inference.fail 400 $"{key} must be a fraction 0-1 or a pixel count, got {value.GetRawText()}."
                    parsed
                let required key (owner: JsonElement) =
                    match Helpers.tryGetProperty key owner with
                    | Some value -> value
                    | None -> Inference.fail 400 $"{key} is missing: send top, bottom, left and right."
                let raw = unwrap ((query "boxes").Trim())
                let! boxes = task {
                    if raw.Length = 0 then
                        let value key =
                            match Double.TryParse(query key) with
                            | true, value when Double.IsFinite value && value >= 0.0 -> value
                            | _ -> Inference.fail 400 $"{key} must be a fraction 0-1 or a pixel count."
                        return Inference.json [| {| name = named (query "name"); box = {| top = value "top"; bottom = value "bottom"; left = value "left"; right = value "right" |} |} |]
                    else
                        try
                            use doc = JsonDocument.Parse raw
                            let items =
                                match doc.RootElement.ValueKind with
                                | JsonValueKind.Array -> doc.RootElement.EnumerateArray() |> Seq.toArray
                                | JsonValueKind.Object -> [| doc.RootElement |]
                                | _ -> Inference.fail 400 "boxes must be a JSON array of 1-8 objects, or one such object."
                            if items.Length > 8 then Inference.fail 400 $"boxes names {items.Length} objects; the limit is 8."
                            if Array.isEmpty items then Inference.fail 400 "boxes names no object; send at least one box."
                            let entries =
                                [ for item in items ->
                                    let area =
                                        match Helpers.tryGetProperty "box" item with
                                        | Some area when area.ValueKind = JsonValueKind.Object -> area
                                        | _ -> Inference.fail 400 "each boxes item needs a box object of four coordinates."
                                    {| name = named (Inference.tryString "name" item |> Option.defaultValue "object")
                                       box = dict [ for edge in [ "top"; "bottom"; "left"; "right" ] -> edge, box (amount edge (required edge area)) ] |} ]
                            return Inference.json (entries |> List.toArray)
                        with :? JsonException ->
                            return Inference.fail 400 "boxes is not valid JSON: send [{\"name\":\"cta\",\"box\":{\"top\":0.1,\"bottom\":0.4,\"left\":0.2,\"right\":0.8}}]."
                }
                let! bytes = Inference.image ct context
                let pad = padOf query
                let scale = scaleOf query
                let inlineBytes = query "inline" = "1"
                let! crops = cut options (boxes.GetRawText()) pad scale bytes ct
                match format with
                | "json" ->
                    context.Response.ContentType <- "application/json; charset=utf-8"
                    context.Response.Headers.CacheControl <- "no-store"
                    let payload =
                        {| cropped = crops.Length
                           pad = pad
                           scale = scale
                           crops = List.map (cropOf inlineBytes) crops |}
                    do! context.Response.WriteAsync(Json.serialize payload, ct)
                | _ ->
                    match crops with
                    | [] -> Inference.fail 503 "The crop helper returned no picture."
                    | first :: _ ->
                        context.Response.ContentType <- "image/png"
                        context.Response.Headers.CacheControl <- "no-store"
                        let payload = File.ReadAllBytes first.Path
                        do! context.Response.Body.WriteAsync(ReadOnlyMemory payload, ct)
        } :> Task)) ctx
