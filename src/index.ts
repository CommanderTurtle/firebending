import { spawn } from "node:child_process";
import { readFile, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, isAbsolute, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { McpServer } from "@modelcontextprotocol/server";
import { serveStdio } from "@modelcontextprotocol/server/stdio";
import * as z from "zod/v4";

type Config = {
  server: { name: string; version: string };
  daemon: {
    baseUrl: string;
    macro: string;
    requestTimeoutSeconds: number;
    rgd1StateFile: string;
    endpoints: { health: string; zuko: string };
  };
  tools: {
    askName: string;
    checkName: string;
    askDescription: string;
    checkDescription: string;
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
  messages: {
    confirmationPrompt: string;
    searchStarted: string;
    savedToTemp: string;
    waiting: string;
    ready: string;
    unavailable: string;
  };
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

const defaultConfigPath = fileURLToPath(new URL("../config.json", import.meta.url));
const configPath = resolve(process.env.ASK_LARGER_MODEL_CONFIG || defaultConfigPath);
const configDirectory = dirname(configPath);

async function loadConfig(): Promise<Config> {
  const parsed = JSON.parse(await readFile(configPath, "utf8")) as Config;
  if (!parsed.daemon?.baseUrl || !parsed.daemon?.macro) {
    throw new Error("config.json requires daemon.baseUrl and daemon.macro");
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

const rgd1StatePath = expandPath(config.daemon.rgd1StateFile);

function endpoint(path: string): string {
  return new URL(path, config.daemon.baseUrl.endsWith("/") ? config.daemon.baseUrl : `${config.daemon.baseUrl}/`).toString();
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

async function postText(path: string, text: string): Promise<void> {
  const response = await request(path, {
    method: "POST",
    headers: { "content-type": "text/plain; charset=utf-8" },
    body: text,
  });
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}: ${await response.text()}`);
  }
}

async function readRgd1(): Promise<string | null> {
  try {
    return (await readFile(rgd1StatePath, "utf8")).trim();
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return null;
    throw error;
  }
}

async function daemonAvailable(): Promise<boolean> {
  try {
    const response = await request(config.daemon.endpoints.health);
    return response.ok;
  } catch {
    return false;
  }
}

async function loadState(): Promise<State> {
  try {
    return JSON.parse(await readFile(statePath, "utf8")) as State;
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
  let args: string[];
  if (config.shell.kind === "powershell") {
    const encoded = Buffer.from(command, "utf16le").toString("base64");
    args = [...config.shell.arguments, encoded];
  } else {
    args = [...config.shell.arguments, command];
  }

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
        reject(new Error(`stored RGD1 command exceeded ${config.shell.timeoutSeconds} seconds`));
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

let submissionActive = false;

function createServer(): McpServer {
  const server = new McpServer(
    { name: config.server.name, version: config.server.version },
    {
      instructions:
        `Use ${config.tools.askName} only for a deliberate request to a larger-model workflow. `
        + `The model must answer this question itself before submission: ${config.messages.confirmationPrompt} `
        + `Use ${config.tools.checkName} later to detect and retrieve a changed RGD1 result command.`,
    },
  );

  server.registerTool(
    config.tools.askName,
    {
      description: config.tools.askDescription,
      inputSchema: z.object({
        argument: z.string().min(1).describe("The complete text to submit unchanged to /v1/zuko."),
        confirm: z.enum(["Y", "N"]).describe(config.messages.confirmationPrompt),
      }),
    },
    async ({ argument, confirm }) => {
      if (confirm !== "Y") {
        return textResult(`cancelled: ${config.messages.confirmationPrompt}`);
      }
      if (!(await daemonAvailable())) {
        return textResult(config.messages.unavailable, true);
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
        const baseline = await readRgd1();
        state.lastRunAt = now;
        state.rgdBaseline = baseline;
        await saveState(state);

        await postText(config.daemon.endpoints.zuko, argument);

        return textResult(
          config.daemon.macro.toLowerCase() === "off"
            ? config.messages.savedToTemp
            : config.messages.searchStarted,
        );
      } catch (error) {
        return textResult(`larger-model submission failed: ${(error as Error).message}`, true);
      } finally {
        submissionActive = false;
      }
    },
  );

  server.registerTool(
    config.tools.checkName,
    {
      description: config.tools.checkDescription,
      inputSchema: z.object({}),
    },
    async () => {
      try {
        if (!(await daemonAvailable())) {
          return textResult(config.messages.unavailable, true);
        }
        const current = await readRgd1();
        if (current === null) {
          return textResult("RGD1 has no stored command yet");
        }

        const state = await loadState();
        if (!("rgdBaseline" in state)) {
          state.rgdBaseline = current;
          await saveState(state);
          return textResult("RGD1 baseline recorded; no changed result is available yet");
        }
        if (current === state.rgdBaseline) {
          return textResult(config.messages.waiting);
        }
        if (!isRgdCommand(current)) {
          return textResult("changed RGD1 value was rejected because it does not begin with rgd", true);
        }

        const result = await runStoredCommand(current);
        if (result.exitCode !== 0) {
          const details = [result.stdout, result.stderr].filter(Boolean).join("\n");
          return textResult(`changed RGD1 command failed with exit code ${result.exitCode}${details ? `\n${details}` : ""}`, true);
        }

        state.rgdBaseline = current;
        state.lastExecutedRgd = current;
        await saveState(state);
        const output = result.stdout.trim() || result.stderr.trim() || "command completed with no shell output";
        return textResult(`${config.messages.ready}\n\n${output}`);
      } catch (error) {
        return textResult(`larger-model result check failed: ${(error as Error).message}`, true);
      }
    },
  );

  return server;
}

void serveStdio(createServer);
console.error(`${config.server.name} MCP server running on stdio`);
