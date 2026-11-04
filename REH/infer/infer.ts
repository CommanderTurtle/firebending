/**
 * REH pass-2 inference driver.
 *
 *   bun infer/infer.ts <workdir> [--daemon URL] [--rate 1.0] [--force]
 *                                     [--dry-run] [--only 2,4-6]
 *
 * Sequentially, one scene at a time: stage the scene's lossless keep PNGs on the
 * firebending httpd (`POST /v1/file`, absolute paths — the daemon reads them when
 * the message goes out), then `POST /v1/message` with the guiding prompt and wait
 * for the active model's markdown reply. Reply → scenes/<dir>/description.md.
 *
 * Contract notes (firebending docs/MESSAGES.md):
 *  - a 504 keeps staged files for deliberate retry (we retry the SAME message ≤2 times)
 *  - 429 carries Retry-After (honored, not counted as a failure)
 *  - never blind-resend after a success or partial answer
 *  - ≤64 files and ≤64 MiB of file bytes per message → larger keep sets are
 *    chunked into sequential part-messages, replies joined as "## part k"
 *
 * --dry-run performs zero HTTP: it writes out/PROMPT.md (the exact bundle an
 * inside-omp coding agent consumes instead, keep paths included).
 */
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "path";

// ---------- args ----------
const argv = process.argv.slice(2);
const work = argv.find((a) => !a.startsWith("--"));
if (!work) die("usage: bun infer/infer.ts <workdir> [--daemon URL] [--rate s] [--force] [--dry-run] [--only 2,4-6]");
const flag = (name: string): string | undefined => {
  const i = argv.indexOf(`--${name}`);
  return i >= 0 ? argv[i + 1] : undefined;
};
const bool = (name: string) => argv.includes(`--${name}`);
const DAEMON = (flag("daemon") ?? "http://127.0.0.1:5087").replace(/\/+$/, "");
const RATE = Number(flag("rate") ?? "1.0");
const FORCE = bool("force");
const DRY = bool("dry-run");
const ONLY = flag("only");
const HERE = dirname(new URL(import.meta.url).pathname);

function die(msg: string): never {
  console.error(`reh-infer: ${msg}`);
  process.exit(2);
}
function sleep(s: number): Promise<void> {
  const { promise, resolve: resume } = Promise.withResolvers<void>();
  setTimeout(resume, s * 1000);
  return promise;
}

// ---------- work dir state ----------
const WORK = resolve(work!);
const read = <T,>(p: string): T => JSON.parse(readFileSync(join(WORK, p), "utf8"));
const scenes = read<{ fps: [number, number]; width: number; height: number; nb_frames: number; scenes: Scene[] }>("scenes.json");
const probe = read<{
  path: string;
  vcodec: string;
  acodec?: string;
  width: number;
  height: number;
  fps: [number, number];
  duration_s: number;
}>("probe.json");
const waveform = existsSync(join(WORK, "waveform.json"))
  ? read<{ silence: { t0: number; t1: number }[]; beats: { t: number; strength: number }[] }>("waveform.json")
  : { silence: [], beats: [] };
const template = readFileSync(join(HERE, "prompts", "scene.md"), "utf8");

interface Scene { i: number; dir: string; f0: number; f1: number; t0: number; t1: number }

// shapes authored by the Rust engine (see REH/DESIGN.md contract)
interface Boundary { f: number; kind: string }
interface MotionBox { kind: string; f0: number; f1: number; bbox: [number, number, number, number]; peak_f: number }
interface SceneMeta {
  i: number;
  dir: string;
  f0: number;
  f1: number;
  t0: number;
  t1: number;
  in: Boundary | null;
  out: Boundary | null;
  motions: MotionBox[];
  audio_hints: { silence_in: [number, number][]; beats_in: number[] };
  keep_f: number[];
}

const wanted = new Set<number | "all">(["all"]);
if (ONLY) {
  wanted.clear();
  for (const part of ONLY.split(",")) {
    const m = part.trim().match(/^(\d+)(?:-(\d+))?$/);
    if (!m) die(`bad --only selector ${part}`);
    const a = Number(m[1]), b = m[2] ? Number(m[2]) : a;
    for (let i = a; i <= b; i++) wanted.add(i);
  }
}

// ---------- prompt assembly ----------
function keepFiles(sceneDir: string): string[] {
  const kd = join(sceneDir, "keep");
  if (!existsSync(kd)) return [];
  return readdirSync(kd).filter((f) => f.endsWith(".png")).sort().map((f) => join(kd, f));
}

