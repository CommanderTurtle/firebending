// The picture daemon's three routes, in plain text. Everything the model sees is prose the
// daemon already wrote for a person; this client only moves bytes and names the failure modes.
export class DaemonError extends Error {
  constructor(message, { status = null, fix = "" } = {}) {
    super(message);
    this.name = "DaemonError";
    this.status = status;
    this.fix = fix;
  }
}

const TIMEOUT_MS = Math.max(5_000, Number(process.env.FIREBENDING_TIMEOUT_MS) || 300_000);
const decoder = new TextDecoder();

export class Daemon {
  constructor(url = process.env.FIREBENDING_DAEMON_URL || "http://127.0.0.1:5199") {
    this.base = new URL(url.endsWith("/") ? url : `${url}/`);
  }

  href(pathname, query = {}) {
    const target = new URL(this.base.toString());
    target.pathname = `${this.base.pathname.replace(/\/$/, "")}${pathname}`;
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== null && value !== "") target.searchParams.set(key, String(value));
    }
    return target;
  }

  async request(pathname, { method = "POST", body, type = "", query = {}, signal } = {}) {
    let response;
    try {
      response = await fetch(this.href(pathname, query), {
        method,
        body,
        headers: type ? { "Content-Type": type } : undefined,
        signal: signal || AbortSignal.timeout(TIMEOUT_MS),
      });
    } catch (error) {
      throw new DaemonError(
        `The picture daemon at ${this.base.origin} did not answer: ${error?.message || error}`,
        { fix: "Start it (scripts/Start.ps1 on Windows, or OPENAI_BUFFER_Daemon__Url=<url> dotnet run --project src/OpenAiBufferDaemon/OpenAiBufferDaemon.fsproj -c Release), or point FIREBENDING_DAEMON_URL at the port it is on." },
      );
    }
    const payload = new Uint8Array(await response.arrayBuffer());
    if (!response.ok) {
      const reason = decoder.decode(payload).trim().split("\n").filter(Boolean).slice(0, 6).join(" ");
      throw new DaemonError(reason || `The daemon answered HTTP ${response.status}.`, { status: response.status });
    }
    return {
      bytes: payload,
      contentType: response.headers.get("content-type") || "",
      text: () => decoder.decode(payload).trim(),
    };
  }

  /// One message to the thing-to-look-for list: a request, "Y", "N", or a 1-8 integer.
  tell(session, message, signal) {
    return this.request("/v1/bboxset", { query: { session }, body: message, type: "text/plain; charset=utf-8", signal });
  }

  status(session, signal) {
    return this.request("/v1/bboxset", { method: "GET", query: { session }, signal });
  }

  /// The picture by path; the daemon keeps its own copy, so later tools can reuse it.
  look(session, image, options = {}, signal) {
    return this.request("/v1/bboxask", {
      query: { session, format: "json", ...options },
      body: image,
      type: "text/plain; charset=utf-8",
      signal,
    });
  }

  cut(image, options = {}, signal) {
    return this.request("/v1/bboxcrop", { query: options, body: image, type: "text/plain; charset=utf-8", signal });
  }

  /// Those boxes drawn over the whole picture: one drawing call, no model asked, so a preview cannot
  /// move a measured box.
  draw(image, boxes, signal) {
    return this.request("/v1/bboxcrop", {
      query: { format: "json", draw: 1, boxes: JSON.stringify(boxes) },
      body: image,
      type: "text/plain; charset=utf-8",
      signal,
    });
  }

  /// One message to the decision framework: a task, a refinement, "Y" to agree, or "N" to drop it.
  tellJev(session, message, signal) {
    return this.request("/v1/jevset", { query: { session }, body: message, type: "text/plain; charset=utf-8", signal });
  }

  judgeStatus(session, signal) {
    return this.request("/v1/jevset", { method: "GET", query: { session }, signal });
  }

  /// One state through the agreed framework; 1-32 of them travel together with `batch`.
  judge(session, state, { batch = false, format = "json" } = {}, signal) {
    const asJson = batch || typeof state !== "string";
    return this.request("/v1/jevask", {
      query: { session, batch: batch ? 1 : "", format },
      body: asJson ? JSON.stringify(state) : state,
      type: asJson ? "application/json" : "text/plain; charset=utf-8",
      signal,
    });
  }
}
