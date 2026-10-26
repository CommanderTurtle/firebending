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

module Zuko =
    let private tasketClient = new HttpClient()
    let private tempregPath = Path.Combine(Path.GetTempPath(), "tempreg.txt")

    let private requireSome error = function
        | Some value -> Ok value
        | None -> Error error

    let private readTextBody (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body, Encoding.UTF8)
            let! raw = reader.ReadToEndAsync()
            if String.IsNullOrWhiteSpace(raw) then
                return Error "Request body is empty."
            elif
                not (String.IsNullOrWhiteSpace(ctx.Request.ContentType))
                && (string ctx.Request.ContentType).Contains("json", StringComparison.OrdinalIgnoreCase)
            then
                try
                    use doc = JsonDocument.Parse(raw)
                    let root = doc.RootElement
                    match root.ValueKind with
                    | JsonValueKind.String ->
                        return root.GetString() |> Option.ofObj |> requireSome "JSON string is empty."
                    | JsonValueKind.Object ->
                        return
                            Helpers.tryGetString "content" root
                            |> Option.orElseWith (fun () -> Helpers.tryGetString "text" root)
                            |> Option.orElseWith (fun () -> Helpers.tryGetString "markdown" root)
                            |> requireSome "JSON body requires a content, text, or markdown string."
                    | _ ->
                        return Error "JSON body must be a string or an object containing content, text, or markdown."
                with ex ->
                    return Error $"Invalid JSON: {ex.Message}"
            else
                return Ok raw
        }

    let private normalizeLiteral (value: string) =
        let trimmed = value.Trim()
        if trimmed.Length >= 2
           && ((trimmed[0] = '"' && trimmed[trimmed.Length - 1] = '"')
               || (trimmed[0] = '\'' && trimmed[trimmed.Length - 1] = '\'')) then
            trimmed.Substring(1, trimmed.Length - 2).Trim()
        else
            trimmed

    let private normalizeMarkdown (value: string) =
        value
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim('\uFEFF', '\n')

    let private makeBlankRegistry() =
        // The canonical header occupies lines 0-6.  i1z1 initially addresses
        // the blank line at 7 so `rgd rs` can own all content and range updates.
        let blankZone = "0x0000007 : 0x0000007"
        let emptyZone = "0x0000000 : 0x0000000"
        let header =
            [| "regedited open"
               "index: 1"
               $"{blankZone} : {emptyZone} : {emptyZone}"
               "0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0"
               ""
               ""
               "" |]
            |> String.concat Environment.NewLine

        header + Environment.NewLine + Environment.NewLine

    let private writeRegistryWithRgd (markdown: string) (cancellationToken: CancellationToken) =
        task {
            try
                Files.writeText tempregPath (makeBlankRegistry())

                let startInfo = ProcessStartInfo()
                startInfo.FileName <- "rgd"
                startInfo.ArgumentList.Add("rs")
                startInfo.ArgumentList.Add(tempregPath)
                startInfo.ArgumentList.Add("i1z1")
                startInfo.RedirectStandardInput <- true
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.UseShellExecute <- false
                startInfo.CreateNoWindow <- true

                use rgdProcess = new Process()
                rgdProcess.StartInfo <- startInfo
                if not (rgdProcess.Start()) then
                    return Error "rgd rs did not start."
                else
                    do! rgdProcess.StandardInput.WriteAsync(markdown)
                    rgdProcess.StandardInput.Close()
                    let outputTask = rgdProcess.StandardOutput.ReadToEndAsync()
                    let errorTask = rgdProcess.StandardError.ReadToEndAsync()
                    do! rgdProcess.WaitForExitAsync(cancellationToken)
                    let! output = outputTask
                    let! error = errorTask
                    if rgdProcess.ExitCode <> 0 then
                        let details =
                            if String.IsNullOrWhiteSpace(error) then output.Trim()
                            else error.Trim()
                        return Error $"rgd rs failed: {details}"
                    else
                        return Ok()
            with ex ->
                return Error $"rgd rs failed: {ex.Message}"
        }

    let private readMacro() =
        if File.Exists(Files.macroPointerPath) then
            File.ReadAllText(Files.macroPointerPath, Encoding.UTF8).Trim() |> Some
        else
            None

    let private readRgdCommand slot =
        let path = Files.rgdCommandPath slot
        if File.Exists(path) then
            File.ReadAllText(path, Encoding.UTF8).Trim() |> Some
        else
            None

    let private isRgdCommand (command: string) =
        command.Equals("rgd", StringComparison.OrdinalIgnoreCase)
        || (command.Length > 3
            && command.StartsWith("rgd", StringComparison.OrdinalIgnoreCase)
            && Char.IsWhiteSpace(command[3]))

    let private runRgdAndReadClipboard command cancellationToken =
        task {
            try
                let script =
                    "$ErrorActionPreference = 'Continue'" + Environment.NewLine
                    + "& { " + command + " } | Out-Null" + Environment.NewLine
                    + "Get-Clipboard -Raw"

                let encoded =
                    script
                    |> Encoding.Unicode.GetBytes
                    |> Convert.ToBase64String

                let startInfo = ProcessStartInfo()
                startInfo.FileName <- "powershell"
                startInfo.ArgumentList.Add("-NoLogo")
                startInfo.ArgumentList.Add("-NoProfile")
                startInfo.ArgumentList.Add("-NonInteractive")
                startInfo.ArgumentList.Add("-EncodedCommand")
                startInfo.ArgumentList.Add(encoded)
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.UseShellExecute <- false
                startInfo.CreateNoWindow <- true

                use child = new Process()
                child.StartInfo <- startInfo
                if not (child.Start()) then
                    return Error "PowerShell did not start."
                else
                    let outputTask = child.StandardOutput.ReadToEndAsync()
                    let errorTask = child.StandardError.ReadToEndAsync()
                    do! child.WaitForExitAsync(cancellationToken)
                    let! clipboard = outputTask
                    let! error = errorTask
                    if child.ExitCode <> 0 then
                        return Error $"Stored rgd command failed: {error.Trim()}"
                    else
                        return Ok clipboard
            with ex ->
                return Error $"Stored rgd command failed: {ex.Message}"
        }

    let private invokeTasket (tasketRunUrl: string) (macro: string) (cancellationToken: CancellationToken) =
        task {
            let tasketBody =
                Json.serializeCompact
                    {| task = macro
                       delay = 1
                       loop = 1 |}

            use content = new StringContent(tasketBody, Encoding.UTF8, "application/json")
            try
                let! tasketResult = tasketClient.PostAsync(tasketRunUrl, content, cancellationToken)
                use response = tasketResult
                let! responseText = response.Content.ReadAsStringAsync(cancellationToken)
                if response.IsSuccessStatusCode then
                    return Ok(int response.StatusCode, responseText)
                else
                    return Error $"Tasket returned HTTP {int response.StatusCode}: {responseText}"
            with ex ->
                return Error ex.Message
        }

    let handleMacro (ctx: HttpContext) =
        task {
            match! readTextBody ctx with
            | Error error ->
                do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_macro" error
            | Ok value ->
                let macro = normalizeLiteral value
                if String.IsNullOrWhiteSpace(macro) then
                    do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_macro" "Macro filename is empty."
                else
                    Files.writeText Files.macroPointerPath (macro + Environment.NewLine)
                    do! (Responses.writeJson ctx StatusCodes.Status200OK
                        {| ok = true
                           macro = macro
                           state_file = Files.macroPointerPath |})
        } :> Task

    let handleRgd slot (ctx: HttpContext) =
        task {
            match! readTextBody ctx with
            | Error error ->
                do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_rgd_command" error
            | Ok value ->
                let command = value.Trim()
                if not (isRgdCommand command) then
                    do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_rgd_command" "Stored command must begin with rgd."
                else
                    let path = Files.rgdCommandPath slot
                    Files.writeText path (command + Environment.NewLine)
                    do! (Responses.writeJson ctx StatusCodes.Status200OK
                        {| ok = true
                           slot = slot
                           command = command
                           state_file = path |})
        } :> Task

    let handleZuko tasketRunUrl (ctx: HttpContext) =
        task {
            match readMacro() with
            | None ->
                do! Responses.writeError ctx StatusCodes.Status409Conflict "macro_not_set" "Set /v1/macro before calling /v1/zuko."
            | Some macro ->
                match! readTextBody ctx with
                | Error error ->
                    do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_zuko_text" error
                | Ok value ->
                    let markdown = normalizeMarkdown value
                    if String.IsNullOrWhiteSpace(markdown) then
                        do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_zuko_text" "Zuko text is empty."
                    else
                        match! writeRegistryWithRgd markdown ctx.RequestAborted with
                        | Error error ->
                            do! Responses.writeError ctx StatusCodes.Status500InternalServerError "regedited_write_failed" error
                        | Ok() ->
                            if macro.Equals("off", StringComparison.OrdinalIgnoreCase) then
                                do! (Responses.writeJson ctx StatusCodes.Status200OK
                                    {| ok = true
                                       macro = macro
                                       tempreg_file = tempregPath
                                       zone = "index:1:zone:1"
                                       tasket_skipped = true |})
                            else
                                match! invokeTasket tasketRunUrl macro ctx.RequestAborted with
                                | Ok(status, responseText) ->
                                    do! (Responses.writeJson ctx StatusCodes.Status200OK
                                        {| ok = true
                                           macro = macro
                                           tempreg_file = tempregPath
                                           zone = "index:1:zone:1"
                                           tasket_status = status
                                           tasket_response = responseText |})
                                | Error error ->
                                    do! Responses.writeError ctx StatusCodes.Status502BadGateway "tasket_failed" error
        } :> Task

    let handleIroh tasketRunUrl (ctx: HttpContext) =
        task {
            match readMacro() with
            | None ->
                do! Responses.writeError ctx StatusCodes.Status409Conflict "macro_not_set" "Set /v1/macro before calling /v1/iroh."
            | Some macro ->
                match! readTextBody ctx with
                | Error error ->
                    do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_iroh_slot" error
                | Ok value ->
                    let slot =
                        match normalizeLiteral value with
                        | "1" -> Some 1
                        | "2" -> Some 2
                        | "3" -> Some 3
                        | "4" -> Some 4
                        | "5" -> Some 5
                        | _ -> None

                    match slot with
                    | None ->
                        do! Responses.writeError ctx StatusCodes.Status400BadRequest "invalid_iroh_slot" "Iroh accepts only 1, 2, 3, 4, or 5."
                    | Some selected ->
                        match readRgdCommand selected with
                        | None ->
                            do! Responses.writeError ctx StatusCodes.Status409Conflict "rgd_command_not_set" $"Set /v1/rgd{selected} before calling this Iroh slot."
                        | Some command ->
                            match! runRgdAndReadClipboard command ctx.RequestAborted with
                            | Error error ->
                                do! Responses.writeError ctx StatusCodes.Status500InternalServerError "rgd_command_failed" error
                            | Ok clipboard ->
                                let markdown = normalizeMarkdown clipboard
                                if String.IsNullOrWhiteSpace(markdown) then
                                    do! Responses.writeError ctx StatusCodes.Status400BadRequest "empty_clipboard" "The current clipboard is empty."
                                else
                                    match! writeRegistryWithRgd markdown ctx.RequestAborted with
                                    | Error error ->
                                        do! Responses.writeError ctx StatusCodes.Status500InternalServerError "regedited_write_failed" error
                                    | Ok() ->
                                        if macro.Equals("off", StringComparison.OrdinalIgnoreCase) then
                                            do! (Responses.writeJson ctx StatusCodes.Status200OK
                                                {| ok = true
                                                   slot = selected
                                                   command = command
                                                   macro = macro
                                                   tempreg_file = tempregPath
                                                   zone = "index:1:zone:1"
                                                   tasket_skipped = true |})
                                        else
                                            match! invokeTasket tasketRunUrl macro ctx.RequestAborted with
                                            | Ok(status, responseText) ->
                                                do! (Responses.writeJson ctx StatusCodes.Status200OK
                                                    {| ok = true
                                                       slot = selected
                                                       command = command
                                                       macro = macro
                                                       tempreg_file = tempregPath
                                                       zone = "index:1:zone:1"
                                                       tasket_status = status
                                                       tasket_response = responseText |})
                                            | Error error ->
                                                do! Responses.writeError ctx StatusCodes.Status502BadGateway "tasket_failed" error
        } :> Task