function sceneFacts(sc: Scene, meta: SceneMeta): string {
  const lines = [
    `canvas: ${probe.width}x${probe.height} px · ${probe.fps[0]}/${probe.fps[1]} fps · video ${probe.duration_s}s · codec ${probe.vcodec}`,
    `scene ${String(sc.i).padStart(2, "0")}: frames ${sc.f0}..${sc.f1} (${sc.t0.toFixed(3)}s..${sc.t1.toFixed(3)}s)`,
    `boundary in: ${meta.in ? `${meta.in.kind} at frame ${meta.in.f}` : "video start"}`,
    `boundary out: ${meta.out ? `${meta.out.kind} at frame ${meta.out.f} (first frame of next scene)` : "end of video"}`,
    `keep frames provided (frame = filename minus 1):`,
    ...describeRoles(sc, meta),
  ];
  if (meta.motions.length) {
    lines.push("machine motion intervals (bbox in native px, may be approximate):");
    for (const m of meta.motions) lines.push(`  ${m.kind} f${m.f0}→f${m.f1} bbox [${m.bbox.join(",")}] peak f${m.peak_f}`);
  } else {
    lines.push("machine motion intervals: NONE — treat the scene as static unless frames prove otherwise");
  }
  const beats = waveform.beats.filter((b) => b.t >= sc.t0 && b.t <= sc.t1).map((b) => b.t.toFixed(2)).join(", ");
  const sil = waveform.silence.filter((z) => z.t0 >= sc.t0 && z.t1 <= sc.t1).map((z) => `${z.t0.toFixed(2)}-${z.t1.toFixed(2)}`).join(", ");
  if (beats) lines.push(`audio beat hints (non-authoritative): ${beats}`);
  if (sil) lines.push(`audio silence runs inside scene: ${sil}`);
  return lines.join("\n");
}

function describeRoles(sc: Scene, meta: SceneMeta): string[] {
  const out: string[] = [];
  const role = (f: number): string => {
    if (f === sc.f0) return "scene head (pre-transition resting state)";
    if (f === sc.f1) return "scene tail (final state before cut out)";
    for (const m of meta.motions ?? []) {
      if (f === m.f0 - 1) return `pre-movement (before ${m.kind} f${m.f0}→f${m.f1})`;
      if (f === m.f1 + 1) return `post-movement settled (after ${m.kind} f${m.f0}→f${m.f1})`;
    }
    return "reference";
  };
  for (const f of meta.keep_f) out.push(`  ${String(f + 1).padStart(6, "0")}.png = frame ${f} — ${role(f)}`);
  return out;
}

function buildPrompt(sc: Scene, meta: SceneMeta, part?: { k: number; of: number }): string {
  let p = template.replaceAll("{{FACTS}}", sceneFacts(sc, meta));
  if (part) p += `\n\n(This is part ${part.k}/${part.of} of the scene's frames; describe only what these frames establish, later parts extend it.)`;
  return p;
}

// ---------- firebending httpd client ----------
async function http(path: string, init?: RequestInit, timeoutMs = 1_800_000): Promise<Response> {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), timeoutMs);
  try {
    return await fetch(`${DAEMON}${path}`, { ...init, signal: ctl.signal });
  } finally {
    clearTimeout(timer);
  }
}

async function clearFiles(): Promise<void> {
  try { await http("/v1/file", { method: "DELETE" }); } catch { /* daemon may lack DELETE; harmless */ }
}

async function stageFile(absPath: string): Promise<void> {
  let r = await http("/v1/file", { method: "POST", headers: { "content-type": "text/plain" }, body: absPath });
  if (r.status >= 400) {
    // structured fallback in case this daemon build wants JSON
    r = await http("/v1/file", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ path: absPath, mime: "image/png" }) });
  }
  if (r.status >= 400) die(`stage ${absPath}: HTTP ${r.status} ${await r.text()}`);
}

async function sendPrompt(prompt: string): Promise<string> {
  let attempts = 0;
  for (;;) {
    let r: Response;
    try {
      r = await http("/v1/message", { method: "POST", headers: { "content-type": "text/plain" }, body: prompt });
    } catch (e) {
      if (attempts++ < 2) { console.warn(`  transport error (${String(e).slice(0, 80)}), deliberate retry ${attempts}/2`); await sleep(2); continue; }
      throw e;
    }
    if (r.status === 429) {
      const ra = Number(r.headers.get("retry-after") ?? "1");
      console.warn(`  daemon busy (429), honoring Retry-After: ${ra}`);
      await sleep(Math.min(Math.max(ra, 1), 30));
      continue; // does not count against message retries
    }
    if (r.status === 504) {
      if (attempts++ < 2) { console.warn(`  model did not answer in time (504), deliberate retry ${attempts}/2 (staged files retained)`); continue; }
      die("504 after 3 attempts — daemon up? model polling /v1/buffer? answer manually or lower expectations");
    }
    if (r.status >= 400) die(`message: HTTP ${r.status} ${await r.text()}`);
    const text = await r.text();
    if (!text.trim()) die("empty markdown reply — refusing to write an empty description");
    return text;
  }
}

