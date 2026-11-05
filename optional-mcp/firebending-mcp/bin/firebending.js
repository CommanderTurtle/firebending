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

  server.registerTool("report_read", {
    title: "Read part of a picture report",
    description: "Open sections of a stored report by number - the whole text of a look, with every coordinate and file path. Omit id for the most recent one.",
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
    title: "Search a picture report",
    description: "Find the sections of a stored report that mention something - an object name, a coordinate, a file - and return short excerpts with section numbers.",
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
