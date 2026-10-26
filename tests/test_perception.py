"""Opt-in regression fixtures: fake NInfer and OCR only; no model or screen capture.

These fixtures start the built daemon when pytest is explicitly run. Importing this
module does not start a server, subprocess, OCR engine, or inference request.
"""

import base64
import concurrent.futures
import json
import os
import queue
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
PNG = b"\x89PNG\r\n\x1a\nsynthetic transport fixture, not decoded by fake NInfer"
ART = "+----------+\n| A        |\n|  SPADES  |\n|        A |\n+----------+"
FRAMEWORK = {
    "state": "observation supplied later",
    "questions": {
        "card": {
            "type": "choice",
            "instructions": "Read the visible rank and suit; do not invent hidden cards.",
            "criteria": {
                "ace_spades": "ACE of SPADES",
                "unknown": "Unclear or different",
            },
        }
    },
}
ANSWER = {
    "answers": {
        "card": {
            "type": "choice",
            "choice": "ace_spades",
            "probabilities": {"ace_spades": 0.875, "unknown": 0.125},
            "confidence": 0.75,
        }
    }
}


def tool(name, key, value):
    return {
        "choices": [
            {
                "finish_reason": "tool_calls",
                "message": {
                    "role": "assistant",
                    "tool_calls": [
                        {
                            "id": "fixture-call",
                            "type": "function",
                            "function": {
                                "name": name,
                                "arguments": json.dumps({key: value}),
                            },
                        }
                    ],
                },
            }
        ]
    }


def draft(framework=FRAMEWORK):
    return tool("submit_jev_framework", "framework_json", json.dumps(framework))


