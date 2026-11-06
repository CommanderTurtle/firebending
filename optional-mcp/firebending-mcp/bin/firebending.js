#!/usr/bin/env bun
// firebending: look for named things in a picture, then cut them out and show them.
//
// The daemon does the measuring and answers in prose written for a person. This process adds what
// a conversation needs: a remembered last answer so a cut-out can be asked for by title or number,
// the picture itself rendered back as an image, and lossless sectioned reports for the long answers.
import { randomUUID } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
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

// The pointer probe, word for word. It is a contract with the vision model rather than a sentence to
// reword per call, so it is written once here and sent unchanged.
const MOUSE_QUESTION = "Where is the mouse or crosshair in this image? This would be a perfect center on on the mouse/crosshair itself";
const MOUSE_PLAN = { objects: [{ name: "mouse", kind: "where", instructions: MOUSE_QUESTION }] };

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

function reportText(answer) {
  const lines = [`## What was found in the picture: ${answer.answered} of ${answer.asked}`, ""];
  for (const object of answer.objects) lines.push(objectLine(object));
  lines.push("");
  if (answer.annotated) {
    lines.push(`The picture with what was found drawn on it: ${answer.annotated.path} (${answer.annotated.width}x${answer.annotated.height})`);
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
    instructions: "To look for something in a picture: call plan with the user's own words, show them the list it returns, then call plan again with confirm and the picture path once they agree. Afterwards crop and view work by object name or number, and report_read opens the full text.",
  });

  server.registerTool("plan", {
    title: "Agree on what to look for in a picture",
    description: "One control point for the list of things to look for. request: describe the picture and what matters - the model answers with the list, and any further request refines the one before it. confirm: true with an image path starts looking and returns coordinates for each thing. count: 1-8 chooses how many of the list one look checks, most likely to be present first. clear: true drops the list and starts over. With none of these it reports where things stand. Nothing is measured until a human has seen the list: show it to the user, then confirm.",
    inputSchema: z.object({
      request: z.string().min(1).optional().describe("What to look for, in the user's words. Send it again after the user asks for a change and the list is refined from where it was."),
      confirm: z.boolean().optional().describe("True once the user has approved the list: this is when the picture is actually examined."),
      image: z.string().min(1).optional().describe("Picture file the daemon should read, absolute path."),
      count: z.number().int().min(1).max(8).optional().describe("How many of the listed things one look checks, most likely first."),
      clear: z.boolean().optional().describe("Drop the list, its history and what is running."),
      cut_out: z.boolean().default(true).describe("With confirm: also cut each found thing out of the picture as its own file."),
      pad: z.number().min(0).max(1).default(0.05).describe("With confirm: widen each cut-out by this fraction of its own size."),
      show: z.boolean().default(true).describe("With confirm: return the picture with the findings drawn on it, so it can be seen."),
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
      if (!args.image) throw new Error("confirm needs the picture to look at: pass image with its file path.");
      const started = await daemon.tell(session, "Y", context.signal);
      const answer = JSON.parse((await daemon.look(session, args.image, {
        crop: args.cut_out ? 1 : 0,
        pad: args.pad,
      }, context.signal)).text());
      last = {
        picture: answer.picture?.path || args.image,
        annotated: answer.annotated?.path || null,
        // The pixel size the coordinates turn into; the drawn picture keeps the source's own size.
        dims: { width: answer.annotated?.width, height: answer.annotated?.height },
        objects: answer.objects.map((object) => ({
          index: object.index,
          name: object.name,
          box: object.box,
          presence: object.presence ?? null,
          error: object.error || null,
          crop: (answer.crops || []).find((file) => file.name === object.name)?.path || null,
        })),
      };
      const text = reportText(answer);
      const stored = store.put(createResource(text, {
        kind: "picture report",
        name: `Picture look: ${answer.answered} of ${answer.asked}`,
        tool: "plan",
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
        started: started.text(),
      }, images);
    }
    const drafted = await daemon.tell(session, args.request, context.signal);
    const text = planText(drafted.text());
    return proseResult(text, {
      ok: true,
      confirm_required: true,
      list: text,
      next: "Show this list to the user. Once they agree, call plan again with confirm: true and the image path; more text instead refines the list.",
    });
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
    title: "Show a picture file",
    description: "Return a picture file so it can be seen: the whole file, or just a region of it. Use it for the picture a look marked up, for a cut-out from a list, or for anything on disk the user mentions.",
    inputSchema: z.object({
      path: z.string().min(1).describe("Picture file, absolute path."),
      object: z.string().min(1).optional().describe("Cut out just this part: an object's title or number from the last look."),
      region: z.object({ top: z.number().min(0), bottom: z.number().min(0), left: z.number().min(0), right: z.number().min(0) }).optional(),
      pad: z.number().min(0).max(1).default(0.05),
    }),
    annotations: { readOnlyHint: true },
  }, guarded(async (args, context) => {
    const wanted = args.object ? resolveObject(args.object) : null;
    const region = args.region || (wanted ? { top: wanted.box.top, bottom: wanted.box.bottom, left: wanted.box.left, right: wanted.box.right } : null);
    if (!region) return proseResult(`${args.path}`, { ok: true, path: args.path }, [args.path]);
    const many = [{ name: wanted?.name || "region", box: region }];
    const report = JSON.parse((await daemon.cut(args.path, { format: "json", pad: args.pad, boxes: JSON.stringify(many) }, context.signal)).text());
    const crop = report.crops[0];
    return proseResult(`${args.path}, ${wanted?.name || "that region"}: ${crop.path} (${crop.width}x${crop.height})`, { ok: true, ...crop }, [crop.path]);
  }));

  server.registerTool("getmouse", {
    title: "Measure everything from the pointer",
    description: "Ask the fixed pointer question - where the mouse or crosshair is, centred exactly on it - about the picture the last look already read, then report each thing found in that picture by how far its centre sits from the pointer's centre, in pixels: `2: Green button, -400i+50j`, with i across and j downward. The answer is that printed list and the pointer box alone - no picture, no prose. It only works after a look, because that is what cached both the found objects and the picture's pixel size, and the question is always sent unchanged. Asking replaces what the picture pipeline is looking for, so re-confirm your own list afterwards.",
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
    await daemon.tell(session, JSON.stringify(MOUSE_PLAN), context.signal);
    await daemon.tell(session, "Y", context.signal);
    // Nothing is drawn: this tool answers with vectors, and the pixel size comes from the look that
    // cached the objects.
    const report = JSON.parse((await daemon.look(session, image, { crop: 0, annotate: 0 }, context.signal)).text());
    const mouse = (report.objects || []).find((object) => !object.error && object.box);
    const width = Number(last.dims?.width);
    const height = Number(last.dims?.height);
    if (!mouse) {
      const reason = report.objects?.[0]?.error || "the model gave no box for the pointer";
      return proseResult(`Mouse: not located - ${reason}`, { ok: false, image, error: reason });
    }
    const confidence = mouse.confidence ?? mouse.weakest_edge ?? 0;
    const offsets = last.objects.map((object) => ({
      index: object.index,
      name: object.name,
      offset: object.error ? null : offsetFrom(mouse.box, object.box, width, height),
      error: object.error || null,
    }));
    const lines = offsets.map((entry) => entry.offset === null
      ? `${entry.index}: ${entry.name}, not located`
      : `${entry.index}: ${entry.name}, ${entry.offset}`);
    lines.push("", `Mouse: t ${at(mouse.box.top)}, b ${at(mouse.box.bottom)}, l ${at(mouse.box.left)}, r ${at(mouse.box.right)} - Confidence ${Math.round(confidence * 100)}%`);
    last.mouse = { box: mouse.box, confidence, at: image };
    return proseResult(lines.join("\n"), {
      ok: true,
      image,
      pixels: { width: Number.isFinite(width) ? width : null, height: Number.isFinite(height) ? height : null },
      mouse: { box: mouse.box, confidence, weakest_edge: mouse.weakest_edge ?? null },
      offsets,
    });
  }));

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
