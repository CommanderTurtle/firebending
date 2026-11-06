#!/usr/bin/env bun
// firebending: look for named things in a picture, then cut them out and show them.
//
// The daemon does the measuring and answers in prose written for a person. This process adds what
// a conversation needs: a remembered last answer so a cut-out can be asked for by title or number,
// the picture itself rendered back as an image, and lossless sectioned reports for the long answers.
import { randomUUID } from "node:crypto";
import { spawn } from "node:child_process";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { McpServer } from "@modelcontextprotocol/server";
import { serveStdio } from "@modelcontextprotocol/server/stdio";
import { z } from "zod/v4";
import { Daemon, DaemonError } from "../src/daemon.js";
import {
  ResourceStore,
  createResource,
  findInResource,
  readResource,
  resourceOutline,
  resourceSummary,
} from "../src/store.js";

const VERSION = "1.0.0";
const EDGES = ["top", "bottom", "left", "right"];
const MIME = { png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg", webp: "image/webp", gif: "image/gif", bmp: "image/bmp" };
const session = process.env.FIREBENDING_SESSION || randomUUID().slice(0, 12);
const daemon = new Daemon();
const store = new ResourceStore({ maxResources: 24 });
// What the last look found, so "crop the llama logo" and "crop 2" mean the same thing to the model.
let last = null;

function envPositiveInt(name, fallback) {
  const value = Number(process.env[name]);
  return Number.isInteger(value) && value > 0 ? value : fallback;
}

// A picture in a reply is worth a lot of context; the newest few are enough and the rest stay on disk.
const MAX_IMAGES = envPositiveInt("FIREBENDING_MAX_IMAGES", 2);
// One agreed list can be run over a folder of pictures in one call; a call stays inside one hold, so
// the folder is taken in name order up to this many pictures and the rest are asked for again.
const MAX_PICTURES = envPositiveInt("FIREBENDING_MAX_PICTURES", 24);
const PICTURE_SUFFIXES = new Set([".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff"]);

// The pointer probe, word for word. It is a contract with the vision model rather than a sentence to
// reword per call, so it is written once here and sent unchanged - over the ordinary picture route,
// which is all this needs: the machine already asks models about pictures, and nothing here is a
// new kind of endpoint.
//
// It asks for the box of coordinates rather than for the cursor, because that box is boxed text:
// bright digits on a plate, four real edges, which is what a vision model can localize. A 2-pixel grey
// crosshair on near-black is what it confabulates - measured, three passes running, at 143 px off.
// The overlay draws that tag beside the cursor whose numbers it prints, below and to the right of it,
// so the tag's top-left corner is the point nearest the cursor, and that corner becomes the pointer.
const MOUSE_QUESTION = "Where is the box of coordinates? If it exists, it's beside either a crosshair or mouse";
const mousePlan = (instructions) => ({ objects: [{ name: "mouse", kind: "where", instructions }] });

/// From the middle of one box to the middle of another, in picture pixels: i across, j downward.
function offsetFrom(from, to, width, height) {
  if (!Number.isFinite(width) || !Number.isFinite(height)) return "unknown distance";
  const across = Math.round(((to.left + to.right) / 2 - (from.left + from.right) / 2) * width);
  const down = Math.round(((to.top + to.bottom) / 2 - (from.top + from.bottom) / 2) * height);
  const i = across < 0 ? `-${Math.abs(across)}` : `${across}`;
  const j = down < 0 ? `-${Math.abs(down)}` : `+${down}`;
  return `${i}i${j}j`;
}

function images(files) {
  return (files || []).slice(0, MAX_IMAGES).map(imageBlock).filter(Boolean);
}

function payloadResult(value, files = []) {
  return {
    content: [{ type: "text", text: JSON.stringify(value) }, ...images(files)],
    structuredContent: value,
  };
}

function proseResult(text, value, files = []) {
  return { content: [{ type: "text", text }, ...images(files)], structuredContent: value };
}

function errorResult(error) {
  const value = {
    ok: false,
    error: error instanceof Error ? error.message : String(error),
    ...(error instanceof DaemonError && error.status !== null ? { status: error.status } : {}),
    ...(error instanceof DaemonError && error.fix ? { fix: error.fix } : {}),
  };
  return { isError: true, content: [{ type: "text", text: JSON.stringify(value) }], structuredContent: value };
}

function guarded(handler) {
  return async (args, context) => {
    try {
      return await handler(args, context);
    } catch (error) {
      return errorResult(error);
    }
  };
}

function imageBlock(file) {
  if (!file || !existsSync(file)) return null;
  const bytes = readFileSync(file);
  const kind = MIME[(path.extname(file).slice(1) || "png").toLowerCase()] || "image/png";
  return { type: "image", data: Buffer.from(bytes).toString("base64"), mimeType: kind };
}

function resource(store, id) {
  const text = String(id || "").trim();
  if (!text) {
    const newest = store.list()[0];
    if (!newest) throw new Error("No report has been stored by this process yet; run a look first.");
    return newest;
  }
  const found = store.get(text);
  if (!found) throw new Error(`Report ${JSON.stringify(text)} is not available in this process; reports live only as long as this server runs.`);
  return found;
}

function at(value, digits = 2) {
  return typeof value === "number" && Number.isFinite(value) ? value.toFixed(digits) : "unknown";
}

function objectLine(object) {
  if (object.error) return `${object.index}. ${object.name} - not found: ${object.error}`;
  const region = EDGES.map((edge) => `${edge} ${at(object.box?.[edge])}`).join(", ");
  if (typeof object.presence === "number") {
    return `${object.index}. ${object.name} - ${at(object.presence * 100, 0)}% likely to be there, about the region where ${region}`;
  }
  return `${object.index}. ${object.name} - found at ${region}; weakest edge ${at(object.weakest_edge, 3)}, agreement ${at(object.confidence, 3)}`;
}

/// One picture, or every picture in a folder, in name order. Classifying a group of pictures is the
/// obvious route for "does X appear in these", so a folder is accepted where a picture is asked for
/// rather than making the caller list files by hand.
function picturesOf(target, wanted) {
  const entries = wanted?.length ? wanted : [target];
  const found = [];
  for (const entry of entries.filter(Boolean)) {
    if (!existsSync(entry)) {
      throw new Error(`There is nothing at ${JSON.stringify(entry)}: pass a picture file, a folder of them, or an absolute path.`);
    }
    if (statSync(entry).isDirectory()) {
      const inside = readdirSync(entry)
        .filter((name) => PICTURE_SUFFIXES.has(path.extname(name).toLowerCase()))
        .sort()
        .map((name) => path.join(entry, name));
      if (!inside.length) {
        throw new Error(`${JSON.stringify(entry)} holds no pictures: send a folder of ${[...PICTURE_SUFFIXES].join("/")} files, or name one file.`);
      }
      found.push(...inside);
    } else {
      found.push(entry);
    }
  }
  const unique = [...new Set(found)];
  if (!unique.length) throw new Error("No picture was named: pass image, or images with a list of files or folders.");
  return { pictures: unique.slice(0, MAX_PICTURES), skipped: Math.max(0, unique.length - MAX_PICTURES) };
}

/// Pixel size straight out of a picture's header - a PNG's IHDR, a JPEG's start-of-frame. Fractions
/// only become distances once the picture they were measured over has a size, and a look that skipped
/// the drawing never learned one from the daemon.
function headerSize(path) {
  try {
    const head = readFileSync(path);
    if (head.length >= 24 && head[0] === 0x89 && head[1] === 0x50) {
      return { width: head.readUInt32BE(16), height: head.readUInt32BE(20) };
    }
    if (head.length >= 4 && head[0] === 0xff && head[1] === 0xd8) {
      for (let at = 2; at + 9 < head.length; ) {
        if (head[at] !== 0xff) {
          at += 1;
        } else if (head[at + 1] >= 0xc0 && head[at + 1] <= 0xcf && ![0xc4, 0xc8, 0xcc].includes(head[at + 1])) {
          return { width: head.readUInt16BE(at + 7), height: head.readUInt16BE(at + 5) };
        } else {
          at += 2 + head.readUInt16BE(at + 2);
        }
      }
    }
  } catch {
    // An unreadable picture has no size, and the caller says so in its own words.
  }
  return null;
}

/// The Macrohelp overlay speaks MCP like anything else does, so that is how a measured vector gets
/// moved: this process is a plain client of that MCP. One tool, `control`, takes one shape - open a
/// menu, then the run id it answers with plus the inputs for that menu - and a move is a menu and two
/// inputs. Spawned lazily, so a conversation that never touches the pointer never starts it.
class Overlay {
  #child = null;
  #ready = null;
  #pending = new Map();
  #buffer = "";
  #id = 0;
  #tool = null;

  #send(method, params, reply = true) {
    return new Promise((resolve, reject) => {
      const id = ++this.#id;
      if (reply) this.#pending.set(id, { resolve, reject });
      const body = reply ? { jsonrpc: "2.0", id, method, params } : { jsonrpc: "2.0", method, params };
      this.#child.stdin.write(`${JSON.stringify(body)}\n`);
    });
  }

  #start() {
    if (this.#ready) return this.#ready;
    const command = process.env.MACROHELP_MCP_COMMAND || "macrohelpctl";
    const args = (process.env.MACROHELP_MCP_ARGS || "mcp").split(" ").filter(Boolean);
    const child = spawn(command, args, { stdio: ["pipe", "pipe", "inherit"] });
    this.#child = child;
    const refuse = (reason) => {
      this.#child = null;
      this.#ready = null;
      this.#tool = null;
      for (const { reject } of this.#pending.values()) reject(new Error(reason));
      this.#pending.clear();
    };
    child.on("error", (failure) => refuse(
      failure.code === "ENOENT"
        ? `no Macrohelp MCP at ${JSON.stringify(command)} - set MACROHELP_MCP_COMMAND and MACROHELP_MCP_ARGS to how this client starts it`
        : String(failure?.message || failure),
    ));
    child.on("close", () => refuse("the Macrohelp MCP closed"));
    child.stdout.on("data", (chunk) => {
      this.#buffer += chunk;
      for (;;) {
        const at = this.#buffer.indexOf("\n");
        if (at < 0) break;
        const line = this.#buffer.slice(0, at).trim();
        this.#buffer = this.#buffer.slice(at + 1);
        if (!line) continue;
        let message = null;
        try {
          message = JSON.parse(line);
        } catch {
          continue;
        }
        const waiter = message?.id === undefined ? null : this.#pending.get(message.id);
        if (!waiter) continue;
        this.#pending.delete(message.id);
        if (message.error) waiter.reject(new Error(JSON.stringify(message.error)));
        else waiter.resolve(message.result);
      }
    });
    this.#ready = (async () => {
      await this.#send("initialize", {
        protocolVersion: "2025-06-18",
        capabilities: {},
        clientInfo: { name: "firebending", version: VERSION },
      });
      this.#send("notifications/initialized", {}, false);
      // Ask what it actually registers rather than guessing the name: the overlay answers
      // "Unknown tool" and makes the caller read the list itself.
      const listed = await this.#send("tools/list", {});
      const names = (listed?.tools || []).map((tool) => tool.name);
      this.#tool = names.find((name) => name === "control")
        || names.find((name) => name.endsWith("_control"))
        || names.find((name) => (listed.tools.find((tool) => tool.name === name)?.description || "").includes("menu"));
      if (!this.#tool) {
        throw new Error(`the Macrohelp MCP offers no control tool - it lists ${names.join(", ") || "nothing"}`);
      }
      return this.#send.bind(this);
    })().catch((failure) => {
      refuse(String(failure?.message || failure));
      try {
        child.kill();
      } catch {
        // Already gone.
      }
      throw failure;
    });
    return this.#ready;
  }

  /// One call to the overlay's control tool, answered as the JSON the overlay itself writes.
  async control(request) {
    const send = await this.#start();
    const result = await send("tools/call", { name: this.#tool, arguments: request });
    const text = (result?.content || []).map((part) => part.text || "").join("");
    if (result?.isError) throw new Error(text || "the overlay refused the request");
    try {
      return JSON.parse(text);
    } catch {
      return { raw: text };
    }
  }

  stop() {
    try {
      this.#child?.kill();
    } catch {
      // Already gone.
    }
    this.#child = null;
    this.#ready = null;
  }
}

const overlay = new Overlay();
process.on("exit", () => overlay.stop());

function reportText(answer) {
  const lines = [`## What was found in the picture: ${answer.answered} of ${answer.asked}`, ""];
  for (const object of answer.objects) lines.push(objectLine(object));
  lines.push("");
  if (answer.annotated) {
    lines.push(`The picture with what was found drawn on it: ${answer.annotated.path} (${answer.annotated.width}x${answer.annotated.height})`);
  } else if (answer.picture?.path) {
    lines.push(`The picture: ${answer.picture.path}`);
    if (answer.answered > 0) {
      // The gate is asked, not assumed: drawing costs one drawing call, and the boxes it paints are
      // the ones already held, so a yes can never move a measurement.
      lines.push(`Preview (yes/no): say yes and call preview - it draws these very boxes over ${answer.picture.path} without measuring anything again.`);
    }
  }
  for (const crop of answer.crops || []) {
    lines.push(`Cut-out ${crop.name || ""}: ${crop.path} (${crop.width}x${crop.height})`);
  }
  lines.push("");
  lines.push("Coordinates are fractions of the picture: 0 is its top-left corner, 1 its bottom-right. The agreement numbers describe how certain the model was about its own spread, not how correct it is.");
  return lines.join("\n");
}

/// The plan reply the daemon writes for a terminal: keep the list and the next step, drop the JSON.
function planText(text) {
  return text.replace(/```json[\s\S]*?```/g, "").replace(/\n{3,}/g, "\n\n").trim();
}

function resolveObject(wanted) {
  if (!last?.objects?.length) {
    throw new Error(`Nothing has been looked for yet in this process${wanted ? `; ${JSON.stringify(wanted)} cannot be resolved` : ""}. Run plan with confirm first, or pass a region.`);
  }
  const needle = String(wanted).trim().toLowerCase();
  const byNumber = Number(needle);
  const found = last.objects.find((object) => (
    (Number.isInteger(byNumber) && object.index === byNumber)
    || object.name.toLowerCase() === needle
    || object.name.toLowerCase().includes(needle)
    || needle.includes(object.name.toLowerCase())
  ));
  if (!found) {
    throw new Error(`${JSON.stringify(wanted)} is not one of what was found: ${last.objects.map((object) => `${object.index}: ${object.name}${object.error ? " (not found)" : ""}`).join("; ")}.`);
  }
  if (found.error) throw new Error(`${found.index}: ${found.name} was not found (${found.error}), so there is nothing to cut out for it.`);
  return found;
}

/// The daemon wraps JSON replies in a fence; take the last block, which is the payload.
function jsonFrom(text) {
  const blocks = [...String(text).matchAll(/```json\s*([\s\S]*?)```/g)].map((block) => block[1]);
  const candidate = (blocks.at(-1) || text || "").trim();
  if (!candidate.startsWith("{") && !candidate.startsWith("[")) return null;
  try { return JSON.parse(candidate); } catch { return null; }
}

/// The framework the daemon holds, read back rather than scraped out of prose. Right after a draft
/// the fresh questions are the draft, not whatever is still active from an earlier agreement.
async function frameworkOf(context, prefer = "active") {
  const status = jsonFrom((await daemon.judgeStatus(session, context.signal)).text()) || {};
  const framework = (prefer === "draft" ? status.draft || status.active : status.active || status.draft) || null;
  const entries = Object.entries(framework?.questions || {});
  return { framework, entries, levels: new Map(entries), turns: status.turns ?? 0 };
}

/// Agree to the draft. A 409 here always means the same thing, so say what to do about it.
async function activateJev(context, hint) {
  try {
    await daemon.tellJev(session, "Y", context.signal);
  } catch (error) {
    if (error instanceof DaemonError && error.status === 409) error.fix = hint;
    throw error;
  }
}

/// What the questions will ask, in the words a person chooses from.
function questionLines(entries) {
  return entries.map(([id, question], index) => {
    if (question.type === "choice") {
      const labels = Object.keys(question.criteria || {});
      return `${index + 1}. ${id} - pick exactly one of: ${labels.join(", ")}`;
    }
    if (question.type === "score") {
      const levels = Array.isArray(question.criteria) ? question.criteria : [];
      const named = levels.every((level) => typeof level === "string") ? ` (${levels.join(", ")})` : "";
      return `${index + 1}. ${id} - rate on ${levels.length} ordered levels${named}`;
    }
    if (question.type === "noul") {
      const trueSide = question.criteria?.true || "the proposition holds";
      return `${index + 1}. ${id} - yes or no: how likely is it that ${trueSide}?`;
    }
    if (question.type === "box") return `${index + 1}. ${id} - where in the picture`;
    return `${index + 1}. ${id} - ${question.type}`;
  });
}

function answersOf(result) {
  if (result && (result.error || result.status)) {
    return [{ error: result.error || `the model did not answer (status ${result.status})`, status: result.status }];
  }
  const answers = result?.answers ?? result ?? {};
  const entries = Object.entries(answers);
  if (!entries.length) return [{ error: "the answer had nothing in it" }];
  return entries.map(([id, answer]) => {
    if (!answer || typeof answer !== "object") return { id, error: "the answer had nothing in it" };
    const spread = answer.probabilities && typeof answer.probabilities === "object" ? answer.probabilities : null;
    const top = spread ? Object.entries(spread).sort((a, b) => b[1] - a[1])[0] : null;
    if (answer.type === "noul") return { id, kind: "noul", p_true: answer.noul };
    if (answer.type === "choice") {
      return {
        id,
        kind: "choice",
        label: answer.choice,
        probability: spread?.[answer.choice] ?? answer.probability,
        confidence: answer.confidence,
        spread,
      };
    }
    if (answer.type === "score") {
      return { id, kind: "score", mean: answer.score, modal: top?.[0], confidence: answer.confidence, levels: spread };
    }
    if (answer.type === "box") {
      return { id, kind: "box", box: answer.box, aggregate: answer.aggregate, confidence: answer.confidence };
    }
    return { id, kind: answer.type || "unknown", raw: answer };
  });
}

function answerLine(entry, levels = new Map()) {
  if (entry.error) return `could not decide: ${entry.error}`;
  if (entry.kind === "noul") return `P(true) ${at(entry.p_true, 4)}`;
  if (entry.kind === "choice") return `${entry.label} - ${at((entry.probability ?? 0) * 100, 1)}% of the distribution (agreement ${at(entry.confidence, 3)})`;
  if (entry.kind === "score") {
    // The distribution is keyed 0..n-1, while a model often writes its own 1-based labels inside the
    // level text. Quote the level in the model's own words, with the axis stated once, so a line
    // never reads "level 4: 5: Total outage".
    const question = levels.get(entry.id);
    const criteria = Array.isArray(question?.criteria) ? question.criteria : [];
    const axis = Math.max(0, criteria.length - 1);
    const raw = typeof criteria[Number(entry.modal)] === "string" ? criteria[Number(entry.modal)].trim() : null;
    const stripped = raw ? raw.replace(/^\s*\d+\s*[:.\-–)\]]\s*/, "").trim() : "";
    const named = stripped.length > 2 ? stripped : null;
    const scale = `expected ${at(entry.mean, 2)} on a 0-${axis} scale`;
    return named
      ? `"${named}" (${scale}, agreement ${at(entry.confidence, 3)})`
      : `${scale}, modal level ${entry.modal} (agreement ${at(entry.confidence, 3)})`;
  }
  if (entry.kind === "box") return `top ${at(entry.box?.top)} bottom ${at(entry.box?.bottom)} left ${at(entry.box?.left)} right ${at(entry.box?.right)} as ${entry.aggregate}`;
  return JSON.stringify(entry.raw ?? entry);
}