// ---------- chunking ----------
const MAX_FILES = 64, MAX_BYTES = 64 * 1024 * 1024 - (1 << 20); // headroom
function chunk(paths: string[]): string[][] {
  const out: string[][] = [];
  let cur: string[] = [], bytes = 0;
  for (const p of paths) {
    const sz = statSync(p).size;
    if (cur.length && (cur.length + 1 > MAX_FILES || bytes + sz > MAX_BYTES)) { out.push(cur); cur = []; bytes = 0; }
    cur.push(p); bytes += sz;
  }
  if (cur.length) out.push(cur);
  return out;
}

// ---------- main ----------
const outDir = join(WORK, "out");
mkdirSync(outDir, { recursive: true });
const bundle: string[] = [];

console.log(`reh-infer: ${WORK}`);
console.log(`  daemon: ${DAEMON}${DRY ? "  (dry-run: no HTTP)" : ""}`);

let done = 0, skipped = 0, failed = 0;
for (const sc of scenes.scenes) {
  if (!wanted.has("all") && !wanted.has(sc.i)) continue;
  // engine writes scenes[].dir as a bare "NN-hhmmss" name; tolerate the
  // DESIGN.md-documented "scenes/NN-hhmmss" spelling too
  const sceneDir = join(WORK, sc.dir.startsWith("scenes/") ? sc.dir : join("scenes", sc.dir));
  const descPath = join(sceneDir, "description.md");
  const metaPath = join(sceneDir, "meta.json");
  if (!existsSync(metaPath)) { console.warn(`  scene ${sc.dir}: meta.json missing — rerun 'reh scenes export'`); continue; }
  const meta = JSON.parse(readFileSync(metaPath, "utf8")) as SceneMeta;
  const keep = keepFiles(sceneDir);
  const prompt = buildPrompt(sc, meta);

  bundle.push(
    `---\n\n## Scene ${sc.dir}  (${sc.t0.toFixed(3)}s–${sc.t1.toFixed(3)}s)\n`,
    prompt,
    keep.length ? `\nKeep PNGs to view (in order): \n${keep.map((p) => `- ${p}`).join("\n")}\n` : "\n(no keep frames)\n",
  );

  if (DRY) { skipped++; continue; }
  if (existsSync(descPath) && !FORCE) { console.log(`  scene ${sc.dir}: description.md exists — skipping (--force)`); skipped++; continue; }
  if (!keep.length) { console.warn(`  scene ${sc.dir}: no keep frames — writing stub`); await Bun.write(descPath, "# (no keep frames — nothing observable)\n"); continue; }

  process.stdout.write(`  scene ${sc.dir}: staging ${keep.length} PNG(s) → waiting for model… `);
  const parts = chunk(keep);
  const replies: string[] = [];
  try {
    for (let k = 0; k < parts.length; k++) {
      await clearFiles();
      for (const p of parts[k]) await stageFile(p);
      const reply = await sendPrompt(parts.length > 1 ? buildPrompt(sc, meta, { k: k + 1, of: parts.length }) : prompt);
      replies.push(parts.length > 1 ? `## part ${k + 1}/${parts.length}\n\n${reply}` : reply);
    }
  } catch (e) {
    console.log(`FAILED`);
    failed++;
    continue;
  }
  await Bun.write(descPath, replies.join("\n\n").trimEnd() + "\n");
  console.log(`ok (${replies.reduce((a, r) => a + r.length, 0)} chars)`);
  done++;
  if (RATE > 0) await sleep(RATE);
}

if (DRY || bundle.length) {
  const head = `# REH assembly bundle\n\nvideo: ${probe.path}\ncanvas ${probe.width}x${probe.height} @ ${probe.fps[0]}/${probe.fps[1]} fps · ${scenes.nb_frames} frames\n\nRead REH/SKILL.md first. Each scene below lists its facts prompt and its lossless keep PNGs; view them, then author out/index.html.\n`;
  await Bun.write(join(outDir, "PROMPT.md"), head + bundle.join("\n"));
  console.log(`  bundle: ${join(outDir, "PROMPT.md")}`);
}
console.log(`done: ${done} described, ${skipped} skipped, ${failed} failed`);
if (!DRY) console.log(`next: open a coding agent on ${WORK} with REH/SKILL.md → out/index.html`);
