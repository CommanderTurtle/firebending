import ctypes
import sys
from ctypes import wintypes
from types import SimpleNamespace
from unittest.mock import Mock

import pytest
from openai_buffer_vision.model import Viewport
from openai_buffer_vision.uia import collect_uia


def test_native_handle_is_pointer_sized_and_window_loss_is_a_fallback(monkeypatch):
    foreground = Mock(return_value=1 << 40)
    monkeypatch.setattr(
        ctypes,
        "windll",
        SimpleNamespace(user32=SimpleNamespace(GetForegroundWindow=foreground)),
        raising=False,
    )
    root = Mock()
    root.window_text.return_value = "fixture"
    root.children.side_effect = RuntimeError("provider exited")
    desktop = Mock()
    desktop.return_value.window.return_value = root
    monkeypatch.setitem(sys.modules, "pywinauto", SimpleNamespace(Desktop=desktop))
    monkeypatch.setattr(sys, "platform", "win32")
    with pytest.raises(RuntimeError, match="Foreground UIA tree became unavailable"):
        collect_uia(Viewport(100, 100))
    assert foreground.restype is wintypes.HWND
    assert foreground.argtypes == []
    desktop.return_value.window.assert_called_once_with(handle=1 << 40)
