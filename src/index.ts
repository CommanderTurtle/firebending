import { spawn } from "node:child_process";
import { readFile, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, isAbsolute, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { McpServer } from "@modelcontextprotocol/server";
import { serveStdio } from "@modelcontextprotocol/server/stdio";
import { parse } from "jsonc-parser";
import * as z from "zod/v4";

type OptionalFeatures = {
  offMode?: {
    savedMessage: string;
    recoveryHint: string;
  };
  check?: {
    toolName: string;
    description: string;
    rgd1StateFile: string;
    waitingMessage: string;
    readyMessage: string;
  };
  recover?: {
    toolName: string;
    description: string;
    rgd2StateFile: string;
    readyMessage: string;
  };
};

type Config = {
  instanceId: string;
  server: { name: string; version: string };
  daemon: {
    baseUrl: string;
    expectedMacro: string;
    requestTimeoutSeconds: number;
    endpoints: { health: string; zuko: string };
  };
  ask: {
    toolName: string;
    description: string;
    confirmationPrompt: string;
    searchStartedMessage: string;
  };
  cooldownSeconds: number;
  stateFile: string;
  shell: {
    kind: "powershell" | "posix";
    program: string;
    arguments: string[];
    workingDirectory: string;
    timeoutSeconds: number;
    maxOutputCharacters: number;
  };
  unavailableMessage: string;
  features?: OptionalFeatures;
};

type State = {
  lastRunAt?: number;
  rgdBaseline?: string | null;
  lastExecutedRgd?: string;
};

type ShellResult = {
  exitCode: number;
  stdout: string;
  stderr: string;
};

const defaultConfigPath = fileURLToPath(new URL("../config.jsonc", import.meta.url));
const configPath = resolve(process.env.ASK_LARGER_MODEL_CONFIG || defaultConfigPath);
const configDirectory = dirname(configPath);

async function loadConfig(): Promise<Config> {
  let text: string;
  try {
    text = await readFile(configPath, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") {
      throw new Error(`Configuration not found at ${configPath}. Run: bun run setup`);
    }
    throw error;
  }

  const parsed = parse(text, [], { allowTrailingComma: true, disallowComments: false }) as Config;
  if (!parsed.instanceId || !parsed.server?.name || !parsed.ask?.toolName) {
    throw new Error("Configuration requires a generated instanceId, server.name, and ask.toolName");
  }
  if (!parsed.daemon?.baseUrl || !parsed.daemon?.endpoints?.health || !parsed.daemon?.endpoints?.zuko) {
    throw new Error("Configuration requires daemon.baseUrl and its health and zuko endpoint paths");
  }
  if (parsed.cooldownSeconds < 120) {
    throw new Error("cooldownSeconds cannot be lower than the hard 120-second minimum");
  }
  return parsed;
}

const config = await loadConfig();
const statePath = isAbsolute(config.stateFile)
  ? config.stateFile
  : resolve(configDirectory, config.stateFile);

function expandPath(value: string): string {
  const expanded = value
    .replace(/%([^%]+)%/g, (match, name: string) => process.env[name] ?? match)
    .replace(/\$\{([^}]+)\}/g, (match, name: string) => process.env[name] ?? match)
    .replace(/^~(?=[\\/])/, homedir());
  return isAbsolute(expanded) ? expanded : resolve(configDirectory, expanded);
}

function endpoint(path: string): string {
  const base = config.daemon.baseUrl.endsWith("/")
    ? config.daemon.baseUrl
    : `${config.daemon.baseUrl}/`;
  return new URL(path, base).toString();
}