function stateLabel(state, index) {
  if (typeof state === "string") return state.length > 90 ? `${state.slice(0, 90)}…` : state;
  const text = JSON.stringify(state);
  return text.length > 90 ? `${text.slice(0, 90)}…` : text;
}

function judgedText(rows, answered, total) {
  const lines = [`## Judged ${answered} of ${total}`, ""];
  for (const row of rows) lines.push(`${row.index + 1}. ${row.state}${row.answers.length === 1 ? ` - ${row.answers[0].line}` : ""}`);
  if (rows.some((row) => row.answers.length > 1)) {
    lines.push("");
    for (const row of rows) {
      for (const answer of row.answers) lines.push(`   ${row.index + 1}.${answer.id || ""}: ${answer.line}`);
    }
  }
  lines.push("");
  lines.push("Probabilities and agreement describe how the model spread its own belief, not how correct it is.");
  return lines.join("\n");
}

export function buildServer() {
  const server = new McpServer({
    name: "firebending",
    version: VERSION,
    instructions: "To look for something in a picture: call plan with the user's own words, show them the list it returns, then call plan again with confirm and the picture path once they agree. rerun asks that same list of that same picture again when an answer came back weak, with no rewrite. Afterwards view prints where every box came out, preview draws those same boxes without measuring again, crop cuts a found thing out by name or number, getmouse measures everything from the pointer, and report_read opens the full text.",
  });

  // What crop, getmouse and the walks work from afterwards. A preview draws without cutting anything
  // out, so cut-outs already written on an earlier pass keep their names rather than going null.
  function applyLook(answer, args) {
    const carried = new Map((last?.objects || []).filter((object) => object.crop).map((object) => [object.name, object.crop]));
    const written = new Map((answer.crops || []).map((file) => [file.name, file.path]));
    last = {
      picture: answer.picture?.path || args.image,
      annotated: answer.annotated?.path || null,
      // Kept so a rerun cuts out and pads exactly as the look it repeats did.
      options: { cut_out: args.cut_out, pad: args.pad },
      // The pixel size the coordinates turn into. The drawn picture keeps the source's own size; when
      // the drawing was skipped the header of the source answers it instead.
      dims: answer.annotated
        ? { width: answer.annotated.width, height: answer.annotated.height }
        : headerSize(answer.picture?.path || args.image) || { width: null, height: null },
      objects: answer.objects.map((object) => ({
        index: object.index,
        name: object.name,
        kind: object.kind || null,
        box: object.box,
        presence: object.presence ?? null,
        confidence: object.confidence ?? null,
        weakest_edge: object.weakest_edge ?? null,
        error: object.error || null,
        crop: written.get(object.name) || carried.get(object.name) || null,
      })),
    };
  }

  // One picture through whatever list is agreed right now: "Y" re-activates it, the daemon reads the
  // picture, and the answer becomes what crop, getmouse and the walks work from. plan { confirm } and
  // rerun are the same machinery asked twice, so both come through here.
  async function askOnce(image, args, context) {
    const started = await daemon.tell(session, "Y", context.signal);
    const answer = JSON.parse((await daemon.look(session, image, {
      crop: args.cut_out ? 1 : 0,
      pad: args.pad,
      annotate: args.show ? 1 : 0,
    }, context.signal)).text());
    applyLook(answer, { image });
    return { answer, started: started.text() };
  }

  async function runLook(args, context) {
    const { answer, started } = await askOnce(args.image, args, context);
    const text = reportText(answer);
    const stored = store.put(createResource(text, {
      kind: "picture report",
      name: `Picture look: ${answer.answered} of ${answer.asked}`,
      tool: args.source,
      metadata: { picture: last.picture, objects: answer.objects.length, answered: answer.answered, failed: answer.failed },
    }));
    const images = args.show && answer.annotated?.path ? [answer.annotated.path] : [];
    return proseResult(`${text}\n\nReport id: ${stored.id} - report_read opens it by section; crop cuts any object out again by name or number.`, {
      ok: true,
      asked: answer.asked,
      answered: answer.answered,
      failed: answer.failed,
      objects: answer.objects,
      picture: answer.picture,
      annotated: answer.annotated,
      cut_outs: answer.crops,
      report_id: stored.id,
      started,
    }, images);
  }

  // The same agreed list over a group of pictures, one look each, a tally on top: "does this appear in
  // any of these" is one agreement, not one conversation per file. Only the last picture asked with the
  // drawing on, so show: true attaches one picture and not twenty.
  async function runBatch(pictures, skipped, args, context) {
    const perPicture = [];
    for (const [index, picture] of pictures.entries()) {
      try {
        const { answer } = await askOnce(picture, { ...args, show: index === pictures.length - 1 && args.show }, context);
        perPicture.push({ picture, answer });
      } catch (error) {
        perPicture.push({ picture, error: error instanceof Error ? error.message : String(error) });
      }
    }
    const tallies = new Map();
    for (const entry of perPicture) {
      if (entry.error) continue;
      for (const object of entry.answer.objects) {
        const tally = tallies.get(object.name) || { present: 0, absent: 0, located: 0, failed: 0 };
        if (object.error) tally.failed += 1;
        else if (typeof object.presence === "number") {
          if (object.presence >= 0.5) tally.present += 1;
          else tally.absent += 1;
        } else tally.located += 1;
        tallies.set(object.name, tally);
      }
    }
    const lines = [`## ${pictures.length} pictures, each asked the same agreed list`, ""];
    for (const entry of perPicture) {
      lines.push(`## ${entry.picture}`, "");
      if (entry.error) lines.push(`Not measured: ${entry.error}`, "");
      else {
        for (const object of entry.answer.objects) lines.push(objectLine(object));
        lines.push("");
      }
    }
    lines.push("## Tally", "");
    for (const [name, tally] of tallies) {
      const parts = [];
      if (tally.present) parts.push(`there in ${tally.present}`);
      if (tally.absent) parts.push(`not there in ${tally.absent}`);
      if (tally.located) parts.push(`located in ${tally.located}`);
      if (tally.failed) parts.push(`no answer in ${tally.failed}`);
      lines.push(`${name}: ${parts.join(", ") || "nothing came back"}`);
    }
    if (skipped) lines.push("", `${skipped} further pictures in that group were left for another call; one call covers ${MAX_PICTURES}.`);
    const text = lines.join("\n");
    const stored = store.put(createResource(text, {
      kind: "picture batch",
      name: `Picture batch: ${pictures.length} pictures`,
      tool: args.source,
      metadata: { pictures: pictures.length, listed: tallies.size, failed: perPicture.filter((entry) => entry.error).length },
    }));
    const measured = perPicture.filter((entry) => !entry.error);
    const newest = measured.at(-1);
    return proseResult(
      `${text}\n\nReport id: ${stored.id} - one section per picture, report_find locates a name across them. crop, view, preview and getmouse work on the newest picture measured${newest ? `: ${newest.picture}` : ""}.`,
      {
        ok: true,
        pictures: pictures.length,
        measured: measured.length,
        failed: perPicture.length - measured.length,
        skipped,
        tally: Object.fromEntries(tallies),
        per_picture: perPicture.map((entry) => ({ picture: entry.picture, error: entry.error || null, objects: entry.answer?.objects || [] })),
        report_id: stored.id,
      },
      args.show && newest && last.annotated ? [last.annotated] : [],
    );
  }

  // One picture or a whole group, decided by what was named: a folder is a group, so classifying a
  // directory is one confirmed call rather than a loop the model has to write itself.
  async function runNamed(args, context) {
    const { pictures, skipped } = picturesOf(args.image, args.images);
    const look = { ...args, image: pictures[0], source: args.source };
    if (pictures.length === 1) return runLook(look, context);
    return runBatch(pictures, skipped, look, context);
  }

  server.registerTool("plan", {
    title: "Agree on what to look for in a picture",
    description: "One control point for the list of things to look for. request: describe the picture and what matters - the model answers with the list, and any further request refines the one before it. confirm: true with an image path starts looking and returns coordinates for each thing. image may be a folder, or pass images with an explicit group: the same agreed list is then asked of every picture in it, one look per picture, each with its own section and a tally on top - that is the route for classifying a batch of files. count: 1-8 chooses how many of the list one look checks, most likely to be present first. clear: true drops the list and starts over. With none of these it reports where things stand. Nothing is measured until a human has seen the list: show it to the user, then confirm.",
    inputSchema: z.object({
      request: z.string().min(1).optional().describe("What to look for, in the user's words. Send it again after the user asks for a change and the list is refined from where it was."),
      confirm: z.boolean().optional().describe("True once the user has approved the list: this is when the picture is actually examined."),
      image: z.string().min(1).optional().describe("Picture the daemon should read, absolute path. A folder works too: every picture in it is asked the same agreed list, one look each, with a tally on top."),
      images: z.array(z.string().min(1)).min(1).max(24).optional().describe("An explicit group instead of one image: files and folders, mixed freely, each picture asked the same agreed list."),
      count: z.number().int().min(1).max(8).optional().describe("How many of the listed things one look checks, most likely first."),
      clear: z.boolean().optional().describe("Drop the list, its history and what is running."),
      cut_out: z.boolean().default(true).describe("With confirm: also cut each found thing out of the picture as its own file."),
      pad: z.number().min(0).max(1).default(0.05).describe("With confirm: widen each cut-out by this fraction of its own size."),
      show: z.boolean().default(false).describe("With confirm: draw the findings over the picture and return it too. Off by default - the printed list of what was found is the result, and view is the optional look."),
    }),
    annotations: { openWorldHint: true },
  }, guarded(async (args, context) => {
    const controls = [args.request, args.confirm, args.count, args.clear].some((value) => value !== undefined && value !== false);
    if (!controls) {
      const status = await daemon.status(session, context.signal);
      return proseResult(status.text(), { ok: true, status: status.text() });
    }
    if (args.count !== undefined) {
      const changed = await daemon.tell(session, String(args.count), context.signal);
      if (!args.request && !args.confirm && !args.clear) {
        return proseResult(changed.text(), { ok: true, count: args.count, note: changed.text() });
      }
    }
    if (args.clear) {
      const cleared = await daemon.tell(session, "N", context.signal);
      last = null;
      return proseResult(cleared.text(), { ok: true, cleared: true });
    }
    if (args.confirm) {
      if (!args.image && !args.images?.length) {
        throw new Error("confirm needs something to look at: pass image with its file path or a folder of them, or images with an explicit group.");
      }
      return runNamed({ ...args, source: "plan" }, context);
    }
    const drafted = await daemon.tell(session, args.request, context.signal);
    const text = planText(drafted.text());
    return proseResult(text, {
      ok: true,
      confirm_required: true,
      list: text,
      next: "Show this list to the user. Once they agree, call plan again with confirm: true and image - one picture, or a folder of them, or images with an explicit group; more text instead refines the list.",
    });
  }));

  server.registerTool("rerun", {
    title: "Ask the same questions of the same picture again",
    description: "The rerun button: no rewrite and no new agreement - the list agreed last time is asked of the same picture once more, and the reply is the usual printed update, one line per thing with its coordinates and agreement. Nothing is drawn and no picture comes back unless show is passed, which is what makes it cheap to press. It costs what a look costs: one request per listed thing. Worth pressing when a thing came back missing or with a weak edge, because a second sample shows whether its box moves or holds. Whatever framework is agreed right now is what gets asked - after getmouse that is the pointer's own question. The answer replaces the last one, so crop, getmouse and the walks read the newest boxes, and count and clear still belong to plan.",
    inputSchema: z.object({
      show: z.boolean().default(false).describe("Draw the new findings over the picture and return it too, as confirm with show does."),
      image: z.string().min(1).optional().describe("Ask again of this picture instead of the one the last look read. A folder asks the whole group, one look per picture."),
      images: z.array(z.string().min(1)).min(1).max(24).optional().describe("Ask again of this explicit group of files and folders instead."),
    }),
    annotations: { openWorldHint: true },
  }, guarded(async (args, context) => {
    if (!args.image && !args.images?.length && !last?.picture) {
      throw new Error("Nothing has been looked at yet, so there is nothing to rerun: agree on a list with plan, then confirm it against a picture.");
    }
    return runNamed({
      ...(args.image || args.images?.length ? args : { ...args, image: last.picture }),
      cut_out: last?.options?.cut_out ?? true,
      pad: last?.options?.pad ?? 0.05,
      source: "rerun",
    }, context);
  }));

  server.registerTool("crop", {
    title: "Cut a thing out of the picture",
    description: "Cut a region out of a picture and show it. After a look, name the object (its title or its number from the list) and the region comes from what was found; the picture from that look is reused, so image is only needed for a fresh one. A region can be given by hand as fractions of the picture, or several at once as objects.",
    inputSchema: z.object({
      object: z.string().min(1).optional().describe("What was found, by its title or its number in the list - \"llama logo\" or \"2\"."),
      image: z.string().min(1).optional().describe("Picture file to cut, absolute path. Defaults to the picture from the last look."),
      region: z.object({
        top: z.number().min(0),
        bottom: z.number().min(0),
        left: z.number().min(0),
        right: z.number().min(0),
      }).optional().describe("Region as fractions of the picture, 0 to 1."),
      regions: z.array(z.object({
        name: z.string().min(1).optional(),
        box: z.object({ top: z.number().min(0), bottom: z.number().min(0), left: z.number().min(0), right: z.number().min(0) }),
      })).max(8).optional().describe("Several regions at once, each kept as its own file."),
      pad: z.number().min(0).max(1).default(0.05).describe("Widen every region by this fraction of its own size on each side."),
      name: z.string().min(1).optional().describe("Label for the file the cut-out is written to."),
      show: z.boolean().default(true),
    }),
    annotations: { readOnlyHint: true },
  }, guarded(async (args, context) => {
    const named = args.object ? resolveObject(args.object) : null;
    const region = args.region
      || (named ? { top: named.box.top, bottom: named.box.bottom, left: named.box.left, right: named.box.right } : null);
    const many = args.regions?.length
      ? args.regions
      : region
        ? [{ name: args.name || named?.name || "region", box: region }]
        : null;
    if (!many) throw new Error("Say what to cut: name an object from the last look, or give a region.");
    const image = args.image || last?.picture;
    if (!image) throw new Error("There is no picture to cut: pass image, or run a look first.");
    // A cut-out the look already wrote with the same padding is reused instead of redrawn.
    if (named && named.crop && !args.image && !args.regions && args.pad === 0.05 && existsSync(named.crop)) {
      return proseResult(
        `Cut-out ${named.index}: ${named.name} - ${named.crop}`,
        { ok: true, reused: true, object: named.index, name: named.name, path: named.crop, box: named.box },
        args.show ? [named.crop] : [],
      );
    }
    const query = { format: "json", pad: args.pad, boxes: JSON.stringify(many) };
    const report = JSON.parse((await daemon.cut(image, query, context.signal)).text());
    const files = (report.crops || []).map((crop) => crop.path);
    const text = [
      `Cut ${report.cropped} region${report.cropped === 1 ? "" : "s"} out of ${image}:`,
      ...report.crops.map((crop) => `${crop.name || "region"}: ${crop.path} (${crop.width}x${crop.height})`),
    ].join("\n");
    return proseResult(text, { ok: true, image, pad: report.pad, cut_outs: report.crops }, args.show ? files.slice(0, 1) : []);
  }));

  server.registerTool("view", {
    title: "Print where everything came out",
    description: "The last look printed the way the command line prints it: one line per thing - never the pointer's own box - with its coordinates, its confidence, and its presence when the question was yes/no. Nothing is measured and nothing is drawn, so it costs nothing and repeats exactly. The reply closes with what can still be done: rerun measures those boxes again, preview draws them. Pass path to show some other file instead (a cut-out, a report's picture, anything the user named), or path with object / region to cut that part out and show only it, which measures nothing.",
    inputSchema: z.object({
      path: z.string().min(1).optional().describe("Show or cut this picture instead of printing the last look, absolute path."),
      object: z.string().min(1).optional().describe("With path: cut out just this part, an object's title or number from the last look."),
      region: z.object({ top: z.number().min(0), bottom: z.number().min(0), left: z.number().min(0), right: z.number().min(0) }).optional(),
      pad: z.number().min(0).max(1).default(0.05),
    }),
    annotations: { openWorldHint: true },
  }, guarded(async (args, context) => {
    if (!args.path) {
      // The pointer's own answer is named "mouse" and belongs to getmouse; it is not part of the list.
      const printed = (last?.objects || []).filter((object) => object.name !== "mouse");
      if (!printed.length) {
        throw new Error("Nothing has been looked for yet, so there is nothing to print: agree on a list with plan, then confirm it against a picture.");
      }
      const lines = printed.map((object) => {
        if (object.error) return `${object.index}: ${object.name}, not found - ${object.error}`;
        const region = EDGES.map((edge) => `${edge} ${at(object.box[edge])}`).join(", ");
        const numbers = [];
        if (typeof object.confidence === "number") numbers.push(`Confidence ${Math.round(object.confidence * 100)}%`);
        if (typeof object.presence === "number") numbers.push(`Presence ${Math.round(object.presence * 100)}%`);
        return `${object.index}: ${object.name}, ${region}${numbers.length ? ` - ${numbers.join(", ")}` : ""}`;
      });
      const size = last.dims?.width ? ` (${last.dims.width}x${last.dims.height})` : "";
      lines.push("", `Picture: ${last.picture || "none named by the last look"}${size} - rerun measures these again, preview draws them.`);
      return proseResult(lines.join("\n"), {
        ok: true,
        picture: last.picture || null,
        printed: printed.length,
        objects: printed,
      });
    }
    const wanted = args.object ? resolveObject(args.object) : null;
    const region = args.region || (wanted ? { top: wanted.box.top, bottom: wanted.box.bottom, left: wanted.box.left, right: wanted.box.right } : null);
    if (!region) return proseResult(`${args.path}`, { ok: true, path: args.path }, [args.path]);
    const many = [{ name: wanted?.name || "region", box: region }];
    const report = JSON.parse((await daemon.cut(args.path, { format: "json", pad: args.pad, boxes: JSON.stringify(many) }, context.signal)).text());
    const crop = report.crops[0];
    return proseResult(`${args.path}, ${wanted?.name || "that region"}: ${crop.path} (${crop.width}x${crop.height})`, { ok: true, ...crop }, [crop.path]);
  }));

  server.registerTool("preview", {
    title: "Say yes to the preview",
    description: "The yes to a look's preview gate: the picture with every box from the last look drawn on it, returned as an image. Nothing is re-measured - the coordinates already held go straight to the drawing helper, so the boxes cannot move and the answer costs one drawing call, not a model request. It draws up to the 8 things the last look found, and never the pointer's own box.",
    inputSchema: z.object({}),
    annotations: { readOnlyHint: true },
  }, guarded(async (args, context) => {
    const found = (last?.objects || []).filter((object) => !object.error && object.box && object.name !== "mouse");
    if (!found.length) {
      throw new Error("Nothing has been measured yet, so there is nothing to draw: agree on a list with plan, then confirm it against a picture.");
    }
    if (!last.picture) throw new Error("The last look named no picture to draw on.");
    const drawn = JSON.parse((await daemon.draw(last.picture, found.slice(0, 8).map((object) => ({ name: object.name, box: object.box })), context.signal)).text());
    const art = drawn.annotated;
    if (!art?.path) throw new Error("the drawing helper answered without a picture");
    // The drawn file becomes what a later view or crop call points at, without touching the numbers.
    last.annotated = art.path;
    last.dims = { width: art.width, height: art.height };
    return proseResult(`Drawn from the coordinates already held: ${art.path} (${art.width}x${art.height}) - ${found.length} box${found.length === 1 ? "" : "es"} painted, nothing measured again.`, {
      ok: true,
      preview: art.path,
      width: art.width,
      height: art.height,
      drawn: found.length,
    }, [art.path]);
  }));

  server.registerTool("getmouse", {
    title: "Measure everything from the pointer",
    description: "Ask the fixed coordinate-tag question - \"Where is the box of coordinates? If it exists, it's beside either a crosshair or mouse\" - about the picture the last look already read, then report each thing found in that picture by how far its centre sits from the pointer's centre, in pixels: `2: Green button, -400i+50j`, with i across and j downward. One model request, no cropping and no second look: the tag is boxed digits, which is what a vision model can localize, while a thin grey crosshair on a dark screen is what it invents instead. The overlay draws that tag below and to the right of the cursor whose numbers it prints, so the pointer box is that plate translated by its fixed layout margin (-100i+50j, i across and j upward, the frame the circle placer takes) and the offsets stay centre-to-centre arithmetic on a box that keeps its four edges. The answer is that printed list, the pointer point and the tag it was read from - no picture, no prose. It only works after a look, because that is what cached both the found objects and the picture's pixel size, and the question is always sent unchanged. Asking replaces what the picture pipeline is looking for, so re-confirm your own list afterwards.",
    inputSchema: z.object({
      image: z.string().min(1).optional().describe("Picture file, absolute path. Defaults to the picture the last look read."),
    }),
    annotations: { openWorldHint: true },
  }, guarded(async (args, context) => {
    if (!last?.objects?.length) {
      throw new Error("Nothing has been looked for in a picture yet: send plan a request, confirm it with an image, then call getmouse.");
    }
    const image = args.image || last.picture;
    if (!image) throw new Error("There is no picture to probe: pass image, or run a look on one first.");
    const width = Number(last.dims?.width);
    const height = Number(last.dims?.height);
    await daemon.tell(session, JSON.stringify(mousePlan(MOUSE_QUESTION)), context.signal);
    await daemon.tell(session, "Y", context.signal);
    // Nothing is drawn: this tool answers with vectors, and the pixel size comes from the look that
    // cached the objects.
    const first = JSON.parse((await daemon.look(session, image, { crop: 0, annotate: 0 }, context.signal)).text());
    const coarse = (first.objects || []).find((object) => !object.error && object.box);
    if (!coarse) {
      const reason = first.objects?.[0]?.error || "the model gave no box for the pointer";
      return proseResult(`Mouse: not located - ${reason}`, { ok: false, image, error: reason });
    }
    // One ask, one box: the tag of coordinates. The cursor is the box's own centre, translated: the
    // overlay draws that plate at a fixed margin below and right of the cursor whose numbers it prints,
    // so the cursor sits 100 px across and 50 px above that centre. The margin is written here and in
    // the reply the way the circle placer takes vectors - i across, j upward - as "-100i+50j", the one
    // number two measurements agree on: against a mark located pixel by pixel the plate's centre was
    // (1082.5, 587) with the cursor at (970.5, 524.5), and applied to a native-resolution capture the
    // derived cursor agreed with the overlay's live position to 0 px across and 21 px down. j flips
    // once, here, into the picture's downward-positive frame. The box is only moved, never resized or
    // collapsed, so the offsets below stay centre-to-centre arithmetic on a box with four real edges.
    const margin = { i: -100, j: 50 };
    const dx = Number.isFinite(width) ? margin.i / width : 0;
    const dy = Number.isFinite(height) ? -margin.j / height : 0;
    const pointer = {
      box: {
        top: coarse.box.top + dy,
        bottom: coarse.box.bottom + dy,
        left: coarse.box.left + dx,
        right: coarse.box.right + dx,
      },
      margin,
      tag: coarse.box,
      confidence: coarse.confidence ?? coarse.weakest_edge ?? 0,
      weakest_edge: coarse.weakest_edge ?? null,
      passes: 1,
    };
    const offsets = last.objects.map((object) => ({
      index: object.index,
      name: object.name,
      offset: object.error ? null : offsetFrom(pointer.box, object.box, width, height),
      error: object.error || null,
    }));
    const lines = offsets.map((entry) => entry.offset === null
      ? `${entry.index}: ${entry.name}, not located`
      : `${entry.index}: ${entry.name}, ${entry.offset}`);
    lines.push(
      "",
      `Mouse: t ${at(pointer.box.top)}, b ${at(pointer.box.bottom)}, l ${at(pointer.box.left)}, r ${at(pointer.box.right)} - Confidence ${Math.round(pointer.confidence * 100)}%`,
      `Coordinate tag: t ${at(coarse.box.top)}, b ${at(coarse.box.bottom)}, l ${at(coarse.box.left)}, r ${at(coarse.box.right)} - the plate is drawn below and right of the cursor, so the pointer box is that plate translated by ${margin.i}i${margin.j >= 0 ? "+" : ""}${margin.j}j, i across and j upward - the frame the circle placer takes.`,
    );
    last.mouse = { ...pointer, at: image };
    return proseResult(lines.join("\n"), {
      ok: true,
      image,
      pixels: { width: Number.isFinite(width) ? width : null, height: Number.isFinite(height) ? height : null },
      mouse: pointer,
      offsets,
    });
  }));

  /// The circle placer's own notation: radius first, then i to the right and j upward. The print
  /// measures j downward, so the sign turns over on the way out - arithmetic, not another look.
  function circleInput(across, down) {
    return `${Math.round(Math.hypot(across, down))}@${across}i${down <= 0 ? "+" : "-"}${Math.abs(down)}j`;
  }

  function titles() {
    return (last?.objects || []).filter((object) => !object.error).map((object) => `${object.index}: ${object.name}`);
  }

  /// The overlay answers refusals as JSON, sometimes with the complaint in a string and sometimes in
  /// an object, so read it from whichever shape it used.
  function complaintOf(answer) {
    return (typeof answer?.error === "string" ? answer.error : answer?.error?.message) || "";
  }

  /// The overlay keeps a control run active until its scheduled stroke has landed, so a walk called
  /// straight after another meets "Control run ... is active". Wait briefly for it to drain rather
  /// than stopping whatever else the keyboard was doing, then say so plainly if it will not.
  async function openPlacer(request) {
    for (let attempt = 0; attempt < 4; attempt += 1) {
      const opened = await overlay.control(request);
      if (opened?.run) return opened;
      const complaint = complaintOf(opened);
      if (!complaint.includes("is active")) {
        throw new Error(`the overlay did not open the circle placer: ${complaint || opened?.raw || "no run id came back"}`);
      }
      await new Promise((resolve) => setTimeout(resolve, 400));
    }
    throw new Error("the overlay is still busy with another control run; try the move again in a moment.");
  }

  /// Walk the overlay's circle placer with a measured vector and press one key at the Confirm stage:
  /// Z leaves the pointer standing on the target, B clicks the target and blinks the pointer back.
  /// The vector comes from the last getmouse, so the pointer's position is never re-guessed here.
  async function walkCircle(kind, args) {
    if (!last?.mouse) throw new Error("There is no pointer to measure from: run getmouse first.");
    const key = kind === "move" ? "Z" : "B";
    let across;
    let down;
    let what;
    if (args.offset) {
      const matched = /^(-?\d+)i([+-]\d+)j$/.exec(args.offset.trim());
      if (!matched) throw new Error(`offset must read like the print, for example "570i-165j" - got ${JSON.stringify(args.offset)}.`);
      across = Number(matched[1]);
      down = Number(matched[2]);
      what = `the offset ${args.offset.trim()}`;
    } else {
      const choices = titles();
      const named = args.object === undefined
        ? (choices.length === 1 ? last.objects.find((object) => !object.error) : null)
        : resolveObject(args.object);
      if (!named) {
        throw new Error(choices.length
          ? `Say which one: ${choices.join(", ")}.`
          : "Nothing was located, so there is nothing to move to.");
      }
      const pointer = last.mouse.box;
      const width = Number(last.dims?.width);
      const height = Number(last.dims?.height);
      across = Math.round(((named.box.left + named.box.right) / 2 - (pointer.left + pointer.right) / 2) * width);
      down = Math.round(((named.box.top + named.box.bottom) / 2 - (pointer.top + pointer.bottom) / 2) * height);
      what = `${named.index}: ${named.name}`;
    }
    const input = circleInput(across, down);
    if (!args.confirm) {
      return proseResult(
        `${what} - ${input} from the live cursor: ${across} across, ${Math.abs(down)} ${down <= 0 ? "up" : "down"}. Nothing has moved: this is the draft. Call again with confirm: true and the overlay's circle placer is walked - open it, feed this vector, then press ${key} once it answers with stage Confirm, which ${kind === "move" ? "leaves the pointer standing on the thing" : "clicks the target and blinks the pointer back to where it started"}.`,
        {
          ok: true,
          action: kind,
          confirmed: false,
          vector: input,
          i: across,
          j: down,
          radius: Math.round(Math.hypot(across, down)),
          key,
          start: args.origin || "live cursor",
        },
      );
    }
    const opened = await openPlacer(args.origin ? { menu: "circle_placer", origin: args.origin } : { menu: "circle_placer" });
    const run = opened.run;
    let placed = await overlay.control({ run, inputs: [input] });
    // The placer takes its action keys at the Confirm stage and nowhere else: pressed while it is still
    // at Distance the key goes into the distance field as text, which is what left a walk 40-56 px into
    // a 400-563 px vector. The reply that accepts the vector names the stage, so feed and check until it
    // says Confirm rather than pressing blind.
    for (let attempt = 0; placed?.dialog?.state?.stage !== "Confirm" && attempt < 4; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 150));
      placed = await overlay.control({ run, inputs: [input] });
    }
    const stage = placed?.dialog?.state?.stage || "";
    const origin = placed?.dialog?.state?.origin || opened?.dialog?.state?.origin || null;
    const target = placed?.dialog?.state?.target || null;
    if (!target) {
      throw new Error(`the overlay placed nothing for ${input}: ${complaintOf(placed) || placed?.dialog?.state?.error || "it answered without a target"}`);
    }
    const done = await overlay.control({ run, inputs: [key] });
    const refused = complaintOf(done) || null;
    const stageNote = stage === "Confirm"
      ? ""
      : ` The placer never answered with stage Confirm, so ${key} may have gone in as distance text: aim by hand from the printed vector if the pointer is not where this says.`;
    last.action = { kind, vector: input, origin, target, stage, error: refused };
    const line = kind === "move"
      ? `Moved ${what} - ${input} from [${origin}] to [${target}]; the pointer stands on it.`
      : `Clicked ${what} and blinked back - ${input} from [${origin}] to [${target}]; the click landed there and the pointer is back where it started.`;
    return proseResult([line, stageNote, refused && `The overlay reported: ${refused}`].filter(Boolean).join("\n"), {
      ok: !refused,
      action: kind,
      confirmed: true,
      stage,
      vector: input,
      i: across,
      j: down,
      radius: Math.round(Math.hypot(across, down)),
      origin,
      target,
      key,
      error: refused,
    });
  }

  const walkSchema = z.object({
    object: z.string().min(1).optional().describe("Which thing to aim at, by the exact title or number the last look printed, for example \"Qween\" or \"2\". One of them is required whenever more than one thing was found."),
    offset: z.string().min(1).optional().describe("Aim by this vector instead of an object's, shaped like the print: \"570i-165j\", i right and j down."),
    origin: z.array(z.number().int()).length(2).optional().describe("Start the vector at these screen coordinates instead of the live cursor."),
    confirm: z.boolean().default(false).describe("False prints the vector and what the walk would do without touching the overlay; true means the user agreed and the walk is taken."),
  });

  server.registerTool("move", {
    title: "Move the pointer along the measured vector",
    description: "Needs getmouse first. Take the vector from the pointer to one of the things the last look printed and walk the Macrohelp overlay's circle placer to it - open the menu, feed the vector, press the key that moves - so the pointer ends standing on the thing and nothing is clicked. The vector is arithmetic from the two boxes, never a second look. Gated like the plan and decide tools: without confirm it prints the vector and the walk it would take, touching nothing; with confirm: true it drives the overlay's MCP in that order and sends the key only once the placer answers with stage Confirm, because pressed earlier the key lands in the distance field as text and the pointer barely moves.",
    inputSchema: walkSchema,
    annotations: { openWorldHint: true },
  }, guarded(async (args) => walkCircle("move", args)));

  server.registerTool("clickreturn", {
    title: "Click along the measured vector and bring the pointer back",
    description: "The same gated walk as move, ending on the key that clicks the target and blinks the pointer back to where it started: the click happens where the vector pointed, and the pointer leaves no trace of the trip. The draft call prints the vector and the two keys; confirm: true takes the walk.",
    inputSchema: walkSchema,
    annotations: { openWorldHint: true },
  }, guarded(async (args) => walkCircle("clickreturn", args)));

  /// Ask the agreed framework about 1-N states, chunked under the daemon's 32-state / 256-decision
  /// ceiling. A state that cannot be decided is a row of its own; it never loses the states that
  /// answered, and a chunk whose whole request failed keeps the daemon's own reason.
  async function judgeStates(states, entries, context) {
    const per = Math.min(32, Math.max(1, Math.floor(256 / Math.max(1, entries.length))));
    const levels = new Map(entries);
    const rows = [];
    let answered = 0;
    for (let start = 0; start < states.length; start += per) {
      const chunk = states.slice(start, start + per);
      const batch = chunk.length > 1;
      let results;
      try {
        const parsed = jsonFrom((await daemon.judge(session, batch ? chunk : chunk[0], { batch }, context.signal)).text());
        results = batch ? (parsed?.results || []) : [{ state: chunk[0], result: parsed }];
      } catch (error) {
        results = chunk.map((state) => ({ state, result: { error: error.message } }));
      }
      if (!results.length) results = chunk.map((state) => ({ state, result: { error: "the judge returned nothing" } }));
      for (const item of results) {
        const answers = answersOf(item.result).map((answer) => ({ ...answer, line: answerLine(answer, levels) }));
        if (!(answers.length === 1 && answers[0].error)) answered += 1;
        rows.push({
          index: rows.length,
          state: stateLabel(item.state ?? chunk[0], rows.length),
          answers,
        });
      }
    }
    return { rows, answered, per };
  }

  server.registerTool("decide", {
    title: "Agree how to judge things, then judge them",
    description: "The text decision pipeline, in one control point. request: say the task in the user's words (label a set of things, rate severity, answer a yes/no about each item) and the questions it intends to ask come back for the user to approve; more text refines them. confirm: true judges what you send - one `state`, or `states` as an array of up to 32 items, each judged on its own and reported on its own line. clear: true drops the agreed questions. With no arguments it says where things stand. Nothing is decided until a human has read the questions, so show them first.",
    inputSchema: z.object({
      request: z.string().min(1).optional().describe("The judging task, in the user's words. Send it again after the user changes their mind and the questions are refined from where they were."),
      confirm: z.boolean().optional().describe("True once the user has approved the questions."),
      state: z.unknown().optional().describe("One thing to judge: a sentence, or a JSON object/array/null about it."),
      states: z.array(z.unknown()).min(1).max(32).optional().describe("Up to 32 things to judge in one call; each is still judged on its own."),
      clear: z.boolean().optional().describe("Drop the agreed questions and start over."),
    }),
    annotations: { openWorldHint: true },
  }, guarded(async (args, context) => {
    if ([args.request, args.confirm, args.clear].some((value) => value !== undefined && value !== false)) {
      if (args.clear) {
        const cleared = await daemon.tellJev(session, "N", context.signal);
        return proseResult(cleared.text(), { ok: true, cleared: true });
      }
      if (args.confirm) {
        const states = args.states ?? (args.state === undefined ? [] : [args.state]);
        if (!states.length) throw new Error("confirm needs something to judge: pass state (one thing) or states (up to 32).");
        await activateJev(context, "No questions are agreed yet: send request first, show the user what comes back, then confirm.");
        const { entries } = await frameworkOf(context);
        if (!entries.length) throw new Error("There are no agreed questions to ask: send a request first and show it to the user.");
        const { rows, answered, per } = await judgeStates(states, entries, context);
        const text = judgedText(rows, answered, states.length);
        const stored = store.put(createResource(text, {
          kind: "decision report",
          name: `Judged ${answered} of ${states.length}`,
          tool: "decide",
          metadata: { questions: entries.map(([id]) => id), batch_width: per },
        }));
        return proseResult(`${text}\n\nReport id: ${stored.id} - report_read opens it by section.`, {
          ok: true,
          judged: states.length,
          answered,
          failed: states.length - answered,
          questions: entries.map(([id, question]) => ({ id, type: question.type })),
          results: rows,
          report_id: stored.id,
        });
      }
      await daemon.tellJev(session, args.request, context.signal);
      const drafted = await frameworkOf(context, "draft");
      const questions = questionLines(drafted.entries);
      return proseResult(`What it will ask each thing:\n${questions.join("\n")}`, {
        ok: true,
        confirm_required: true,
        questions,
        next: "Show these questions to the user. Once they agree, call decide again with confirm: true and the state or states to judge; more text instead refines the questions.",
      });
    }
    const { entries, turns } = await frameworkOf(context);
    if (!entries.length) {
      return proseResult("No questions are agreed yet. Say what to judge with request, show the user the questions that come back, then confirm.", { ok: true, agreed: false });
    }
    const list = questionLines(entries);
    return proseResult(`Agreed questions:\n${list.join("\n")}`, { ok: true, agreed: true, questions: list, turns });
  }));

  server.registerTool("categorize", {
    title: "Label a long list with one agreed set of labels",
    description: "Large categorization: send the items and the labels. The first call drafts the one labelling question it intends to ask and shows it back; with confirm: true it judges every item and returns one line per item plus a tally. Items are short strings or JSON objects. Calls are chunked automatically (never more than 32 items, or 256 decisions, in one request), and an item it cannot decide is a line of its own without losing the rest. Up to 512 items per call.",
    inputSchema: z.object({
      items: z.array(z.union([z.string(), z.record(z.string(), z.unknown()), z.array(z.unknown())])).min(1).max(512).describe("The things to label, one per entry."),
      labels: z.array(z.string().min(1)).min(1).max(60).describe("The labels to choose from; each item gets exactly one."),
      note: z.string().min(1).optional().describe("Anything that helps tell the labels apart, in the user's words."),
      confirm: z.boolean().optional().describe("True once the user has approved the labelling question."),
    }),
    annotations: { openWorldHint: true },
  }, guarded(async (args, context) => {
    const task = [
      `Label each item with exactly one of these: ${args.labels.join(", ")}.`,
      args.note ? `How to tell them apart: ${args.note}.` : "Choose the label that fits best; if none truly fits, pick the closest one and say how sure you are.",
      "Each item arrives as its own state; the state is the item, nothing else.",
    ].join(" ");
    if (!args.confirm) {
      await daemon.tellJev(session, task, context.signal);
      const { entries } = await frameworkOf(context, "draft");
      const list = questionLines(entries);
      return proseResult(`The labelling question for ${args.items.length} items:\n${list.join("\n")}`, {
        ok: true,
        confirm_required: true,
        items: args.items.length,
        labels: args.labels,
        questions: list,
        next: "Show this to the user. With their agreement, call categorize again with confirm: true and the same items and labels.",
      });
    }
    await activateJev(context, "Nothing to agree to yet: call categorize without confirm first, show the user the labelling question, then confirm.");
    const { entries } = await frameworkOf(context);
    if (!entries.length) throw new Error("No labelling question was agreed: call categorize without confirm first and show it to the user.");
    const { rows, answered, per } = await judgeStates(args.items, entries, context);
    const tally = new Map();
    for (const row of rows) {
      const chosen = row.answers.find((answer) => answer.kind === "choice");
      if (chosen?.label) tally.set(chosen.label, (tally.get(chosen.label) || 0) + 1);
    }
    const text = `${judgedText(rows, answered, args.items.length)}\n\nTally: ${[...tally].map(([label, count]) => `${label} ${count}`).join(", ") || "no labels chosen"}`;
    const stored = store.put(createResource(text, {
      kind: "categorization",
      name: `Labelled ${answered} of ${args.items.length}`,
      tool: "categorize",
      metadata: { labels: args.labels, tally: Object.fromEntries(tally), batch_width: per },
    }));
    return proseResult(`${text}\n\nReport id: ${stored.id} - report_read opens it by section.`, {
      ok: true,
      items: args.items.length,
      answered,
      failed: args.items.length - answered,
      labels: args.labels,
      tally: Object.fromEntries(tally),
      results: rows,
      report_id: stored.id,
    });
  }));

  server.registerTool("report_read", {
    title: "Read part of a stored report",
    description: "Open sections of a stored report by number - the whole text of a look or a judged list, with every coordinate, label and file path. Omit id for the most recent one.",
    inputSchema: z.object({
      id: z.string().min(1).optional(),
      sections: z.array(z.number().int().min(1)).min(1).describe("One-based section numbers from the report index."),
    }),
    annotations: { readOnlyHint: true },
  }, guarded(async (args) => {
    const found = resource(store, args.id);
    return payloadResult({ ok: true, ...readResource(found, args.sections) });
  }));

  server.registerTool("report_find", {
    title: "Search a stored report",
    description: "Find the sections of a stored report that mention something - an object name, a label, a coordinate, a file - and return short excerpts with section numbers.",
    inputSchema: z.object({
      id: z.string().min(1).optional(),
      query: z.string().min(1),
      limit: z.number().int().min(1).max(100).default(12),
    }),
    annotations: { readOnlyHint: true },
  }, guarded(async (args) => {
    const found = resource(store, args.id);
    return payloadResult({ ok: true, id: found.id, matches: findInResource(found, args.query, args.limit) });
  }));

  server.registerTool("report_outline", {
    title: "Index a picture report",
    description: "List a stored report's sections with their headings, size and the picture files each one names, so the right section can be opened without reading the whole thing.",
    inputSchema: z.object({
      id: z.string().min(1).optional(),
      section: z.number().int().min(1).default(1),
      limit: z.number().int().min(1).max(256).default(64),
    }),
    annotations: { readOnlyHint: true },
  }, guarded(async (args) => {
    const found = resource(store, args.id);
    return payloadResult({ ok: true, ...resourceOutline(found, args.section, args.limit) });
  }));

  server.registerTool("report_resources", {
    title: "List stored reports and picture files",
    description: "Every report this process still holds, each with its index and the picture files it produced. Omit id for the most recent.",
    inputSchema: z.object({
      id: z.string().min(1).optional(),
    }),
    annotations: { readOnlyHint: true },
  }, guarded(async (args) => {
    if (args.id) return payloadResult({ ok: true, ...resourceSummary(resource(store, args.id), { includeFirst: false }) });
    return payloadResult({
      ok: true,
      reports: store.list().map((found) => resourceSummary(found, { includeFirst: false })),
    });
  }));

  return server;
}

process.on("uncaughtException", (error) => {
  process.stderr.write(`[firebending] ${error?.stack || error}\n`);
  process.exitCode = 1;
});

process.on("unhandledRejection", (error) => {
  process.stderr.write(`[firebending] ${error?.stack || error}\n`);
  process.exitCode = 1;
});

if (import.meta.main) {
  serveStdio(() => buildServer(), {
    legacy: "serve",
    onerror: (error) => process.stderr.write(`[firebending] ${error?.stack || error}\n`),
  });
}
