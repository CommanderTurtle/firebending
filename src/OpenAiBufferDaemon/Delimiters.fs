namespace OpenAiBufferDaemon

open System

module Delimiters =
    /// Prefer complete marker lines, so a casual mention is not a boundary.
    /// Fall back to an inline literal pair. No regex, trimming, or table parsing.
    let extract (marker: string) (text: string) =
        let value = text.Replace("\r\n", "\n").Replace("\r", "\n")
        // A manually piped /v1/format response may include our disclosure.
        // Only its answer, not an example marker in reasoning, is actionable.
        let trimmed = value.TrimStart()
        let value =
            if trimmed.StartsWith("<details>\n<summary>Reasoning</summary>", StringComparison.OrdinalIgnoreCase) then
                let finish = trimmed.IndexOf("</details>", StringComparison.OrdinalIgnoreCase)
                if finish >= 0 then trimmed.Substring(finish + 10) else value
            else value
        let lines = value.Split('\n')
        let positions = lines |> Array.indexed |> Array.choose (fun (i, line) -> if line.Trim() = marker then Some i else None)
        if positions.Length >= 2 then
            Some (lines[positions[0] + 1 .. positions[1] - 1] |> String.concat "\n")
        else
            let first = value.IndexOf(marker, StringComparison.Ordinal)
            if first < 0 then None
            else
                let start = first + marker.Length
                let finish = value.IndexOf(marker, start, StringComparison.Ordinal)
                if finish < 0 then None
                else
                    let mutable content = value.Substring(start, finish - start)
                    if content.StartsWith('\n') then content <- content.Substring(1)
                    if content.EndsWith('\n') then content <- content.Substring(0, content.Length - 1)
                    Some content
