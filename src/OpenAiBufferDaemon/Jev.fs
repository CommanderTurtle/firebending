namespace OpenAiBufferDaemon

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

type JevSession = {
    Sync: obj
    History: ResizeArray<JsonElement>
    mutable Draft: JsonElement option
    mutable Active: JsonElement option
    mutable Epoch: int64
}

module Jev =
    let private sessions = Dictionary<string, JevSession>()
    let private sync = obj()
    let private session (ctx: HttpContext) =
        let name = if ctx.Request.Query.ContainsKey("session") then string ctx.Request.Query["session"] else "default"
        if name.Length = 0 || name.Length > 128 || name |> Seq.exists Char.IsControl then invalidArg "session" "Invalid session name."
        lock sync (fun () ->
            match sessions.TryGetValue(name) with
            | true, state -> state
            | _ ->
                if sessions.Count >= 128 then Inference.fail 409 "The 128 Jev sessions are full; restart to reset them."
                let state = { Sync = obj(); History = ResizeArray(); Draft = None; Active = None; Epoch = 0L }
                sessions.Add(name, state)
                state)

    let private require condition message = if not condition then invalidArg "framework" message
    let private memberValue name value = Helpers.tryGetProperty name value |> Option.defaultWith (fun () -> invalidArg "framework" $"Missing {name}.")
    let private keys allowed (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Object) "Expected a JSON object."
        let seen = HashSet<string>(StringComparer.Ordinal)
        for property in value.EnumerateObject() do
            require (seen.Add property.Name) $"Duplicate member: {property.Name}."
            require (List.contains property.Name allowed) $"Unsupported member: {property.Name}."
    let private entry (value: JsonElement) =
        require (List.contains value.ValueKind [ JsonValueKind.String; JsonValueKind.Object; JsonValueKind.Array; JsonValueKind.Null ]) "State, instructions and criteria descriptions must be strings, objects, arrays or null."
    let private label (value: string) =
        require (not (String.IsNullOrWhiteSpace(value)) && Encoding.UTF8.GetByteCount(value) <= 4096 && not (value |> Seq.exists Char.IsControl)) "Question ids and choice labels must be nonempty, printable and at most 4096 UTF-8 bytes."

    /// Mirrors the native SystemOne wire contract; validation does not run inference.
    let validate (value: JsonElement) =
        keys [ "state"; "questions"; "model"; "images" ] value
        entry (memberValue "state" value)
        match Helpers.tryGetProperty "model" value with
        | Some model -> require (model.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(model.GetString()))) "model must be a nonempty string."
        | None -> ()
        // Absent means no images; an explicit null is not a list on the native wire.
        match Helpers.tryGetProperty "images" value with
        | None -> ()
        | Some sources ->
            require (sources.ValueKind = JsonValueKind.Array && sources.GetArrayLength() <= 8) "images must be an array of up to 8 image sources."
            for source in sources.EnumerateArray() do
                require (source.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(source.GetString()))) "Each image source must be a nonempty local path or data: URL."
        let questions = memberValue "questions" value
        require (questions.ValueKind = JsonValueKind.Object) "questions must be an object."
        let properties = questions.EnumerateObject() |> Seq.toArray
        require (properties.Length >= 1 && properties.Length <= 32) "Use 1-32 questions."
        require ((properties |> Array.map (fun p -> p.Name) |> Array.distinct |> Array.length) = properties.Length) "Duplicate question ids are not allowed."
        for property in properties do
            label property.Name
            let question = property.Value
            keys [ "type"; "instructions"; "criteria" ] question
            Helpers.tryGetProperty "instructions" question |> Option.iter entry
            let kind = memberValue "type" question
            require (kind.ValueKind = JsonValueKind.String) "Question type must be a string."
            match kind.GetString() with
            | "choice" ->
                let criteria = memberValue "criteria" question
                require (criteria.ValueKind = JsonValueKind.Object) "Choice criteria must be an object."
                let options = criteria.EnumerateObject() |> Seq.toArray
                require (options.Length >= 1 && options.Length <= 255) "Choice questions need 1-255 options."
                require ((options |> Array.map (fun p -> p.Name) |> Array.distinct |> Array.length) = options.Length) "Duplicate choice labels are not allowed."
                for option in options do label option.Name; entry option.Value
            | "noul" ->
                match Helpers.tryGetProperty "criteria" question with
                | Some criteria when criteria.ValueKind <> JsonValueKind.Null ->
                    keys [ "true"; "false" ] criteria
                    for option in criteria.EnumerateObject() do entry option.Value
                | _ -> ()
            | "score" ->
                let criteria = memberValue "criteria" question
                require (criteria.ValueKind = JsonValueKind.Array && criteria.GetArrayLength() >= 2 && criteria.GetArrayLength() <= 10) "Score criteria must be 2-10 ordered descriptions."
                for option in criteria.EnumerateArray() do entry option
            | "box" ->
                // Four independently scored grid coordinates; the picture rides in top-level images.
                match Helpers.tryGetProperty "images" value with
                | Some sources when sources.ValueKind = JsonValueKind.Array && sources.GetArrayLength() > 0 -> ()
                | _ -> invalidArg "framework" "box questions are asked of an image: send images."
                match Helpers.tryGetProperty "criteria" question with
                | None -> ()
                | Some criteria when criteria.ValueKind = JsonValueKind.Null -> ()
                | Some criteria ->
                    keys [ "step"; "aggregate" ] criteria
                    match Helpers.tryGetProperty "step" criteria with
                    | None -> ()
                    | Some step ->
                        let mutable number = 0.0
                        require (step.ValueKind = JsonValueKind.Number && step.TryGetDouble(&number) && Double.IsFinite number && number > 0.0 && number <= 1.0)
                            "box step must be a number greater than 0 and at most 1."
                    match Helpers.tryGetProperty "aggregate" criteria with
                    | None -> ()
                    | Some aggregate ->
                        require (aggregate.ValueKind = JsonValueKind.String && List.contains (aggregate.GetString()) [ "mode"; "mean"; "median" ])
                            "box aggregate must be mode, mean or median."
            | _ -> invalidArg "framework" "Question type must be choice, noul, score or box."
        require (Encoding.UTF8.GetByteCount(value.GetRawText()) <= 1024 * 1024) "Framework exceeds the 1 MiB SystemOne limit."
        value.Clone()

    let private message role content = Inference.json {| role = role; content = content |}
    let private prompt = """Design a read-only /v1/systemone request for the user's classification/scoring task. Return submit_jev_framework with framework_json containing ONE valid JSON object: {"state":"observation supplied later","questions":{"id":{"type":"choice|noul|score|box","instructions":"...","criteria":...}}}. Use noul for binary propositions: criteria is {"true":"...","false":"..."}, output is P(true), not yes/no prose. Use choice for mutually exclusive identities (such as individual playing cards), criteria maps each label to a description. Use score for 2-10 ordered levels (an array). Use box for where-in-the-picture questions: the request then needs top-level "images" with 1-8 local image paths or data: URLs, and criteria is optional, {"step":0.01,"aggregate":"mode|mean|median"} (mode answers the peak grid point, mean/median the distribution). Include explicit unknown/ambiguous options when warranted. Use 1-32 named questions, 1-255 options per choice; instructions/criterion descriptions accept text, object, array or null. No extra top-level properties or executable tools. The state is replaced by the next jevask input. Follow refinement requests; never activate/save the framework yourself. Never confuse confidence with calibrated accuracy. Shape for throughput: cost is prompt-bound, not request-bound (measured on a local single-model NInfer: ~0.05s per decision with one-line instructions, ~0.27s per decision with 5-criterion rubrics, and fan-out past ~4 concurrent requests buys nothing because the server serializes), so bundle every question that shares one state into this ONE framework — up to the 32-question limit — rather than asking for several sessions or asks, and keep rubrics short. Ask conditional subtype questions in the same request (they share the state's cost) and read them only when the parent answer selects that branch. Ask nothing about the judgment itself — no self-confidence, certainty or ambiguity question: the native distributions and the confidence field already carry it, and every extra question is one more decision to pay for on every state. Identical state + questions replay from a warm cache in ~0s, so a probe state must carry a distinguishing field or it measures a cache hit."""

    /// The picture rides with every state built from this framework; the native wire has no images key at all
    /// when the framework has none, because an explicit null is not a list.
    let private canonical modelId state questions (framework: JsonElement) =
        match Helpers.tryGetProperty "images" framework with
        | Some sources -> Inference.json {| model = modelId; state = state; questions = questions; images = sources |}
        | None -> Inference.json {| model = modelId; state = state; questions = questions |}

    let set options stopping (ctx: HttpContext) =
        Inference.runUnlocked options stopping (fun ct context -> (task {
            let state = session context
            if context.Request.Method = "GET" then
                let status = lock state.Sync (fun () -> Inference.json {| draft = state.Draft; active = state.Active; turns = state.History.Count / 2 |})
                do! Formatting.write context 200 (Inference.codeBlock "json" (status.GetRawText()))
            else
                let! raw = Inference.readText (1024 * 1024) ct context
                let input = Inference.textBody context raw
                if String.IsNullOrWhiteSpace input then invalidArg "message" "Send a question, a refinement, Y to activate, or N to clear this Jev session."
                match input.Trim().ToUpperInvariant() with
                | "N" ->
                    lock state.Sync (fun () ->
                        state.Epoch <- state.Epoch + 1L
                        state.Draft <- None
                        state.Active <- None
                        state.History.Clear())
                    do! Formatting.write context 200 "Draft, refinement history and activated framework cleared. Activate a new framework before /v1/jevask.\n"
                | "Y" ->
                    let saved = lock state.Sync (fun () ->
                        match state.Draft with
                        | None -> Inference.fail 409 "There is no validated draft to activate."
                        | Some draft ->
                            state.Active <- Some(validate draft)
                            state.Epoch <- state.Epoch + 1L
                            draft)
                    do! Formatting.write context 200 ("Activated for /v1/jevask.\n\n" + Inference.codeBlock "json" (saved.GetRawText()))
                | _ ->
                    do! Inference.inLane (fun () -> task {
                        let epoch, history = lock state.Sync (fun () -> state.Epoch, state.History.ToArray())
                        let! modelId = Inference.model options ct
                        let turns = ResizeArray<JsonElement>()
                        turns.Add(message "system" prompt)
                        turns.AddRange(history)
                        turns.Add(message "user" input)
                        if (turns |> Seq.sumBy (fun turn -> Encoding.UTF8.GetByteCount(turn.GetRawText()))) > 1024 * 1024 then
                            Inference.fail 413 "Refinement history exceeds 1 MiB; N clears this session, including its activated framework, before starting another."
                        let mutable candidate = None
                        let mutable error = ""
                        let mutable attempt = 0
                        while candidate.IsNone && attempt < 3 do
                            let messages = turns.ToArray()
                            let messages = if attempt = 0 then messages else Array.append messages [| message "user" ("Repair the invalid framework/tool format: " + error) |]
                            try
                                let! serialized = Inference.submitTool options modelId "submit_jev_framework" "Return the complete SystemOne framework as JSON text." "framework_json" messages ct
                                use doc = JsonDocument.Parse(serialized)
                                let value = validate doc.RootElement
                                candidate <- Some(canonical modelId (memberValue "state" value) (memberValue "questions" value) value)
                            with
                            | :? ArgumentException as ex -> error <- ex.Message
                            | :? JsonException -> error <- "The framework or tool arguments were not valid JSON."
                            attempt <- attempt + 1
                        let valid = candidate |> Option.defaultWith (fun () -> Inference.fail 502 ("NInfer did not produce a valid framework after three attempts: " + error))
                        lock state.Sync (fun () ->
                            if state.Epoch <> epoch then Inference.fail 409 "Draft was cleared/activated while inference was running; the late draft was discarded."
                            state.Draft <- Some valid
                            state.History.Add(message "user" input)
                            state.History.Add(message "assistant" (valid.GetRawText())))
                        do! Formatting.write context 200 (Inference.codeBlock "json" (Json.serialize valid) + "\nSave for /v1/jevask? Y/N\nReply with more text to refine instead.\n")
                    })
        } :> Task)) ctx

    let private probability (value: JsonElement) =
        let mutable number = 0.0
        if value.ValueKind <> JsonValueKind.Number || not (value.TryGetDouble(&number)) || not (Double.IsFinite number) || number < 0.0 || number > 1.0 then
            Inference.fail 502 "Jev returned an invalid probability."
        number

    let private tableText (value: string) = value.Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ")

    let summarize (framework: JsonElement) (response: JsonElement) =
        let questions = memberValue "questions" framework
        let answers = Helpers.tryGetProperty "answers" response |> Option.defaultWith (fun () -> Inference.fail 502 "Jev returned no answers.")
        if answers.ValueKind <> JsonValueKind.Object then Inference.fail 502 "Jev answers must be an object."
        let ids = questions.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Set.ofSeq
        let answerIds = answers.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.toArray
        if Set.ofArray answerIds <> ids || answerIds.Length <> ids.Count then Inference.fail 502 "Jev returned missing, duplicate or unsolicited answers."
        let lines = ResizeArray<string>()
        lines.Add("| Question | Result | Probability | Native confidence |")
        lines.Add("| --- | --- | ---: | ---: |")
        for property in questions.EnumerateObject() do
            let answer = memberValue property.Name answers
            if answer.ValueKind <> JsonValueKind.Object then Inference.fail 502 "Jev answer must be an object."
            let fields = answer.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.toArray
            if (Array.distinct fields).Length <> fields.Length then Inference.fail 502 "Jev returned duplicate answer members."
            let kind = memberValue "type" property.Value |> Inference.stringValue
            if Inference.tryString "type" answer <> Some kind then Inference.fail 502 "Jev answer type did not match the framework."
            if kind = "noul" then
                let yes = memberValue "noul" answer |> probability
                lines.Add($"| {tableText property.Name} | P(true) | {yes:F6} | n/a |")
            elif kind = "box" then
                let allowed = [ "type"; "aggregate"; "box"; "confidence"; "edges" ]
                if fields |> Array.exists (fun name -> not (List.contains name allowed)) then
                    Inference.fail 502 "Jev box answer has unsupported members."
                let aggregate = memberValue "aggregate" answer |> Inference.stringValue
                if not (List.contains aggregate [ "mode"; "mean"; "median" ]) then
                    Inference.fail 502 "Jev box answer used an unknown aggregate."
                let box = memberValue "box" answer
                if box.ValueKind <> JsonValueKind.Object || (box.EnumerateObject() |> Seq.toArray).Length <> 4 then
                    Inference.fail 502 "Jev box answer needs exactly four coordinates."
                let coordinate name =
                    match Helpers.tryGetProperty name box with
                    | Some value ->
                        let mutable number = 0.0
                        if value.ValueKind <> JsonValueKind.Number || not (value.TryGetDouble(&number)) || not (Double.IsFinite number) || number < 0.0 || number > 1.0 then
                            Inference.fail 502 $"Jev returned an invalid {name} coordinate."
                        number
                    | None -> Inference.fail 502 "Jev box answer is missing a coordinate."
                let top, bottom, left, right = coordinate "top", coordinate "bottom", coordinate "left", coordinate "right"
                if top > bottom || left > right then Inference.fail 502 "Jev returned an inverted box."
                let edges = memberValue "edges" answer
                if edges.ValueKind <> JsonValueKind.Object then Inference.fail 502 "Jev returned no edge distributions."
                // Every edge is scored on its own and the native side only ever sorts a pair; it never
                // re-reads a coordinate, so each answer must equal its own grid summary or be the sort.
                let chosen = ResizeArray<float>()
                let mutable weakest = 1.0
                for name in [ "top"; "bottom"; "left"; "right" ] do
                    let summary = memberValue name edges
                    if summary.ValueKind <> JsonValueKind.Object then Inference.fail 502 $"Jev returned no {name} distribution."
                    let grid = if aggregate = "mode" then "modal" elif aggregate = "mean" then "mean" else "median"
                    let value = memberValue grid summary
                    let mutable number = 0.0
                    if value.ValueKind <> JsonValueKind.Number || not (value.TryGetDouble(&number)) || number < 0.0 || number > 1.0 then
                        Inference.fail 502 $"Jev returned an invalid {name} distribution summary."
                    weakest <- min weakest (memberValue "probability" summary |> probability)
                    let moved =
                        match Helpers.tryGetProperty "moved" summary with
                        | Some flag when flag.ValueKind = JsonValueKind.True -> true
                        | Some flag when flag.ValueKind = JsonValueKind.False -> false
                        | _ -> Inference.fail 502 $"Jev returned no {name} moved flag."
                    let answered =
                        match name with
                        | "top" -> top | "bottom" -> bottom | "left" -> left | _ -> right
                    if moved <> (abs (number - answered) > 1e-9) then
                        Inference.fail 502 $"Jev box {name} did not match its edge distribution."
                    chosen.Add number
                let pair (a: float) (b: float) (low: float) (high: float) =
                    let sorted = Array.sort [| a; b |]
                    abs (low - sorted[0]) <= 1e-9 && abs (high - sorted[1]) <= 1e-9
                let values = chosen.ToArray()
                if not (pair values[0] values[1] top bottom) || not (pair values[2] values[3] left right) then
                    Inference.fail 502 "Jev box coordinates did not match its edge distributions."
                let confidence = memberValue "confidence" answer |> probability
                lines.Add($"| {tableText property.Name} | top {top:F2} bottom {bottom:F2} left {left:F2} right {right:F2} as {aggregate} | {weakest:F6} | {confidence:F6} |")
            else
                let probabilities = memberValue "probabilities" answer
                if probabilities.ValueKind <> JsonValueKind.Object then Inference.fail 502 "Jev returned no distribution."
                let expected =
                    let criteria = memberValue "criteria" property.Value
                    if kind = "choice" then criteria.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Set.ofSeq
                    else [0 .. criteria.GetArrayLength()-1] |> Seq.map string |> Set.ofSeq
                let values = probabilities.EnumerateObject() |> Seq.map (fun p -> p.Name, probability p.Value) |> Seq.toArray
                if (values |> Seq.map fst |> Set.ofSeq) <> expected || values.Length <> expected.Count || abs ((values |> Array.sumBy snd) - 1.0) > 0.001 then Inference.fail 502 "Jev returned an incomplete or duplicate distribution."
                let winner, score = values |> Array.maxBy snd
                let confidence = memberValue "confidence" answer |> probability
                if kind = "choice" then
                    let choice = Inference.tryString "choice" answer |> Option.defaultWith (fun () -> Inference.fail 502 "Jev returned no choice string.")
                    let selected = values |> Array.tryFind (fun (label, _) -> label = choice)
                    if selected.IsNone || snd selected.Value + 0.00000001 < score then Inference.fail 502 "Jev choice did not match its distribution."
                    lines.Add($"| {tableText property.Name} | {tableText choice} | {snd selected.Value:F6} | {confidence:F6} |")
                else
                    let result = memberValue "score" answer
                    let mutable mean = 0.0
                    if result.ValueKind <> JsonValueKind.Number || not (result.TryGetDouble(&mean)) || not (Double.IsFinite mean) then Inference.fail 502 "Invalid Jev score."
                    let calculated = values |> Array.sumBy (fun (key, p) -> float (int key) * p)
                    if abs (mean - calculated) > 0.001 then Inference.fail 502 "Jev score did not match its distribution."
                    lines.Add($"| {tableText property.Name} | {mean:F6} (modal level {winner}) | {score:F6} | {confidence:F6} |")
        String.concat "\n" lines + "\n\nScores describe the model distribution, not calibrated accuracy.\n\n" + Inference.codeBlock "json" (Json.serialize response)

    /// One line of state identity for batch headings; never a classification.
    let private preview (value: JsonElement) =
        let raw = value.GetRawText().Replace("\n", " ").Replace("\r", " ")
        tableText (if raw.Length <= 72 then raw else raw.Substring(0, 72) + "\u2026")

    /// `?batch=1` promotes a JSON array body from one array-rooted state to N states.
    let private stateList (raw: string) (isJson: bool) (batch: bool) =
        if not batch then
            if isJson then
                use doc = JsonDocument.Parse(raw)
                [ doc.RootElement.Clone() ]
            else [ Inference.json raw ]
        else
            if not isJson then Inference.fail 400 "batch=1 sends a JSON array of states; plain text stays one state."
            use doc = JsonDocument.Parse(raw)
            if doc.RootElement.ValueKind <> JsonValueKind.Array then
                Inference.fail 400 "batch=1 needs a JSON array of states; one state needs no batch flag."
            let items = doc.RootElement.EnumerateArray() |> Seq.toArray
            if items.Length = 0 then Inference.fail 400 "The batch has no states."
            if items.Length > 32 then Inference.fail 413 "A batch holds at most 32 states; split it into another request."
            items |> Array.map (fun item -> item.Clone()) |> Array.toList

    /// The native endpoint takes one state per request, so batch width is decision count.
    let private budget (stateCount: int) (questions: int) (options: InferenceOptions) =
        let decisions = stateCount * questions
        if decisions > 256 then
            Inference.fail 413 $"A batch holds at most 256 decisions: {stateCount} states x {questions} questions = {decisions}. Split it \u2014 measured 0.05-0.27s per decision against the {options.TimeoutSeconds}s whole-operation deadline."

    /// Per-state result inside a batch: an answer table, a validation verdict about the
    /// native answer, or a failed native call. Only the last two count as a failure.
    type private verdict =
        | Answered of table: string * native: JsonElement
        | Refused of reason: string
        | Unreachable of status: int * reason: string

    let private verdictReason = function
        | Refused reason -> reason
        | Unreachable(status, reason) -> $"NInfer returned HTTP {status}: {reason}"
        | Answered _ -> ""

    let ask options stopping ctx =
        Inference.run options stopping (fun ct context -> (task {
            let state = session context
            let epoch, framework = lock state.Sync (fun () ->
                state.Epoch, (state.Active |> Option.defaultWith (fun () -> Inference.fail 409 "Activate a framework with /v1/jevset and Y first.")))
            let ensureCurrent() = lock state.Sync (fun () ->
                if state.Epoch <> epoch then Inference.fail 409 "Jev framework was cleared/replaced while this request was running; the stale result was discarded.")
            let! raw = Inference.readText (1024 * 1024) ct context
            if String.IsNullOrWhiteSpace(raw) then Inference.fail 400 "Supply one text/JSON state; an empty pipe is not an observation."
            let batch = string context.Request.Query["batch"] = "1"
            let items =
                stateList raw ((string context.Request.ContentType).Contains("json", StringComparison.OrdinalIgnoreCase)) batch
            let questions = memberValue "questions" framework
            budget items.Length (questions.EnumerateObject() |> Seq.length) options
            let! modelId = Inference.model options ct
            // Validate every state before the first native call: a bad state is a 400,
            // never a half-answered batch.
            let prepared =
                items |> List.toArray |> Array.indexed
                |> Array.map (fun (index, input) -> index, input, canonical modelId input questions framework |> validate)
            ensureCurrent()
            let answers = ResizeArray<int * JsonElement * verdict>()
            for (index, input, request) in prepared do
                // A cleared/replaced framework aborts the whole call with 409; a per-state
                // native or validation failure is recorded verbatim and the rest still get asked.
                let! outcome = task {
                    let! response = task {
                        try
                            let! value = Inference.request options "/v1/systemone" (Some request) ct
                            ensureCurrent()
                            return Ok value
                        with
                        | PipeFailure(409, message) -> return raise (PipeFailure(409, message))
                        | PipeFailure(status, message) -> return Error(Unreachable(status, message))
                    }
                    match response with
                    | Error verdict -> return verdict
                    | Ok value ->
                        try return Answered(summarize request value, value)
                        with
                        | PipeFailure(_, message) -> return Refused message
                        | :? ArgumentException -> return Refused "Jev returned a malformed answer."
                }
                answers.Add((index, input, outcome))
            let total = answers.Count
            let failed = answers |> Seq.sumBy (fun (_, _, outcome) -> match outcome with Answered _ -> 0 | _ -> 1)
            let failedStatus = function
                | Answered _ -> 200
                | Unreachable(status, _) -> status
                | _ -> 502
            if string context.Request.Query["format"] = "json" then
                let entry = function
                    | Answered(_, native) -> box native
                    | Refused reason -> box {| error = reason |}
                    | Unreachable(status, reason) -> box {| status = status; error = reason |}
                let payload =
                    if not batch then entry (match answers[0] with _, _, outcome -> outcome)
                    else
                        box {|
                            answered = total - failed
                            failed = failed
                            results =
                                answers |> Seq.map (fun (index, input, outcome) ->
                                    box {| index = index; state = input; result = entry outcome |})
                            |}
                context.Response.StatusCode <-
                    if not batch then failedStatus (match answers[0] with _, _, outcome -> outcome)
                    elif failed = total then 502 else 200
                context.Response.ContentType <- "application/json; charset=utf-8"
                context.Response.Headers.CacheControl <- "no-store"
                do! context.Response.WriteAsync(Json.serialize payload, ct)
            elif not batch then
                // Exactly the single-state contract from before batches: answer table, or the
                // native/validation status with its verbatim reason.
                match answers[0] with
                | _, _, Answered(table, _) -> do! Formatting.write context 200 table
                | _, _, Refused reason -> do! Formatting.write context 502 reason
                | _, _, Unreachable(status, reason) -> do! Formatting.write context status reason
            else
                let mutable body =
                    sprintf "%d of %d states answered; each state is one native /v1/systemone request inside this one lane hold. Scores describe the model distribution, not calibrated accuracy.\n"
                        (total - failed) total
                for (index, input, outcome) in answers do
                    body <- body + sprintf "\n### State %d/%d \u2014 %s\n\n" (index + 1) total (preview input)
                    body <- body + (match outcome with Answered(table, _) -> table | _ -> "Error: " + verdictReason outcome + "\n")
                do! Formatting.write context (if failed = total then 502 else 200) body
        } :> Task)) ctx
