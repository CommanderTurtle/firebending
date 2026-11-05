#!/usr/bin/env bun
// Register firebending with a client, or take it back out.
//
// The config file is the contract; where a client has a native command for the same thing, that is
// run too, and every write is atomic and backed up first. Shape borrowed from localflame (AGPL-3.0).
import { execFileSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import {
  chmodSync, copyFileSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync,
} from "node:fs";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const SERVER_NAME = "firebending";
const serverEntry = path.join(REPO_ROOT, "bin", "firebending.js");
const home = os.homedir();
const ompHome = path.resolve(process.env.OMP_HOME || path.join(home, ".omp", "agent"));
const backupRoot = path.join(home, ".local", "state", "firebending-mcp", "backups");
let daemonUrl = process.env.FIREBENDING_DAEMON_URL || "http://127.0.0.1:5199";

function usage() {
  return `Usage: bun scripts/configure.mjs [install|uninstall|status] [--target omp] [--dry-run] [--daemon-url <url>] [--command <bun>]

  install     register ${SERVER_NAME} as a stdio MCP server (default)
  uninstall   remove just the ${SERVER_NAME} entry
  status      show what each target currently has
  --dry-run   print what would change, touch nothing
`;
}

const argv = process.argv.slice(2);
const options = { action: "install", targets: ["omp"], dryRun: false };
let command = process.execPath;
for (let index = 0; index < argv.length; index += 1) {
  const arg = argv[index];
  if (arg === "--dry-run" || arg === "-n") options.dryRun = true;
  else if (arg === "--target" || arg === "-t") options.targets = String(argv[++index] || "omp").split(",").filter(Boolean);
  else if (arg === "--daemon-url") daemonUrl = String(argv[++index] || daemonUrl);
  else if (arg === "--command") command = String(argv[++index] || command);
  else if (["install", "uninstall", "status"].includes(arg)) options.action = arg;
  else if (["-h", "--help"].includes(arg)) {
    process.stdout.write(usage());
    process.exit(0);
  } else {
    process.stderr.write(`unknown argument: ${arg}\n${usage()}`);
    process.exit(2);
  }
}

let backupStamp = "";

function log(message) {
  process.stderr.write(`${options.dryRun ? "[dry-run] " : ""}${message}\n`);
}

function ensureDirectory(dir) {
  if (!options.dryRun) mkdirSync(dir, { recursive: true, mode: 0o700 });
}

function backup(file) {
  if (!existsSync(file) || options.dryRun) return;
  if (!backupStamp) backupStamp = new Date().toISOString().replace(/[:.]/g, "-");
  const relative = file.replace(/^[/\\]+/, "").replace(/:/g, "");
  const destination = path.join(backupRoot, backupStamp, relative);
  mkdirSync(path.dirname(destination), { recursive: true, mode: 0o700 });
  copyFileSync(file, destination);
}

function atomicWrite(file, content, mode = 0o600) {
  const normalized = content.endsWith("\n") ? content : `${content}\n`;
  const existing = existsSync(file) ? readFileSync(file, "utf8") : null;
  if (existing === normalized) {
    log(`unchanged ${file}`);
    return false;
  }
  log(`${existing === null ? "create" : "update"} ${file}`);
  if (options.dryRun) return true;
  ensureDirectory(path.dirname(file));
  backup(file);
  const temporary = `${file}.firebending-${process.pid}-${randomBytes(4).toString("hex")}`;
  writeFileSync(temporary, normalized, { mode });
  renameSync(temporary, file);
  chmodSync(file, mode);
  return true;
}

function readJson(file, fallback = {}) {
  if (!existsSync(file)) return structuredClone(fallback);
  try { return JSON.parse(readFileSync(file, "utf8")); }
  catch (cause) { throw new Error(`Cannot parse JSON ${file}: ${cause.message}`); }
}

const TARGETS = {
  omp: {
    file: path.join(ompHome, "mcp.json"),
    entry() {
      const settings = readJson(path.join(ompHome, "config.yml"), {});
      return Boolean(settings.mcp?.enabled ?? true);
    },
  },
};

function serverDefinition() {
  return {
    type: "stdio",
    command,
    args: [serverEntry],
    cwd: REPO_ROOT,
    env: { FIREBENDING_DAEMON_URL: daemonUrl },
  };
}

function describe(target, file) {
  const document = readJson(file, {});
  const entry = document.mcpServers?.[SERVER_NAME];
  if (!entry) return `${target}: not registered in ${file}`;
  return `${target}: ${entry.command} ${entry.args?.join(" ")} (cwd ${entry.cwd}) in ${file}`;
}

function apply(target) {
  const profile = TARGETS[target];
  if (!profile) throw new Error(`Unknown target ${JSON.stringify(target)}; try: ${Object.keys(TARGETS).join(", ")}`);
  const file = profile.file;
  if (options.action === "status") {
    process.stdout.write(`${describe(target, file)}\n`);
    return;
  }
  const document = readJson(file, {});
  document.mcpServers = document.mcpServers ?? {};
  if (options.action === "uninstall") {
    if (!document.mcpServers[SERVER_NAME]) {
      log(`${SERVER_NAME} is not in ${file}`);
      return;
    }
    delete document.mcpServers[SERVER_NAME];
    atomicWrite(file, JSON.stringify(document, null, 2));
    return;
  }
  if (!existsSync(serverEntry)) throw new Error(`The server entry is missing: ${serverEntry}`);
  if (!profile.entry()) log(`${target} has MCP switched off in its settings; the entry is written anyway`);
  document.mcpServers[SERVER_NAME] = serverDefinition();
  atomicWrite(file, JSON.stringify(document, null, 2));
  if (!options.dryRun) {
    log(`restart ${target} to load the tools`);
  }
}

if (options.action !== "status" && !options.dryRun) ensureDirectory(path.dirname(backupRoot));
for (const target of options.targets) apply(target);
if (options.action === "install" && !options.dryRun) {
  process.stdout.write(`firebending is registered for: ${options.targets.join(", ")}\n`);
  process.stdout.write(`the picture daemon should answer at ${daemonUrl}\n`);
  process.stdout.write("restart the client to load the tools\n");
}