class Api:
    def __init__(self, url):
        self.url = url

    def call(self, path, body=None, headers=None, method=None):
        headers = dict(headers or {})
        if body is not None and not isinstance(body, (str, bytes)):
            body = json.dumps(body).encode()
            headers.setdefault("Content-Type", "application/json")
        if isinstance(body, str):
            body = body.encode()
            headers.setdefault("Content-Type", "text/plain; charset=utf-8")
        request = urllib.request.Request(self.url + path, body, headers, method=method)
        try:
            response = urllib.request.urlopen(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.read().decode(), response.headers


class FakeNinfer:
    def __init__(self):
        self.replies = queue.Queue()
        self.calls = []
        self.lock = threading.Lock()

    def reply(self, body, status=200, headers=None, gate=None):
        self.replies.put((status, body, headers or {}, gate))

    def snapshot(self):
        with self.lock:
            return list(self.calls)


@pytest.fixture
def bench(tmp_path):
    upstream = FakeNinfer()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            with upstream.lock:
                upstream.calls.append((self.path, None))
            encoded = json.dumps({"data": [{"id": "fixture-model"}]}).encode()
            self.send_response(200)
            self.end_headers()
            self.wfile.write(encoded)

        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            with upstream.lock:
                upstream.calls.append((self.path, body))
            try:
                status, result, headers, gate = upstream.replies.get_nowait()
            except queue.Empty:
                status, result, headers, gate = (
                    500,
                    {"error": "unexpected call"},
                    {},
                    None,
                )
            if gate is not None:
                gate.wait(timeout=5)
            encoded = (
                result if isinstance(result, bytes) else json.dumps(result).encode()
            )
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(encoded)))
            for key, value in headers.items():
                self.send_header(key, value)
            self.end_headers()
            try:
                self.wfile.write(encoded)
            except (BrokenPipeError, ConnectionResetError):
                pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    package = tmp_path / "fake-helper" / "openai_buffer_vision"
    package.mkdir(parents=True)
    (package / "__init__.py").write_text("")
    # This subprocess is a wire-protocol fake, deliberately not the actual OCR package.
    (package / "oneshot.py").write_text(
        "import json, os, pathlib, sys, time\n"
        "prefix = sys.stdin.buffer.read(4)\n"
        "engine = sys.argv[sys.argv.index('--engine') + 1]\n"
        "if prefix == b'FAIL':\n"
        "    print('fixture missing dependency: install OCR extra', file=sys.stderr)\n"
        "    raise SystemExit(2)\n"
        "if prefix == b'SLOW':\n"
        "    with pathlib.Path(os.environ['OCR_FIXTURE_LOG']).open('a') as log:\n"
        "        log.write(json.dumps({'pid': os.getpid(), 'engine': engine, 'bytes': 4}) + '\\n')\n"
        "    time.sleep(30)\n"
        "raw = prefix + sys.stdin.buffer.read()\n"
        "with pathlib.Path(os.environ['OCR_FIXTURE_LOG']).open('a') as log:\n"
        "    log.write(json.dumps({'pid': os.getpid(), 'engine': engine, 'bytes': len(raw)}) + '\\n')\n"
        "print(json.dumps({'text': 'one\\n  two', 'engine': engine}))\n",
        encoding="utf-8",
    )
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    api = Api(f"http://127.0.0.1:{port}")
    helper_log = tmp_path / "helper.jsonl"
    env = dict(
        os.environ,
        OPENAI_BUFFER_Daemon__Url=api.url,
        OPENAI_BUFFER_Daemon__DataDirectory=str(tmp_path / "data"),
        OPENAI_BUFFER_Daemon__NinferBaseUrl=f"http://127.0.0.1:{server.server_port}",
        OPENAI_BUFFER_Daemon__NinferModel="fixture-model",
        OPENAI_BUFFER_Daemon__NinferApiKey="",
        OPENAI_BUFFER_Daemon__NinferMaxTokens="4096",
        OPENAI_BUFFER_Daemon__PerceptionTimeoutSeconds="4",
        OPENAI_BUFFER_Daemon__OcrPython=sys.executable,
        PYTHONPATH=str(package.parent),
        OCR_FIXTURE_LOG=str(helper_log),
    )
    dll = ROOT / "src/OpenAiBufferDaemon/bin/Release/net10.0/OpenAiBufferDaemon.dll"
    assert dll.is_file(), "Build Release before explicitly running this bench"
    with (tmp_path / "daemon.log").open("wb") as log:
        process = subprocess.Popen(
            ["dotnet", str(dll)],
            cwd=ROOT,
            env=env,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
        try:
            for _ in range(100):
                try:
                    if api.call("/health")[0] == 200:
                        break
                except OSError:
                    if process.poll() is not None:
                        raise AssertionError((tmp_path / "daemon.log").read_text())
                    time.sleep(0.05)
            else:
                raise AssertionError("Daemon did not start")
            yield api, upstream, helper_log
        finally:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            server.shutdown()
            server.server_close()
            thread.join(timeout=5)


def session():
    return "?session=" + uuid.uuid4().hex


def activate(api, upstream, suffix):
    upstream.reply(draft())
    assert api.call("/v1/jevset" + suffix, "Identify playing cards")[0] == 200
    assert api.call("/v1/jevset" + suffix, "Y")[0] == 200


def test_idle_inspection_and_switches_do_not_call_model_or_load_ocr(bench):
    api, upstream, helper_log = bench
    assert api.call("/v1/ocr")[1].startswith("0: tesseract")
    assert api.call("/v1/jevset")[0] == 200
    for value in ("1", "0", "1"):
        assert api.call("/v1/ocr", value)[0] == 200
    time.sleep(0.1)
    assert upstream.snapshot() == []
    assert not helper_log.exists()


@pytest.mark.parametrize("path", ["/v1/jevset", "/v1/jevask", "/v1/ocr", "/v1/cereal"])
def test_perception_rejects_cross_site_and_rebound_hosts(bench, path):
    api, upstream, _ = bench
    assert api.call(path, "text", {"Origin": "https://untrusted.example"})[0] == 403
    assert api.call(path, "text", {"Host": "rebound.example"})[0] == 403
    assert not upstream.snapshot()


def test_refinement_needs_y_and_n_preserves_last_activated_framework(bench):
    api, upstream, _ = bench
    suffix = session()
    assert api.call("/v1/jevask" + suffix, ART)[0] == 409
    assert api.call("/v1/jevset" + suffix, "Y")[0] == 409
    upstream.reply(draft())
    status, text, _ = api.call("/v1/jevset" + suffix, {"message": "Identify this card"})
    assert status == 200 and "Y/N" in text
    assert api.call("/v1/jevask" + suffix, ART)[0] == 409
    upstream.reply(draft())
    assert api.call("/v1/jevset" + suffix, "Add an unknown option")[0] == 200
    messages = upstream.snapshot()[-1][1]["messages"]
    assert [item["role"] for item in messages] == [
        "system",
        "user",
        "assistant",
        "user",
    ]
    assert api.call("/v1/jevset" + suffix, "Y")[0] == 200
    assert api.call("/v1/jevset" + suffix, "N")[0] == 200
    upstream.reply(ANSWER)
    status, output, _ = api.call("/v1/jevask" + suffix, ART)
    assert status == 200 and "ace_spades" in output and "0.875000" in output
    assert upstream.snapshot()[-1][0] == "/v1/systemone"
    assert upstream.snapshot()[-1][1]["state"] == ART
    assert api.call("/v1/jevask" + session(), ART)[0] == 409


def test_framework_repairs_are_bounded_and_invalid_draft_is_never_saved(bench):
    api, upstream, _ = bench
    suffix = session()
    upstream.reply(draft({"state": "missing questions"}))
    upstream.reply(draft())
    assert api.call("/v1/jevset" + suffix, "Cards")[0] == 200
    assert len(upstream.snapshot()) == 2
    assert "Repair" in upstream.snapshot()[-1][1]["messages"][-1]["content"]
    assert api.call("/v1/jevset" + suffix, "N")[0] == 200
    for _ in range(3):
        upstream.reply(draft({"state": "invalid", "questions": {}}))
    assert api.call("/v1/jevset" + suffix, "Broken")[0] == 502
    assert api.call("/v1/jevset" + suffix, "Y")[0] == 409
    assert len(upstream.snapshot()) == 5


def test_clear_during_refinement_discards_late_draft(bench):
    api, upstream, _ = bench
    suffix = session()
    gate = threading.Event()
    upstream.reply(draft(), gate=gate)
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/jevset" + suffix, "Cards")
        for _ in range(100):
            if upstream.snapshot():
                break
            time.sleep(0.01)
        assert upstream.snapshot()
        assert api.call("/v1/jevset" + suffix, "N")[0] == 200
        gate.set()
        assert waiting.result()[0] == 409
    assert api.call("/v1/jevset" + suffix, "Y")[0] == 409


def test_cereal_repairs_unsafe_ascii_and_sends_correct_native_image_part(bench):
    api, upstream, _ = bench
    upstream.reply(tool("submit_cereal", "ascii", "\x1b[31munsafe"))
    upstream.reply(tool("submit_cereal", "ascii", ART))
    status, output, _ = api.call("/v1/cereal", PNG, {"Content-Type": "image/png"})
    assert status == 200 and output == "```text\n" + ART + "\n```\n"
    calls = upstream.snapshot()
    assert len(calls) == 2
    part = calls[0][1]["messages"][1]["content"][1]
    assert (
        part["image_url"]["url"]
        == "data:image/png;base64," + base64.b64encode(PNG).decode()
    )
    assert calls[0][1]["max_tokens"] > 0
    assert calls[0][1]["tool_choice"]["function"]["name"] == "submit_cereal"
    for _ in range(3):
        upstream.reply(tool("submit_cereal", "ascii", "x" * 97))
    assert api.call("/v1/cereal", PNG, {"Content-Type": "image/png"})[0] == 502
    assert len(upstream.snapshot()) == 5


def test_native_busy_retry_and_validated_json_return(bench):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    upstream.reply({"error": "busy"}, status=423, headers={"Retry-After": "0"})
    upstream.reply({"error": "busy"}, status=429, headers={"Retry-After": "0"})
    upstream.reply(ANSWER)
    status, output, headers = api.call("/v1/jevask" + suffix + "&format=json", ART)
    assert status == 200 and json.loads(output) == ANSWER
    assert headers.get_content_type() == "application/json"
    assert sum(path == "/v1/systemone" for path, _ in upstream.snapshot()) == 3
    malformed = json.loads(json.dumps(ANSWER))
    malformed["answers"]["card"]["probabilities"]["ace_spades"] = 0.1
    upstream.reply(malformed)
    assert api.call("/v1/jevask" + suffix, ART)[0] == 502


def test_noul_and_score_keep_native_meaning_and_full_distributions(bench):
    api, upstream, _ = bench
    suffix = session()
    framework = {
        "state": "later",
        "questions": {
            "visible": {"type": "noul", "instructions": "A card is visible."},
            "clarity": {
                "type": "score",
                "instructions": "How readable is the card?",
                "criteria": ["unreadable", "readable"],
            },
        },
    }
    response = {
        "answers": {
            "visible": {"type": "noul", "noul": 0.8},
            "clarity": {
                "type": "score",
                "score": 0.75,
                "probabilities": {"0": 0.25, "1": 0.75},
                "confidence": 0.5,
            },
        }
    }
    upstream.reply(draft(framework))
    assert api.call("/v1/jevset" + suffix, "Presence and clarity")[0] == 200
    assert api.call("/v1/jevset" + suffix, "Y")[0] == 200
    upstream.reply(response)
    status, text, _ = api.call("/v1/jevask" + suffix, {"visible": ART})
    assert status == 200
    assert "P(true)" in text and "0.800000" in text and "0.750000" in text
    assert '"probabilities"' in text
    assert upstream.snapshot()[-1][1]["state"] == {"visible": ART}


@pytest.mark.parametrize(
    "malformed",
    [
        "not an answer object",
        {
            "answers": {
                "card": {
                    "type": "choice",
                    "choice": 123,
                    "probabilities": {"ace_spades": 0.875, "unknown": 0.125},
                    "confidence": 0.75,
                }
            }
        },
        (
            b'{"answers":{"card":{"type":"choice","choice":"ace_spades",'
            b'"probabilities":{"ace_spades":0.875,"unknown":0.125,"unknown":0.0},'
            b'"confidence":0.75}}}'
        ),
    ],
)
def test_malformed_upstream_types_and_duplicate_distributions_are_not_returned(
    bench, malformed
):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    upstream.reply(malformed)
    assert api.call("/v1/jevask" + suffix + "&format=json", ART)[0] == 502


def test_ocr_paths_are_one_shot_cold_and_do_not_consume_file_buffer(bench, tmp_path):
    api, upstream, helper_log = bench
    suffix = session()
    image = tmp_path / "card with spaces.png"
    image.write_bytes(PNG)
    assert api.call("/v1/file" + suffix, str(image))[0] == 200
    assert api.call("/v1/ocr", "1")[0] == 200
    assert api.call("/v1/ocr", "'" + str(image) + "'")[:2] == (200, "one\n  two")
    assert api.call("/v1/ocr", "0")[0] == 200
    assert api.call("/v1/ocr", PNG, {"Content-Type": "image/png"})[:2] == (
        200,
        "one\n  two",
    )
    records = [json.loads(line) for line in helper_log.read_text().splitlines()]
    assert [record["engine"] for record in records] == ["paddle", "tesseract"]
    assert len({record["pid"] for record in records}) == 2
    assert all(record["bytes"] == len(PNG) for record in records)
    files = json.loads(api.call("/v1/file" + suffix)[1])["files"]
    assert len(files) == 1
    assert upstream.snapshot() == []


def process_alive(pid):
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes

        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.GetExitCodeProcess.argtypes = [
            wintypes.HANDLE,
            ctypes.POINTER(wintypes.DWORD),
        ]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.OpenProcess(0x1000, False, pid)  # QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        try:
            code = wintypes.DWORD()
            assert kernel.GetExitCodeProcess(handle, ctypes.byref(code))
            return code.value == 259  # STILL_ACTIVE
        finally:
            kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    return True


def test_ocr_early_exit_preserves_install_diagnostic_instead_of_path_error(bench):
    api, upstream, helper_log = bench
    # Enough input to exceed a pipe buffer; the fake helper exits after four bytes.
    status, text, _ = api.call(
        "/v1/ocr",
        b"FAIL" + b"x" * (1024 * 1024),
        {"Content-Type": "application/octet-stream"},
    )
    assert status == 503
    assert "fixture missing dependency" in text and "OCR extra" in text
    assert not helper_log.exists()
    assert upstream.snapshot() == []


def test_ocr_deadline_kills_and_reaps_child_before_return_then_releases_lane(bench):
    api, upstream, helper_log = bench
    status, _, _ = api.call(
        "/v1/ocr", b"SLOW", {"Content-Type": "application/octet-stream"}
    )
    assert status == 504
    records = [json.loads(line) for line in helper_log.read_text().splitlines()]
    assert len(records) == 1
    assert not process_alive(records[0]["pid"])
    assert api.call("/v1/ocr", PNG, {"Content-Type": "image/png"})[:2] == (
        200,
        "one\n  two",
    )
    assert upstream.snapshot() == []
