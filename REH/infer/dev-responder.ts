/**
 * Dev-only harness: pretends to be the active model behind the firebending
 * buffer. Polls /v1/pending for message requests, logs the received payload
 * (attachment count + prompt head) to stderr, and answers with canned markdown.
 *
 *   bun infer/dev-responder.ts [--daemon URL] [--once]
 */
export {}; // module marker: top-level await

const DAEMON = (process.argv.includes("--daemon") ? process.argv[process.argv.indexOf("--daemon") + 1] : "http://127.0.0.1:5087").replace(/\/+$/, "");
const ONCE = process.argv.includes("--once");

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

const REPLY = `## Elements
- reel-static — box, near-full-frame inset ≈ [1151,22,186,92], fill #10141a, border #2a3646 2px
- badge-96gb — badge top-left ≈ [14,10,78,44], bg #d9b64a, text #10141a

## Static layout
Letterboxed 2.19:1 frame inside black bars; all furniture static in these frames.

## Motion
| t_start–t_end (s) | frames | element | change | from → to | easing |
| 0–0 (sample) | 0→0 | none | — | — | — |

## Text
- "96 GB" — 30px, weight 800, #10141a, badge-96gb

## Transition
in: none-observed. out: none-observed.

## Uncertainty
- single-frame sample: motion/easing unrecoverable (dry rehearsal responder)
`;

interface Pending {
  pending?: { request_id: string; path: string }[];
}
interface Req {
  body?: { messages?: { content?: unknown }[] };
}
interface Part {
  type?: unknown;
  text?: unknown;
}

// dev harness: shapes come from our own daemon, so shallow casts + runtime field
// checks at point of use; malformed shapes degrade to empty parts, never throws
function partsOf(content: unknown): Part[] {
  return Array.isArray(content) ? (content as Part[]) : [];
}
function textOf(parts: Part[]): string {
  for (const p of parts) if (p.type === "text" && typeof p.text === "string") return p.text;
  return "";
}

for (;;) {
  const pending = (await fetch(`${DAEMON}/v1/pending`).then((r) => r.json()).catch(() => ({}))) as Pending;
  const msg = pending.pending?.find((p) => p.path === "/v1/message");
  if (msg) {
    const req = (await fetch(`${DAEMON}/v1/requests/${msg.request_id}`).then((r) => r.json())) as Req;
    const parts = partsOf(req.body?.messages?.at(-1)?.content);
    const nImg = parts.filter((p) => p.type === "image_url").length;
    console.error(`[responder] ${msg.request_id}: ${nImg} image part(s), prompt ${JSON.stringify(textOf(parts).slice(0, 60))}…`);
    const res = await fetch(`${DAEMON}/v1/requests/${msg.request_id}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ content: REPLY }),
    });
    console.error(`[responder] replied HTTP ${res.status}`);
    if (ONCE) break;
  }
  await sleep(400);
}