async function request(path: string, init?: RequestInit): Promise<Response> {
  const controller = new AbortController();
  const timer = setTimeout(
    () => controller.abort(),
    config.daemon.requestTimeoutSeconds * 1000,
  );
  try {
    return await fetch(endpoint(path), { ...init, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}

async function daemonAvailable(): Promise<boolean> {
  try {
    return (await request(config.daemon.endpoints.health)).ok;
  } catch {
    return false;
  }
}

async function postZuko(text: string): Promise<void> {
  const response = await request(config.daemon.endpoints.zuko, {
    method: "POST",
    headers: { "content-type": "text/plain; charset=utf-8" },
    body: text,
  });
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}: ${await response.text()}`);
  }
}

async function readOptionalRgd(path: string): Promise<string | null> {
  try {
    return (await readFile(expandPath(path), "utf8")).trim();
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return null;
    throw error;
  }
}

async function loadState(): Promise<State> {
  try {
    return parse(await readFile(statePath, "utf8")) as State;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return {};
    throw error;
  }
}

async function saveState(state: State): Promise<void> {
  await writeFile(statePath, `${JSON.stringify(state, null, 2)}\n`, "utf8");
}

function isRgdCommand(command: string): boolean {
  return /^rgd(?:\s|$)/i.test(command);
}

function truncate(value: string): string {
  if (value.length <= config.shell.maxOutputCharacters) return value;
  return `${value.slice(0, config.shell.maxOutputCharacters)}\n[output truncated]`;
}

async function runStoredCommand(command: string): Promise<ShellResult> {
  const args = config.shell.kind === "powershell"
    ? [...config.shell.arguments, Buffer.from(command, "utf16le").toString("base64")]
    : [...config.shell.arguments, command];

  const cwd = config.shell.workingDirectory
    ? (isAbsolute(config.shell.workingDirectory)
      ? config.shell.workingDirectory
      : resolve(configDirectory, config.shell.workingDirectory))
    : process.cwd();

  return await new Promise<ShellResult>((resolveResult, reject) => {
    const child = spawn(config.shell.program, args, {
      cwd,
      env: process.env,
      shell: false,
      windowsHide: true,
    });
    const stdout: Buffer[] = [];
    const stderr: Buffer[] = [];
    let timedOut = false;

    child.stdout.on("data", (chunk: Buffer) => stdout.push(chunk));
    child.stderr.on("data", (chunk: Buffer) => stderr.push(chunk));
    child.once("error", reject);

    const timer = setTimeout(() => {
      timedOut = true;
      child.kill();
    }, config.shell.timeoutSeconds * 1000);

    child.once("close", (code) => {
      clearTimeout(timer);
      if (timedOut) {
        reject(new Error(`stored command exceeded ${config.shell.timeoutSeconds} seconds`));
        return;
      }
      resolveResult({
        exitCode: code ?? 1,
        stdout: truncate(Buffer.concat(stdout).toString("utf8")),
        stderr: truncate(Buffer.concat(stderr).toString("utf8")),
      });
    });
  });
}

function textResult(text: string, isError = false) {
  return {
    content: [{ type: "text" as const, text }],
    ...(isError ? { isError: true } : {}),
  };
}

function shellResult(prefix: string, result: ShellResult) {
  if (result.exitCode !== 0) {
    const details = [result.stdout, result.stderr].filter(Boolean).join("\n");
    return textResult(`stored rgd command failed with exit code ${result.exitCode}${details ? `\n${details}` : ""}`, true);
  }
  const output = result.stdout.trim() || result.stderr.trim() || "command completed with no shell output";
  return textResult(`${prefix}\n\n${output}`);
}

let submissionActive = false;

function createServer(): McpServer {
  const enabled = config.features ?? {};
  const optionalInstructions = [
    enabled.check ? `Use ${enabled.check.toolName} later to check the changed RGD1 result.` : "",
    enabled.recover ? `Use ${enabled.recover.toolName} at any time to recover the last Zuko temp file through RGD2.` : "",
  ].filter(Boolean).join(" ");

  const server = new McpServer(
    { name: config.server.name, version: config.server.version },
    {
      instructions:
        `Use ${config.ask.toolName} only for a deliberate request to a larger-model workflow. `
        + `The model must answer this question itself before submission: ${config.ask.confirmationPrompt} `
        + optionalInstructions,
    },
  );

  server.registerTool(
    config.ask.toolName,
    {
      description: config.ask.description,
      inputSchema: z.object({
        argument: z.string().min(1).describe("The complete text to submit unchanged to /v1/zuko."),
        confirm: z.enum(["Y", "N"]).describe(config.ask.confirmationPrompt),
      }),
    },
    async ({ argument, confirm }) => {
      if (confirm !== "Y") {
        return textResult(`cancelled: ${config.ask.confirmationPrompt}`);
      }
      if (!(await daemonAvailable())) {
        return textResult(config.unavailableMessage, true);
      }
      if (submissionActive) {
        return textResult("a larger-model submission is already in progress", true);
      }

      const state = await loadState();
      const now = Date.now();
      const cooldownMs = config.cooldownSeconds * 1000;
      if (state.lastRunAt && now - state.lastRunAt < cooldownMs) {
        const remaining = Math.ceil((cooldownMs - (now - state.lastRunAt)) / 1000);
        return textResult(`hard cooldown active; try again in ${remaining} seconds`, true);
      }

      submissionActive = true;
      try {
        if (enabled.check) {
          state.rgdBaseline = await readOptionalRgd(enabled.check.rgd1StateFile);
        }
        state.lastRunAt = now;
        await saveState(state);
        await postZuko(argument);

        if (enabled.offMode) {
          return textResult(`${enabled.offMode.savedMessage}; ${enabled.offMode.recoveryHint}`);
        }
        return textResult(config.ask.searchStartedMessage);
      } catch (error) {
        return textResult(`larger-model submission failed: ${(error as Error).message}`, true);
      } finally {
        submissionActive = false;
      }
    },
  );

  if (enabled.check) {
    const feature = enabled.check;
    server.registerTool(
      feature.toolName,
      {
        description: feature.description,
        inputSchema: z.object({}),
      },
      async () => {
        try {
          if (!(await daemonAvailable())) {
            return textResult(config.unavailableMessage, true);
          }
          const current = await readOptionalRgd(feature.rgd1StateFile);
          if (current === null) return textResult("RGD1 has no stored command yet");

          const state = await loadState();
          if (!("rgdBaseline" in state)) {
            state.rgdBaseline = current;
            await saveState(state);
            return textResult("RGD1 baseline recorded; no changed result is available yet");
          }
          if (current === state.rgdBaseline) return textResult(feature.waitingMessage);
          if (!isRgdCommand(current)) {
            return textResult("changed RGD1 value was rejected because it does not begin with rgd", true);
          }

          const result = await runStoredCommand(current);
          if (result.exitCode === 0) {
            state.rgdBaseline = current;
            state.lastExecutedRgd = current;
            await saveState(state);
          }
          return shellResult(feature.readyMessage, result);
        } catch (error) {
          return textResult(`larger-model result check failed: ${(error as Error).message}`, true);
        }
      },
    );
  }

  if (enabled.recover) {
    const feature = enabled.recover;
    server.registerTool(
      feature.toolName,
      {
        description: feature.description,
        inputSchema: z.object({}),
      },
      async () => {
        try {
          if (!(await daemonAvailable())) {
            return textResult(config.unavailableMessage, true);
          }
          const command = await readOptionalRgd(feature.rgd2StateFile);
          if (!command) return textResult("RGD2 has no stored recovery command", true);
          if (!isRgdCommand(command)) {
            return textResult("RGD2 recovery value was rejected because it does not begin with rgd", true);
          }
          return shellResult(feature.readyMessage, await runStoredCommand(command));
        } catch (error) {
          return textResult(`recovery failed: ${(error as Error).message}`, true);
        }
      },
    );
  }

  return server;
}

void serveStdio(createServer);
console.error(`${config.server.name} MCP server running on stdio`);
