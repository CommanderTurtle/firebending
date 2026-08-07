# Ask a Larger Model MCP

A deliberately small stdio MCP template for handing one carefully prepared text argument to the OpenAI Buffer Daemon's Zuko route, then checking RGD1 for a result command.

It exposes only two model-facing tools:

- `ask_larger_model` asks for a `Y` or `N` confirmation, enforces a persistent two-minute minimum cooldown, and submits the argument unchanged to `/v1/zuko`.
- `check_larger_model` polls `/v1/rgd1`. When its stored command differs from the baseline captured at submission time, the tool runs that `rgd...` command once in the configured shell and returns its output.

The MCP neither changes nor owns `/v1/macro`. The external daemon must already be configured by its operator. The matching `daemon.macro` value in this repository only selects the MCP's status wording.

## Requirements

- Bun 1.2 or newer.
- The Zuko/Iroh OpenAI Buffer Daemon revision running independently. The MCP only checks its existing `GET /health` route and submits text to its existing `POST /v1/zuko` route.
- `rgd` available to the shell used by the MCP.
- PowerShell available as `powershell` with the default configuration. A POSIX shell can be configured instead.
- Tasket HTTPD only when `daemon.macro` is a `.scht` filename. It is not needed when the value is `off`.

## Install

```powershell
cd C:\path\to\ask-larger-model-mcp
bun install
```

Edit `config.json`, then start the stdio server:

```powershell
bun run start
```

An MCP host should launch it instead of keeping that command open manually. The portable registration shape is:

```json
{
  "mcpServers": {
    "ask-larger-model": {
      "command": "bun",
      "args": [
        "run",
        "C:\\absolute\\path\\to\\ask-larger-model-mcp\\src\\index.ts"
      ],
      "env": {
        "ASK_LARGER_MODEL_CONFIG": "C:\\absolute\\path\\to\\ask-larger-model-mcp\\config.json"
      }
    }
  }
}
```

Use the native MCP registration command or configuration screen supplied by the target harness and provide the same command, arguments, and environment variable.

## Configure

All deployment choices live in `config.json`:

| Setting | Purpose |
|---|---|
| `server` | MCP name and version. |
| `daemon.baseUrl` | OpenAI Buffer Daemon address. |
| `daemon.macro` | Informational mirror of the daemon's externally configured macro: a `.scht` filename or `off`. It controls only the returned status wording. |
| `daemon.requestTimeoutSeconds` | Timeout for each daemon HTTP call. |
| `daemon.rgd1StateFile` | Path to the daemon-owned persisted RGD1 string. `%NAME%`, `${NAME}`, and a leading `~` are expanded. |
| `daemon.endpoints` | Existing daemon health and Zuko paths. |
| `tools` | Tool names and model-facing explanations. |
| `cooldownSeconds` | Submission cooldown. Values below 120 are rejected. |
| `stateFile` | Persistent cooldown and RGD1 baseline state. Relative paths are resolved beside the config file. |
| `shell` | Completion-command shell, arguments, directory, timeout, and output cap. |
| `messages` | Confirmation, started, saved, waiting, and ready wording. |

The default is safe for a file-only handoff:

```json
"macro": "off"
```

When the external daemon is already set to this mode, the ask tool returns `saved to temp file`; Zuko still creates `%TEMP%\tempreg.txt`, but Tasket is skipped. When the daemon operator selects a `.scht` macro, mirror that filename here and restart the MCP server so a submitted request returns `search has begun`. The MCP never posts either value to `/v1/macro`.

For Bash or another POSIX-compatible shell:

```json
"shell": {
  "kind": "posix",
  "program": "bash",
  "arguments": ["-lc"],
  "workingDirectory": "",
  "timeoutSeconds": 30,
  "maxOutputCharacters": 120000
}
```

## Model workflow

1. The model prepares the complete larger-model argument.
2. It explicitly evaluates: `Are you sure your search is correct? Run now?`
3. It calls `ask_larger_model` with the argument and `confirm: "Y"`. Passing `N` cancels without consuming the cooldown.
4. The MCP confirms that the independent daemon is healthy. If it is not reachable, the registered tool remains active and returns `this tool is unavailable at the moment`.
5. A confirmed attempt records the daemon-owned RGD1 state-file baseline and consumes the hard cooldown before contacting Zuko. Restarting the MCP does not reset it.
6. Later, the model calls `check_larger_model`.
7. If RGD1 is unchanged, the tool reports that the result is not ready.
8. If RGD1 changed, the tool verifies that it begins with `rgd`, executes it once, returns its shell output, and records it as the new baseline.

The downstream Tasket macro may update RGD1 with any valid command such as an `rgd rg ...` read without `--clip`. The MCP does not invent or rewrite that command.

## Boundaries

- Zuko arguments are sent as plaintext and are never executed by this MCP.
- Only the changed RGD1 completion value reaches the configured shell.
- An RGD1 value not beginning with `rgd` is rejected.
- The MCP cannot mutate the daemon's macro or RGD slots and adds no daemon routes.
- The MCP remains discoverable when the daemon is offline; calls return the configured unavailable message.
- There is no background daemon, scheduler, or unsolicited polling. `check_larger_model` performs the explicit status poll.
- Runtime state is stored in `.ask-larger-model-state.json` and excluded from version control.
