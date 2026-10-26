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

module Vision =
    let private allowedCoordinateSpaces =
        set [ "css-viewport"; "physical-screen"; "normalized-999" ]

    let private isNumber (value: JsonElement) =
        value.ValueKind = JsonValueKind.Number

    let private requireNonEmptyString fieldName (body: JsonElement) (errors: ResizeArray<string>) =
        match Helpers.tryGetProperty fieldName body with
        | Some value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) ->
            value.GetString() |> Option.ofObj
        | _ ->
            errors.Add($"{fieldName} must be a non-empty string.")
            None

    let private validatePositiveNumber fieldName (body: JsonElement) (errors: ResizeArray<string>) =
        match Helpers.tryGetProperty fieldName body with
        | Some value when isNumber value && value.GetDouble() > 0.0 -> ()
        | _ -> errors.Add($"{fieldName} must be a number greater than zero.")

    let private validateOptionalKind displayName propertyName expectedKind (body: JsonElement) (errors: ResizeArray<string>) =
        match Helpers.tryGetProperty propertyName body with
        | Some value when value.ValueKind <> expectedKind -> errors.Add($"{displayName} has an invalid JSON type.")
        | _ -> ()

    let private validateBoundingBox prefix (bbox: JsonElement) (errors: ResizeArray<string>) =
        if bbox.ValueKind <> JsonValueKind.Object then
            errors.Add($"{prefix} must be an object.")
        else
            for fieldName in [ "x"; "y"; "width"; "height" ] do
                match Helpers.tryGetProperty fieldName bbox with
                | Some value when isNumber value ->
                    if (fieldName = "width" || fieldName = "height") && value.GetDouble() < 0.0 then
                        errors.Add($"{prefix}.{fieldName} must be zero or greater.")
                | _ -> errors.Add($"{prefix}.{fieldName} must be a number.")

    let private validateNormalizedBox prefix (bbox: JsonElement) (errors: ResizeArray<string>) =
        if bbox.ValueKind <> JsonValueKind.Object then
            errors.Add($"{prefix} must be an object.")
        else
            for fieldName in [ "x1"; "y1"; "x2"; "y2" ] do
                match Helpers.tryGetProperty fieldName bbox with
                | Some value when value.ValueKind = JsonValueKind.Number ->
                    let mutable number = 0
                    if not (value.TryGetInt32(&number)) || number < 0 || number > 999 then
                        errors.Add($"{prefix}.{fieldName} must be an integer from 0 through 999.")
                | _ -> errors.Add($"{prefix}.{fieldName} must be an integer from 0 through 999.")

    let private validateObservation (body: JsonElement) =
        let errors = ResizeArray<string>()

        if body.ValueKind <> JsonValueKind.Object then
            errors.Add("Vision observation must be a JSON object.")
        else
            let schemaVersion = requireNonEmptyString "schema_version" body errors
            if schemaVersion |> Option.exists ((<>) "vision-observation/1") then
                errors.Add("schema_version must equal vision-observation/1.")

            requireNonEmptyString "source" body errors |> ignore

            match Helpers.tryGetProperty "request_id" body with
            | Some value when value.ValueKind <> JsonValueKind.String || String.IsNullOrWhiteSpace(value.GetString()) ->
                errors.Add("request_id must be a non-empty string when provided.")
            | _ -> ()

            let capturedAt = requireNonEmptyString "captured_at" body errors
            match capturedAt with
            | Some value ->
                let mutable parsed = DateTimeOffset.MinValue
                if not (DateTimeOffset.TryParse(value, &parsed)) then
                    errors.Add("captured_at must be a valid date-time string.")
            | None -> ()

            let coordinateSpace = requireNonEmptyString "coordinate_space" body errors
            if coordinateSpace |> Option.exists (allowedCoordinateSpaces.Contains >> not) then
                errors.Add("coordinate_space must be css-viewport, physical-screen, or normalized-999.")

            match Helpers.tryGetProperty "viewport" body with
            | Some viewport when viewport.ValueKind = JsonValueKind.Object ->
                validatePositiveNumber "width" viewport errors
                validatePositiveNumber "height" viewport errors
                match Helpers.tryGetProperty "device_pixel_ratio" viewport with
                | Some value when not (isNumber value && value.GetDouble() > 0.0) ->
                    errors.Add("viewport.device_pixel_ratio must be a number greater than zero.")
                | _ -> ()

                for fieldName in [ "left"; "top"; "scroll_x"; "scroll_y" ] do
                    match Helpers.tryGetProperty fieldName viewport with
                    | Some value when not (isNumber value) -> errors.Add($"viewport.{fieldName} must be a number.")
                    | _ -> ()

                match Helpers.tryGetProperty "monitor" viewport with
                | Some value when value.ValueKind = JsonValueKind.Number ->
                    let mutable monitor = 0
                    if not (value.TryGetInt32(&monitor)) || monitor < 0 then
                        errors.Add("viewport.monitor must be a non-negative integer.")
                | Some _ -> errors.Add("viewport.monitor must be a non-negative integer.")
                | None -> ()
            | _ -> errors.Add("viewport must be an object.")

            validateOptionalKind "screen_tag" "screen_tag" JsonValueKind.String body errors
            for fieldName in [ "window"; "page"; "metadata" ] do
                validateOptionalKind fieldName fieldName JsonValueKind.Object body errors

            match Helpers.tryGetProperty "elements" body with
            | Some elements when elements.ValueKind = JsonValueKind.Array ->
                elements.EnumerateArray()
                |> Seq.iteri (fun index element ->
                    let prefix = $"elements[{index}]"
                    if element.ValueKind <> JsonValueKind.Object then
                        errors.Add($"{prefix} must be an object.")
                    else
                        match Helpers.tryGetProperty "id" element with
                        | Some value when value.ValueKind = JsonValueKind.String -> ()
                        | Some value when value.ValueKind = JsonValueKind.Number ->
                            let mutable number = 0L
                            if not (value.TryGetInt64(&number)) then
                                errors.Add($"{prefix}.id must be a string or integer.")
                        | _ -> errors.Add($"{prefix}.id must be a string or integer.")

                        for fieldName in [ "role"; "source" ] do
                            match Helpers.tryGetProperty fieldName element with
                            | Some value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) -> ()
                            | _ -> errors.Add($"{prefix}.{fieldName} must be a non-empty string.")

                        validateOptionalKind $"{prefix}.name" "name" JsonValueKind.String element errors
                        validateOptionalKind $"{prefix}.state" "state" JsonValueKind.Object element errors
                        validateOptionalKind $"{prefix}.metadata" "metadata" JsonValueKind.Object element errors

                        match Helpers.tryGetProperty "bbox" element with
                        | Some bbox -> validateBoundingBox $"{prefix}.bbox" bbox errors
                        | None -> errors.Add($"{prefix}.bbox is required.")

                        match Helpers.tryGetProperty "bbox_normalized" element with
                        | Some bbox -> validateNormalizedBox $"{prefix}.bbox_normalized" bbox errors
                        | None -> ())
            | _ -> errors.Add("elements must be an array.")

        errors |> Seq.toList

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
            | Ok(_, bodyJson) ->
                match validateObservation bodyJson with
                | firstError :: remainingErrors ->
                    let details = String.concat " " (firstError :: remainingErrors)
                    do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_vision_observation" details
                | [] ->
                    let source = Helpers.tryGetString "source" bodyJson |> Option.defaultValue "unknown"
                    let elementCount = tryElementCount bodyJson |> Option.defaultValue 0
                    let requestId = Helpers.tryGetString "request_id" bodyJson
                    let observationId = Helpers.newId "obs"
                    let record =
                        {| observation_id = observationId
                           request_id = requestId
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
                           request_id = requestId
                           source = source
                           element_count = elementCount
                           latest_observation_file = Files.latestObservationPath
                           latest_observation_text_file = Files.latestObservationTextPath |})
        } :> Task

    let handleSchema (ctx: HttpContext) =
        task {
            let schemaPath = Path.Combine(AppContext.BaseDirectory, "contracts", "vision-observation.schema.json")
            if File.Exists(schemaPath) then
                ctx.Response.Headers["Access-Control-Allow-Origin"] <- "*"
                ctx.Response.ContentType <- "application/schema+json; charset=utf-8"
                do! ctx.Response.SendFileAsync(schemaPath)
            else
                do! Responses.writeError ctx StatusCodes.Status500InternalServerError "schema_not_found" "The bundled vision observation schema is unavailable."
        } :> Task
