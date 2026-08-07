# Ask a Larger Model MCP

A minimal, configurable stdio MCP template for sending one confirmed text argument to an independently running OpenAI Buffer Daemon through `/v1/zuko`.

The base installation exposes one tool:

- `ask_larger_model` asks the model to confirm its request, enforces a persistent two-minute minimum cooldown, and submits the text unchanged to Zuko.

Two additional tools and file-only `off` behavior are present as commented JSONC features. A feature does not exist in the MCP until its block is uncommented:

- `check_larger_model` watches the daemon-owned RGD1 string for a changed result command, runs that `rgd...` command once, and returns its output.
- `recover` runs the daemon-owned RGD2 command whenever called. RGD2 is expected to have been configured externally to read `%TEMP%\tempreg.txt` at `i1z1`.
- `offMode` changes the successful ask response to `saved to temp file; call at any time with 'recover'`.

The MCP does not configure, extend, start, stop, or own the daemon. It never writes `/v1/macro`, `/v1/rgd1`, or `/v1/rgd2`.

## Requirements

- Bun 1.2 or newer.
- The Zuko/Iroh OpenAI Buffer Daemon running independently.
- `rgd` and the configured shell available on `PATH`.
- Tasket only when the external daemon is configured with a real `.scht` macro.

The MCP process starts and remains discoverable without the daemon. If the daemon's existing `/health` route cannot be reached, every installed tool returns:

```text
this tool is unavailable at the moment
```

## Install an instance

```powershell
cd C:\path\to\ask-larger-model-mcp
bun install
bun run setup
```

`setup` creates `config.jsonc` once. It refuses to overwrite an existing configuration and generates a random eight-character instance ID used by:

- The MCP server name.
- The persistent cooldown/state filename.
- The suggested MCP registration name printed after setup.

Edit the generated `config.jsonc`. Optional feature blocks are ordinary JSONC comments: remove the leading `//` lines only for the features wanted.

## Install multiple variations

One source checkout can create any number of independent configurations:

```powershell
bun run setup -- configs/research.jsonc
bun run setup -- configs/code-review.jsonc
bun run setup -- configs/offline-recovery.jsonc
```

Each receives a different server identity and state file. Register each generated config under the unique registration name printed by setup. The tool names can remain the readable `ask_larger_model`, `check_larger_model`, and `recover` because their MCP server identities do not overlap.

The portable registration shape is:

```json
{
  "mcpServers": {
    "ask-larger-model-UNIQUE_ID": {
      "command": "bun",
      "args": [
        "run",
        "C:\\absolute\\path\\to\\ask-larger-model-mcp\\src\\index.ts"
      ],
      "env": {
        "ASK_LARGER_MODEL_CONFIG": "C:\\absolute\\path\\to\\ask-larger-model-mcp\\configs\\research.jsonc"
      }
    }
  }
}
```

Use the target harness's native MCP registration command or UI with the same command, arguments, and environment value.

## Base configuration

The generated file keeps all deployment choices portable:

| Setting | Purpose |
|---|---|
| `instanceId` | Random identity generated once by setup. |
| `server` | Unique MCP server name and version. |
| `daemon.baseUrl` | Address of the independently running daemon. |
| `daemon.expectedMacro` | Informational mirror of the macro selected outside this MCP. It is never posted anywhere. |
| `daemon.requestTimeoutSeconds` | Timeout for daemon health and Zuko requests. |
| `daemon.endpoints` | Existing `/health` and `/v1/zuko` paths. |
| `ask` | Base tool name, description, confirmation prompt, and started message. |
| `cooldownSeconds` | Persistent submission cooldown; values below 120 are rejected. |
| `stateFile` | Instance-specific cooldown and completion baseline. |
| `shell` | Program, arguments, working directory, timeout, and output limit used by optional RGD tools. |
| `unavailableMessage` | Text returned while the external daemon cannot be reached. |

## Optional off mode

First configure the external daemon's `/v1/macro` value as `off` yourself. Then uncomment only the `features.offMode` block:

```jsonc
"offMode": {
  "savedMessage": "saved to temp file",
  "recoveryHint": "call at any time with 'recover'"
},
```

This changes only the MCP's response wording. Zuko writes `%TEMP%\tempreg.txt` as usual, and the independently configured daemon skips Tasket.

## Optional completion check

Uncomment `features.check` to install `check_larger_model`. Point `rgd1StateFile` at the persisted RGD1 string owned by the daemon:

```jsonc
"check": {
  "toolName": "check_larger_model",
  "description": "Check whether the daemon-owned RGD1 command changed after the larger-model request. If changed, run that rgd command once and return its output.",
  "rgd1StateFile": "%USERPROFILE%\\Desktop\\openai-buffer-daemon-rgd1.txt",
  "waitingMessage": "the larger-model result is not ready; RGD1 has not changed",
  "readyMessage": "the larger-model result is ready"
},
```

When an ask begins, the current RGD1 value becomes the baseline. A later check executes a changed value once, only when it begins with `rgd`, and records it as the new baseline.

## Optional recovery

Configure RGD2 externally with a native command that reads the temp registry, for example:

```powershell
rgd rg $env:TEMP\tempreg.txt i1z1
```

Then uncomment `features.recover`:

```jsonc
"recover": {
  "toolName": "recover",
  "description": "Recover the most recently saved Zuko text by running the externally preconfigured RGD2 command and returning its output.",
  "rgd2StateFile": "%USERPROFILE%\\Desktop\\openai-buffer-daemon-rgd2.txt",
  "readyMessage": "recovered the last saved temp file"
}
```

Calling `recover` reads that externally persisted RGD2 string, requires it to begin with `rgd`, runs it in the configured shell, and returns the output. It can be called repeatedly and does not consume the Zuko cooldown.

## Workflow

1. The model prepares the complete larger-model argument.
2. It evaluates `Are you sure your search is correct? Run now?`
3. It passes `confirm: "Y"` to `ask_larger_model`. `N` cancels without consuming the cooldown.
4. The MCP checks the independent daemon, records its cooldown, and submits only to `/v1/zuko`.
5. If enabled, `check_larger_model` later retrieves a changed RGD1 result.
6. If enabled, `recover` retrieves the most recently saved Zuko text through the preconfigured RGD2 command at any time.

## Boundaries

- Submitted Zuko text is never executed locally.
- Only externally stored RGD1/RGD2 values beginning with `rgd` can reach the configured shell.
- RGD1 is change-detected and executed once per changed value; RGD2 recovery is deliberately repeatable.
- No optional tool is registered while its JSONC block remains commented.
- The MCP adds no HTTP routes and changes no daemon state.
- Runtime state and generated configurations are excluded from version control.
