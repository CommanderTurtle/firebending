// A lossless, sectioned store for what the daemon reports about a picture.
//
// Shape copied from localflame (AGPL-3.0, can1357/oh-my-pi author): a compact index plus section 1
// comes back with every result, later sections are read by number, and bytes on disk are listed as
// references and only opened when asked. Sectioning here is lossless by construction and asserted.
import { createHash } from "node:crypto";
import { existsSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

export const RESOURCE_SECTION_CHARS = 12_000;
const IMAGE_EXTENSION = /\.(?:avif|bmp|gif|jpe?g|png|webp)$/i;
const FILE_CANDIDATE = /["'`(]?(\/[\w.@+-]+(?:\/[\w.@+-]+)+|[A-Za-z]:[\\/][\w.@+\\-]+(?:[\\/][\w.@+\\-]+)+|\.[\\/][\w.@+\\/-]+)\.(?:avif|bmp|gif|jpe?g|png|webp)/gi;
const searchIndexes = new WeakMap();

function asText(value) {
  return typeof value === "string" ? value : JSON.stringify(value ?? "");
}

export function cleanReportMarkdown(value) {
  return asText(value)
    .replace(/\r\n?/g, "\n")
    .replace(/[ \t]+$/gm, "")
    .replace(/\n{4,}/g, "\n\n\n")
    .trim();
}

/// Paragraph-aware splitting: never cut inside a table row, prefer the widest clean break.
export function segmentMarkdown(value, maxChars = RESOURCE_SECTION_CHARS) {
  const source = asText(value);
  if (!source) return [""];
  const width = Math.max(64, Math.trunc(Number(maxChars) || RESOURCE_SECTION_CHARS));
  if (source.length <= width) return [source];
  const sections = [];
  let cursor = 0;
  while (cursor < source.length) {
    if (source.length - cursor <= width) {
      sections.push(source.slice(cursor));
      break;
    }
    const target = cursor + width;
    const minimum = cursor + Math.floor(width * 0.55);
    let cut = source.lastIndexOf("\n\n", target);
    if (cut >= minimum) cut += 2;
    else {
      cut = source.lastIndexOf("\n", target);
      if (cut >= minimum) cut += 1;
      else {
        cut = source.lastIndexOf(" ", target);
        cut = cut >= minimum ? cut + 1 : target;
      }
    }
    sections.push(source.slice(cursor, cut));
    cursor = cut;
  }
  return sections;
}

function sectionHeading(section) {
  return section.match(/^#{1,6}\s+(.+)$/m)?.[1]?.trim().replace(/\s+#+$/, "") || "";
}

/// Which pictures a section names. The bytes stay on disk; opening one is a separate call.
export function sectionFiles(section) {
  const found = [];
  const seen = new Set();
  for (const match of section.matchAll(FILE_CANDIDATE)) {
    const file = path.resolve(match[1].replace(/[\\/]$/, "") + "." + match[0].split(".").pop().toLowerCase());
    if (seen.has(file)) continue;
    seen.add(file);
    let bytes = null;
    let readable = false;
    try {
      const info = existsSync(file) ? statSync(file) : null;
      readable = Boolean(info?.isFile());
      bytes = readable ? info.size : null;
    } catch {
      readable = false;
    }
    found.push({ path: file, name: path.basename(file), bytes, readable });
  }
  return found;
}

export function analyzeSections(sections) {
  return sections.map((section) => ({ heading: sectionHeading(section), files: sectionFiles(section) }));
}

function searchableText(value) {
  return String(value || "").normalize("NFKC").toLocaleLowerCase();
}

function searchWords(value) {
  return searchableText(value).match(/[\p{L}\p{N}_-]{2,}/gu) ?? [];
}

function buildSearchIndex(resource) {
  const cached = searchIndexes.get(resource);
  if (cached) return cached;
  const sections = resource.sections.map(searchableText);
  const words = new Map();
  sections.forEach((section, index) => {
    for (const word of new Set(searchWords(section))) {
      if (!words.has(word)) words.set(word, new Set());
      words.get(word).add(index);
    }
  });
  const created = { sections, words };
  searchIndexes.set(resource, created);
  return created;
}

function excerpt(section, query) {
  const flattened = section.replace(/\s+/g, " ").trim();
  const position = searchableText(flattened).indexOf(searchableText(query));
  const start = Math.max(0, (position < 0 ? 0 : position) - 90);
  const text = flattened.slice(start, start + 260);
  return `${start ? "…" : ""}${text}${start + 260 < flattened.length ? "…" : ""}`;
}

export function findInResource(resource, queryValue, limit = 12) {
  const query = String(queryValue || "").trim();
  if (!query) throw new Error("find needs a non-empty query.");
  const index = buildSearchIndex(resource);
  const normalizedQuery = searchableText(query);
  const queryWords = [...new Set(searchWords(query))];
  let candidates = null;
  for (const word of queryWords) {
    const matches = index.words.get(word) ?? new Set();
    candidates = candidates === null
      ? new Set(matches)
      : new Set([...candidates].filter((section) => matches.has(section)));
  }
  const pool = candidates === null ? index.sections.map((_, section) => section) : [...candidates];
  const phrase = pool.filter((section) => index.sections[section].includes(normalizedQuery));
  const matches = phrase.length ? phrase : queryWords.length ? pool : [];
  return matches.slice(0, Math.max(1, Math.min(100, Math.trunc(Number(limit) || 12)))).map((section) => ({
    section: section + 1,
    heading: resource.section_meta[section].heading,
    files: resource.section_meta[section].files.map((file) => file.path),
    excerpt: excerpt(resource.sections[section], query),
  }));
}

export function createResource(contentValue, additions = {}) {
  const content = cleanReportMarkdown(contentValue);
  const sections = segmentMarkdown(content, additions.maxChars);
  if (sections.join("") !== content) throw new Error("Internal sectioning error: content was not preserved.");
  const hash = createHash("sha256")
    .update(String(additions.kind || "report"))
    .update("\0")
    .update(String(additions.name || ""))
    .update("\0")
    .update(content)
    .digest("hex")
    .slice(0, 20);
  const sectionMeta = analyzeSections(sections);
  return {
    id: `fb_${hash}`,
    kind: String(additions.kind || "report"),
    name: String(additions.name || "report"),
    source_tool: String(additions.tool || ""),
    content,
    sections,
    section_meta: sectionMeta,
    files: [...new Map(sectionMeta.flatMap((meta) => meta.files).map((file) => [file.path, file])).values()],
    created_at: new Date().toISOString(),
    additions: additions.additions && typeof additions.additions === "object" ? additions.additions : {},
    metadata: additions.metadata && typeof additions.metadata === "object" ? additions.metadata : {},
    opened: false,
    read_sections: [],
  };
}

export function resourceOutline(resource, start = 1, limit = 64) {
  const first = Math.max(1, Math.trunc(Number(start) || 1));
  const size = Math.max(1, Math.min(256, Math.trunc(Number(limit) || 64)));
  const indexed = resource.sections.map((section, position) => ({
    section: position + 1,
    characters: section.length,
    heading: resource.section_meta[position].heading,
    files: resource.section_meta[position].files.map((file) => file.path),
  }));
  const from = Math.min(indexed.length, first - 1);
  const window = indexed.slice(from, from + size);
  return {
    id: resource.id,
    start: window[0]?.section ?? null,
    returned: window.length,
    section_count: indexed.length,
    outline_truncated: from + window.length < indexed.length,
    next_outline_section: from + window.length < indexed.length ? from + window.length + 1 : null,
    outline: window,
  };
}

export function resourceSummary(resource, { includeFirst = true } = {}) {
  const outline = resourceOutline(resource, 1, 64);
  const summary = {
    id: resource.id,
    kind: resource.kind,
    name: resource.name,
    source_tool: resource.source_tool,
    characters: resource.content.length,
    section_count: resource.sections.length,
    files: resource.files.map((file) => ({ path: file.path, bytes: file.bytes, readable: file.readable })),
    file_count: resource.files.length,
    outline: outline.outline,
    outline_truncated: outline.outline_truncated,
    next_outline_section: outline.next_outline_section,
    opened: resource.opened,
    read_sections: [...resource.read_sections],
  };
  if (includeFirst) summary.sections = [{ section: 1, heading: resource.section_meta[0].heading, content: resource.sections[0] }];
  return summary;
}

export function readResource(resource, sections = []) {
  const wanted = [];
  const seen = new Set();
  for (const value of Array.isArray(sections) ? sections : [sections]) {
    const number = Math.trunc(Number(value));
    if (!Number.isFinite(number) || number < 1 || number > resource.sections.length || seen.has(number)) continue;
    seen.add(number);
    wanted.push(number);
  }
  const opening = !resource.opened;
  resource.opened = true;
  for (const number of wanted) if (!resource.read_sections.includes(number)) resource.read_sections.push(number);
  resource.read_sections.sort((left, right) => left - right);
  return {
    id: resource.id,
    reopened: opening,
    sections: wanted.map((number) => ({
      section: number,
      heading: resource.section_meta[number - 1].heading,
      files: resource.section_meta[number - 1].files.map((file) => file.path),
      content: resource.sections[number - 1],
    })),
    read_sections: [...resource.read_sections],
  };
}

export class ResourceStore {
  constructor({ maxResources = 24, maxBytes = 24 * 1024 * 1024 } = {}) {
    this.resources = new Map();
    this.maxResources = Math.max(1, Math.trunc(Number(maxResources) || 24));
    this.maxBytes = Math.max(1024, Math.trunc(Number(maxBytes) || 24 * 1024 * 1024));
    this.bytes = 0;
  }

  put(resource) {
    this.resources.delete(resource.id);
    this.resources.set(resource.id, resource);
    this.bytes += Buffer.byteLength(resource.content, "utf8");
    this.#evict();
    return resource;
  }

  get(id) {
    return this.resources.get(String(id || "")) || null;
  }

  list() {
    return [...this.resources.values()].reverse();
  }

  #evict() {
    while (this.resources.size > 1
      && (this.resources.size > this.maxResources || this.bytes > this.maxBytes)) {
      const oldestId = this.resources.keys().next().value;
      const oldest = this.resources.get(oldestId);
      if (!oldest) break;
      this.resources.delete(oldestId);
      this.bytes -= Buffer.byteLength(oldest.content, "utf8");
    }
  }
}
