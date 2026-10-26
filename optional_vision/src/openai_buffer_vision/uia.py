from __future__ import annotations

import ctypes
import logging
import sys
from ctypes import wintypes
from typing import Any

from .model import BoundingBox, Element, Viewport

logger = logging.getLogger(__name__)

SEMANTIC_CONTROLS = {
    "button",
    "calendar",
    "checkbox",
    "combobox",
    "custom",
    "dataitem",
    "edit",
    "group",
    "hyperlink",
    "image",
    "list",
    "listitem",
    "menu",
    "menubar",
    "menuitem",
    "radiobutton",
    "scrollbar",
    "slider",
    "spinner",
    "tab",
    "tabitem",
    "text",
    "tree",
    "treeitem",
}


def collect_uia(
    viewport: Viewport,
    *,
    include_containers: bool = False,
    max_nodes: int = 300,
    max_depth: int = 8,
) -> tuple[list[Element], dict[str, Any]]:
    if sys.platform != "win32":
        raise RuntimeError("UI Automation is available only on Windows")

    try:
        from pywinauto import Desktop
    except ImportError as exc:
        raise RuntimeError(
            "UI Automation support is not installed; use the 'windows' or 'all' extra"
        ) from exc

    foreground = ctypes.windll.user32.GetForegroundWindow
    foreground.argtypes = []
    foreground.restype = wintypes.HWND
    hwnd = int(foreground() or 0)
    if not hwnd:
        return [], {}

    root = Desktop(backend="uia").window(handle=hwnd)
    window: dict[str, Any] = {"handle": hwnd}
    try:
        window["title"] = root.window_text()
    except Exception:
        logger.debug("Foreground window title became unavailable", exc_info=True)

    results: list[Element] = []
    try:
        children = root.children()
    except Exception as exc:
        # Providers/windows can disappear mid-snapshot. Let the pipeline's
        # existing RuntimeError fallback continue with OCR/CV instead of dying.
        raise RuntimeError("Foreground UIA tree became unavailable") from exc
    stack: list[tuple[Any, int]] = [(child, 1) for child in reversed(children)]
    inspected = 0

    while stack and inspected < max_nodes:
        wrapper, depth = stack.pop()
        inspected += 1
        try:
            info = wrapper.element_info
            rect = wrapper.rectangle()
            bbox = BoundingBox(
                float(rect.left),
                float(rect.top),
                float(rect.width()),
                float(rect.height()),
            )
            role = str(getattr(info, "control_type", "custom") or "custom").lower()
            name = str(getattr(info, "name", "") or "").strip()
            visible = bool(wrapper.is_visible())
            enabled = bool(wrapper.is_enabled())
            useful = include_containers or role in SEMANTIC_CONTROLS or bool(name)

            if visible and useful and bbox.area > 0 and bbox.visible_in(viewport):
                metadata: dict[str, Any] = {"depth": depth}
                automation_id = str(getattr(info, "automation_id", "") or "").strip()
                if automation_id:
                    metadata["automation_id"] = automation_id
                results.append(
                    Element(
                        role=role,
                        name=name,
                        bbox=bbox,
                        source="uia",
                        state={"enabled": enabled, "visible": visible},
                        metadata=metadata,
                    )
                )
        except Exception:
            logger.debug(
                "UIA element became unavailable during inspection", exc_info=True
            )

        if depth < max_depth:
            try:
                children = wrapper.children()
                stack.extend((child, depth + 1) for child in reversed(children))
            except Exception:
                logger.debug(
                    "UIA children became unavailable during inspection", exc_info=True
                )

    window["uia_nodes_inspected"] = inspected
    window["uia_nodes_emitted"] = len(results)
    return results, window
