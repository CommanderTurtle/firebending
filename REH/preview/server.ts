/**
 * REH preview + cut editor. `bun preview/server.ts <workdir>` (or `reh preview <workdir>`).
 * Serves the source mp4 (range-streamed), the waveform/scene timeline, and a
 * [ADD CUT] button; saves cuts.json into the work dir for `reh scenes apply`.
 */
import { Glob } from "bun";
import { join, normalize } from "path";

const workdir = process.argv[2];
if (!workdir) {
  console.error("usage: bun preview/server.ts <workdir>   (work/<slug> dir)");
  process.exit(2);
}
const ROOT = Bun.fileURLToPath(new URL(".", import.meta.url));
const WORK = await (async () => {
  const p = process.argv[2]!;
  return Bun.file(join(p, "probe.json")).exists ? p : join(process.cwd(), p);
})();

const HERE = new URL(".", import.meta.url).pathname;

async function readJson(name: string): Promise<unknown | null> {
  const f = Bun.file(join(WORK, name));
  return (await f.exists()) ? await f.json() : null;
}

function videoPath(): string | null {
  // probe.json carries the absolute source path; fall back to first mp4 in parent
  return null; // resolved in /api/state to keep this sync-free
}

let cachedVideo: string | null | undefined;
async function findVideo(): Promise<string | null> {
  if (cachedVideo !== undefined) return cachedVideo;
  const probe = (await readJson("probe.json")) as { path?: string } | null;
  let v = probe?.path ?? null;
  if (v && !(await Bun.file(v).exists())) v = null;
  if (!v) {
    outer: for (const dir of [WORK, join(WORK, "..")]) {
      for await (const f of new Glob("*.mp4").scan(dir)) {
        v = join(dir, f);
        break outer;
      }
    }
  }
  cachedVideo = v;
  return v;
}

const MIME: Record<string, string> = {
  ".mp4": "video/mp4",
  ".mkv": "video/x-matroska",
  ".webm": "video/webm",
  ".jpg": "image/jpeg",
  ".png": "image/png",
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript",
  ".css": "text/css",
};

async function fileResponse(path: string): Promise<Response> {
  const f = Bun.file(path);
  if (!(await f.exists())) return new Response("not found", { status: 404 });
  return new Response(f, {
    headers: { "content-type": MIME[path.slice(path.lastIndexOf(".")).toLowerCase()] ?? "application/octet-stream", "accept-ranges": "bytes" },
  });
}

async function serveWithin(rel: string): Promise<Response> {
  const full = normalize(join(WORK, rel));
  if (!full.startsWith(WORK)) return new Response("forbidden", { status: 403 });
  return fileResponse(full);
}

const server = Bun.serve({
  hostname: "127.0.0.1",
  port: Number(process.env.REH_PORT ?? 7331),
  async fetch(req) {
    const url = new URL(req.url);
    const path = decodeURIComponent(url.pathname);

    if (path === "/" || path === "/index.html") {
      return new Response(Bun.file(join(HERE, "index.html")));
    }
    if (path === "/api/state") {
      const [probe, waveform, motion, scenes, cuts] = await Promise.all([
        readJson("probe.json"),
        readJson("waveform.json"),
        readJson("motion.json"),
        readJson("scenes.json"),
        readJson("cuts.json"),
      ]);
      const video = await findVideo();
      return Response.json({ work: WORK, video: { present: !!video }, probe, waveform, motion, scenes, cuts });
    }
    if (path === "/media") {
      const v = await findVideo();
      if (!v) return new Response("no source video found (probe.json path missing and no *.mp4 beside work dir)", { status: 404 });
      return fileResponse(v);
    }
    if (path.startsWith("/frames/")) {
      const name = path.slice("/frames/".length).replace(/[^\d.]/g, "");
      return serveWithin(join("frames", name));
    }
    if (path.startsWith("/keep/")) {
      const rel = path.slice("/keep/".length);
      if (/[.]|[.][.]/.test(rel.split("/").join(" ")) && rel.includes("..")) return new Response("forbidden", { status: 403 });
      return serveWithin(join("scenes", rel));
    }
    if (path === "/api/cuts" && req.method === "POST") {
      const body = (await req.json().catch(() => null)) as { cuts?: { t?: number }[]; confirmed?: boolean } | null;
      const raw = Array.isArray(body?.cuts) ? body!.cuts : null;
      if (!raw) return Response.json({ error: "expected {cuts:[{t}...]}" }, { status: 400 });
      const probe = (await readJson("probe.json")) as { duration_s?: number } | null;
      const dur = probe?.duration_s ?? Infinity;
      const ts = raw
        .map((c) => Number(c.t))
        .filter((t) => Number.isFinite(t) && t > 0.05 && t < dur - 0.05)
        .sort((a, b) => a - b);
      const dedup: number[] = [];
      for (const t of ts) if (!dedup.length || t - dedup[dedup.length - 1] > 0.066) dedup.push(Math.round(t * 1e6) / 1e6);
      const doc = { cuts: dedup.map((t) => ({ f: null, t })), confirmed: !!body?.confirmed };
      await Bun.write(join(WORK, "cuts.json"), JSON.stringify(doc, null, 2) + "\n");
      return Response.json({ ok: true, wrote: join(WORK, "cuts.json"), cuts: dedup.length });
    }
    if (path === "/api/cuts" && req.method === "GET") {
      const cuts = await readJson("cuts.json");
      return Response.json(cuts ?? { cuts: [], confirmed: false });
    }
    return new Response("not found", { status: 404 });
  },
});

console.log(`REH preview: http://${server.hostname}:${server.port}`);
console.log(`  work dir:  ${WORK}`);
console.log(`  save cuts → ${join(WORK, "cuts.json")}  then:  reh scenes apply ${WORK} --file ${join(WORK, "cuts.json")}`);
void videoPath;
