(function installBufferVisionObserver(api) {
  "use strict";

  const { Clipboard, Front, mapkey } = api;
  const endpoint = globalThis.OPENAI_BUFFER_VISION_ENDPOINT ||
    "http://127.0.0.1:5087/v1/vision/observations";
  const selector = [
    "a[href]",
    "button",
    "input",
    "textarea",
    "select",
    "summary",
    "label",
    "[role]",
    "[aria-label]",
    "[contenteditable='true']",
    "h1",
    "h2",
    "h3"
  ].join(",");

  function rounded(value) {
    return Math.round(value * 100) / 100;
  }

  function visible(element, rect) {
    const style = getComputedStyle(element);
    return rect.width > 0 &&
      rect.height > 0 &&
      rect.bottom >= 0 &&
      rect.right >= 0 &&
      rect.top <= innerHeight &&
      rect.left <= innerWidth &&
      style.display !== "none" &&
      style.visibility !== "hidden" &&
      Number(style.opacity) !== 0;
  }

  function roleOf(element) {
    const explicit = element.getAttribute("role");
    if (explicit) return explicit;

    const tag = element.tagName.toLowerCase();
    const inputType = (element.getAttribute("type") || "text").toLowerCase();
    if (tag === "a") return "link";
    if (tag === "button" || inputType === "button" || inputType === "submit") return "button";
    if (tag === "textarea" || tag === "input") return inputType === "checkbox" ? "checkbox" : "textbox";
    if (tag === "select") return "combobox";
    if (tag === "summary") return "button";
    if (/^h[1-6]$/.test(tag)) return "heading";
    if (tag === "label") return "label";
    return tag;
  }

  function nameOf(element) {
    const value = typeof element.value === "string" ? element.value : "";
    const text = element.innerText || element.textContent || "";
    return (
      element.getAttribute("aria-label") ||
      element.getAttribute("alt") ||
      element.getAttribute("title") ||
      element.getAttribute("placeholder") ||
      value ||
      text
    ).replace(/\s+/g, " ").trim().slice(0, 240);
  }

  function domPath(element) {
    const parts = [];
    let current = element;
    while (current && current.nodeType === Node.ELEMENT_NODE && parts.length < 8) {
      let part = current.tagName.toLowerCase();
      if (current.id) {
        part += `#${CSS.escape(current.id)}`;
        parts.unshift(part);
        break;
      }
      const parent = current.parentElement;
      if (parent) {
        const siblings = Array.from(parent.children).filter(child => child.tagName === current.tagName);
        if (siblings.length > 1) part += `:nth-of-type(${siblings.indexOf(current) + 1})`;
      }
      parts.unshift(part);
      current = parent;
    }
    return parts.join(" > ");
  }

  function stateOf(element) {
    return {
      disabled: Boolean(element.disabled || element.getAttribute("aria-disabled") === "true"),
      checked: Boolean(element.checked || element.getAttribute("aria-checked") === "true"),
      selected: Boolean(element.selected || element.getAttribute("aria-selected") === "true"),
      expanded: element.getAttribute("aria-expanded"),
      focused: element === document.activeElement
    };
  }

  function normalizedBox(rect) {
    const x1 = Math.max(0, Math.min(999, Math.round(rect.left / Math.max(1, innerWidth) * 999)));
    const y1 = Math.max(0, Math.min(999, Math.round(rect.top / Math.max(1, innerHeight) * 999)));
    const x2 = Math.max(0, Math.min(999, Math.round(rect.right / Math.max(1, innerWidth) * 999)));
    const y2 = Math.max(0, Math.min(999, Math.round(rect.bottom / Math.max(1, innerHeight) * 999)));
    return { x1, y1, x2, y2 };
  }

  function captureObservation() {
    const nodes = Array.from(new Set(document.querySelectorAll(selector)));
    const elements = nodes
      .map(element => ({ element, rect: element.getBoundingClientRect() }))
      .filter(item => visible(item.element, item.rect))
      .sort((a, b) => a.rect.top - b.rect.top || a.rect.left - b.rect.left)
      .slice(0, 750)
      .map((item, index) => {
        const { element, rect } = item;
        return {
          id: index + 1,
          role: roleOf(element),
          name: nameOf(element),
          bbox: {
            x: rounded(rect.left),
            y: rounded(rect.top),
            width: rounded(rect.width),
            height: rounded(rect.height),
            x2: rounded(rect.right),
            y2: rounded(rect.bottom)
          },
          bbox_normalized: normalizedBox(rect),
          state: stateOf(element),
          dom_path: domPath(element),
          source: "dom"
        };
      });

    const screenTag = elements.map(item => {
      const box = item.bbox_normalized;
      const safeName = item.name.replace(/["\\\r\n]/g, " ");
      return `[${item.id}] ${item.role.toUpperCase()}[${box.x1},${box.y1},${box.x2},${box.y2}] "${safeName}"`;
    }).join("\n");

    return {
      schema_version: "vision-observation/1",
      source: "surfingkeys-dom",
      captured_at: new Date().toISOString(),
      coordinate_space: "css-viewport",
      page: {
        url: `${location.origin}${location.pathname}`,
        title: document.title
      },
      viewport: {
        width: innerWidth,
        height: innerHeight,
        device_pixel_ratio: devicePixelRatio,
        scroll_x: scrollX,
        scroll_y: scrollY,
        screen_x: screenX,
        screen_y: screenY,
        outer_width: outerWidth,
        outer_height: outerHeight
      },
      elements,
      screen_tag: screenTag
    };
  }

  async function postObservation() {
    const observation = captureObservation();
    const text = JSON.stringify(observation, null, 2);
    try {
      const response = await fetch(endpoint, {
        method: "POST",
        mode: "cors",
        credentials: "omit",
        headers: { "Content-Type": "application/json" },
        body: text
      });
      if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
      const result = await response.json();
      Front.showBanner(`Vision observation ${result.observation_id}: ${result.element_count} elements`);
    } catch (error) {
      Clipboard.write(text);
      Front.showBanner(`Vision endpoint unavailable; copied ${observation.elements.length} elements`);
      console.warn("Buffer vision observation failed", error);
    }
  }

  mapkey("<Alt-v>", "Capture visible DOM as a vision observation", postObservation);
})(api);
