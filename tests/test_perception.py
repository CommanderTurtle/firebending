"""Opt-in regression fixtures: fake NInfer and OCR only; no model or screen capture.

These fixtures start the built daemon when pytest is explicitly run. Importing this
module does not start a server, subprocess, OCR engine, or inference request.
"""

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
import urllib.parse
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


def bbox_plan(objects):
    return tool("submit_look_plan", "look_json", json.dumps({"objects": objects}))


# Protocol fake only. The real renderer and cropper have their own fixtures below.
FAKE_ONESHOT = """
import base64, json, os, pathlib, sys, time

prefix = sys.stdin.buffer.read(4)
operation = sys.argv[sys.argv.index('--operation') + 1] if '--operation' in sys.argv else 'ocr'
engine = sys.argv[sys.argv.index('--engine') + 1] if '--engine' in sys.argv else None
if prefix == b'FAIL':
    print('fixture missing dependency: install OCR extra', file=sys.stderr)
    raise SystemExit(2)
if prefix == b'SLOW':
    with pathlib.Path(os.environ['OCR_FIXTURE_LOG']).open('a') as log:
        log.write(json.dumps({'pid': os.getpid(), 'engine': engine, 'bytes': 4}) + '\\n')
    time.sleep(30)
raw = prefix + sys.stdin.buffer.read()
with pathlib.Path(os.environ['OCR_FIXTURE_LOG']).open('a') as log:
    log.write(json.dumps({'pid': os.getpid(), 'engine': engine, 'bytes': len(raw)}) + '\\n')
if operation in ('bbox', 'crop'):
    stub = b'\\x89PNG\\r\\n\\x1a\\nfixture'
    out = sys.argv[sys.argv.index('--out') + 1] if '--out' in sys.argv else None
    if operation == 'bbox':
        if out:
            pathlib.Path(out).write_bytes(stub)
            print(json.dumps({'text': '1: fixture\\n', 'path': out, 'width': 1280, 'height': 720}))
        else:
            print(json.dumps({'text': '1: fixture\\n', 'image_base64': base64.b64encode(stub).decode(),
                              'width': 1280, 'height': 720}))
    else:
        boxes = json.loads(sys.argv[sys.argv.index('--boxes') + 1])
        crops = []
        for number, item in enumerate(boxes, start=1):
            entry = {'name': item.get('name') or '?', 'width': 100 + number, 'height': 50 + number}
            if out:
                path = f'{out}-{number}.png'
                pathlib.Path(path).write_bytes(stub)
                entry['path'] = path
            else:
                entry['image_base64'] = base64.b64encode(stub).decode()
            crops.append(entry)
        print(json.dumps({'text': '1: fixture\\n', 'width': 1280, 'height': 720, 'crops': crops}))
elif operation == 'cereal':
    level = int(sys.argv[sys.argv.index('--level') + 1])
    style = sys.argv[sys.argv.index('--style') + 1]
    print(json.dumps({'text': '  /\\\\  \\n /__\\\\ \\n      \\n', 'level': level, 'style': style, 'trimmed': False}))
else:
    print(json.dumps({'text': 'one\\n  two', 'engine': engine}))
"""


class Api:
    def __init__(self, url):
        self.url = url

    def call(self, path, body=None, headers=None, method=None, binary=False):
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
            raw = response.read()
            return response.status, raw if binary else raw.decode(), response.headers


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
    # Protocol fake only. Renderer mathematics has separate real-code fixtures.
    (package / "oneshot.py").write_text(FAKE_ONESHOT, encoding="utf-8")
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
    assert api.call("/v1/cereal")[1].startswith("1: 32p")
    assert api.call("/v1/jevset")[0] == 200
    for value in ("1", "0", "1"):
        assert api.call("/v1/ocr", value)[0] == 200
    for level in ("1", "2", "3"):
        assert api.call("/v1/cereal", level)[0] == 200
    time.sleep(0.1)
    assert upstream.snapshot() == []
    assert not helper_log.exists()


@pytest.mark.parametrize("path", ["/v1/jevset", "/v1/jevask", "/v1/ocr", "/v1/cereal"])
def test_perception_rejects_cross_site_and_rebound_hosts(bench, path):
    api, upstream, _ = bench
    assert api.call(path, "text", {"Origin": "https://untrusted.example"})[0] == 403
    assert api.call(path, "text", {"Host": "rebound.example"})[0] == 403
    assert not upstream.snapshot()


def test_refinement_needs_y_and_n_clears_draft_history_and_activation(bench):
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
    upstream.reply(ANSWER)
    status, output, _ = api.call("/v1/jevask" + suffix, ART)
    assert status == 200 and "ace_spades" in output and "0.875000" in output
    assert upstream.snapshot()[-1][0] == "/v1/systemone"
    assert upstream.snapshot()[-1][1]["state"] == ART
    calls_before_clear = upstream.snapshot()
    assert api.call("/v1/jevset" + suffix, "N")[0] == 200
    assert api.call("/v1/jevask" + suffix, ART)[0] == 409
    assert api.call("/v1/jevset" + suffix, "Y")[0] == 409
    status = api.call("/v1/jevset" + suffix)[1]
    cleared = json.loads(status.split("```json\n", 1)[1].rsplit("\n```", 1)[0])
    assert cleared == {"draft": None, "active": None, "turns": 0}
    assert upstream.snapshot() == calls_before_clear
    assert api.call("/v1/jevask" + session(), ART)[0] == 409


def test_clear_during_ask_discards_stale_result(bench):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    calls_before_ask = len(upstream.snapshot())
    gate = threading.Event()
    upstream.reply(ANSWER, gate=gate)
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/jevask" + suffix, ART)
        for _ in range(100):
            if len(upstream.snapshot()) > calls_before_ask:
                break
            time.sleep(0.01)
        assert len(upstream.snapshot()) > calls_before_ask
        assert api.call("/v1/jevset" + suffix, "N")[0] == 200
        gate.set()
        assert waiting.result()[0] == 409
    calls_after_clear = upstream.snapshot()
    assert api.call("/v1/jevask" + suffix, ART)[0] == 409
    assert upstream.snapshot() == calls_after_clear


def choice_answer(choice, probability, confidence):
    other = "unknown" if choice == "ace_spades" else "ace_spades"
    odds = {choice: probability, other: round(1 - probability, 6)}
    return {
        "answers": {
            "card": {
                "type": "choice",
                "choice": choice,
                "probabilities": {
                    "ace_spades": odds["ace_spades"],
                    "unknown": odds["unknown"],
                },
                "confidence": confidence,
            }
        }
    }


def test_batch_ask_asks_every_state_in_order_inside_one_hold(bench):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    calls_before = len(upstream.snapshot())
    upstream.reply(choice_answer("ace_spades", 0.9, 0.8))
    upstream.reply(choice_answer("unknown", 0.8, 0.7))
    status, text, _ = api.call(
        "/v1/jevask" + suffix + "&batch=1", [{"note": "first"}, {"note": "second"}]
    )
    assert status == 200
    assert "2 of 2 states answered" in text
    assert "### State 1/2" in text and "### State 2/2" in text
    first, second = text.split("### State 2/2")
    assert "| card | ace_spades | 0.900000 | 0.800000 |" in first
    assert "| card | unknown | 0.800000 | 0.700000 |" in second
    asked = upstream.snapshot()[calls_before:]
    assert [path for path, _ in asked] == ["/v1/systemone", "/v1/systemone"]
    assert [body["state"] for _, body in asked] == [
        {"note": "first"},
        {"note": "second"},
    ]


def test_batch_ask_json_keeps_per_state_native_answers(bench):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    upstream.reply(choice_answer("ace_spades", 0.9, 0.8))
    upstream.reply(choice_answer("unknown", 0.8, 0.7))
    status, text, _ = api.call("/v1/jevask" + suffix + "&batch=1&format=json", ["first", "second"])
    assert status == 200
    payload = json.loads(text)
    assert payload["answered"] == 2 and payload["failed"] == 0
    assert [item["state"] for item in payload["results"]] == ["first", "second"]
    assert [item["result"]["answers"]["card"]["choice"] for item in payload["results"]] == [
        "ace_spades",
        "unknown",
    ]


def test_batch_ask_marks_failed_states_without_fabricating(bench):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    upstream.reply(choice_answer("ace_spades", 0.9, 0.8))
    upstream.reply({"answers": {"card": {"type": "noul", "noul": 0.5}}})
    status, text, _ = api.call("/v1/jevask" + suffix + "&batch=1", ["a", "b"])
    assert status == 200 and "1 of 2 states answered" in text
    assert "Error: Jev answer type did not match the framework." in text
    assert "| card | ace_spades | 0.900000 | 0.800000 |" in text


def test_batch_ask_fails_wholly_when_every_state_fails(bench):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    for _ in range(2):
        upstream.reply({"error": "unavailable"}, status=500)
    status, text, _ = api.call("/v1/jevask" + suffix + "&batch=1", ["a", "b"])
    assert status == 502
    assert text.count("Error: NInfer returned HTTP 502: NInfer /v1/systemone returned HTTP 500") == 2


def test_batch_ask_validates_the_batch_before_the_first_native_call(bench):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    calls_before = upstream.snapshot()
    assert api.call("/v1/jevask" + suffix + "&batch=1", {"note": "object"})[0] == 400
    assert api.call("/v1/jevask" + suffix + "&batch=1", [])[0] == 400
    assert api.call("/v1/jevask" + suffix + "&batch=1", list(range(33)))[0] == 413
    assert api.call("/v1/jevask" + suffix + "&batch=1", "plain text")[0] == 400
    assert api.call("/v1/jevask" + suffix + "&batch=1", [{"note": "ok"}, 42])[0] == 400
    assert api.call("/v1/jevask" + suffix + "&batch=1", [True])[0] == 400
    assert upstream.snapshot() == calls_before


def test_batch_ask_caps_the_total_decision_count(bench):
    api, upstream, _ = bench
    suffix = session()
    wide = {
        "state": "observation supplied later",
        "questions": {
            f"q{n}": {"type": "noul", "instructions": "Is the rank legible?"} for n in range(9)
        },
    }
    upstream.reply(draft(wide))
    assert api.call("/v1/jevset" + suffix, "Nine legibility checks")[0] == 200
    assert api.call("/v1/jevset" + suffix, "Y")[0] == 200
    status, text, _ = api.call("/v1/jevask" + suffix + "&batch=1", [str(n) for n in range(32)])
    assert status == 413 and "at most 256 decisions" in text


def test_single_state_ask_shape_is_unchanged(bench):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    upstream.reply(ANSWER)
    status, text, _ = api.call("/v1/jevask" + suffix, ART)
    assert status == 200
    assert "| card | ace_spades | 0.875000 | 0.750000 |" in text
    assert "### State" not in text and "states answered" not in text


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


def test_cereal_is_local_raw_and_preserves_all_spaces_and_rows(bench):
    api, upstream, helper_log = bench
    assert api.call("/v1/cereal", "3")[0] == 200
    status, output, headers = api.call("/v1/cereal", PNG, {"Content-Type": "image/png"})
    assert status == 200 and output == "  /\\  \n /__\\ \n      \n"
    assert headers.get_content_type() == "text/plain"
    assert headers["X-Cereal-Level"] == "3"
    assert upstream.snapshot() == []
    record = json.loads(helper_log.read_text().splitlines()[-1])
    assert record["engine"] is None
    status, body, _ = api.call(
        "/v1/cereal?level=2&style=block&format=json", PNG, {"Content-Type": "image/png"}
    )
    assert status == 200
    result = json.loads(body)
    assert result["level"] == 2 and result["style"] == "block" and not result["trimmed"]
    assert upstream.snapshot() == []


def test_cereal_output_can_be_piped_unchanged_into_jevask(bench):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    status, art, _ = api.call("/v1/cereal", PNG, {"Content-Type": "image/png"})
    assert status == 200
    assert len(upstream.snapshot()) == 1  # Only the explicit jevset draft.
    upstream.reply(ANSWER)
    assert api.call("/v1/jevask" + suffix, art)[0] == 200
    assert upstream.snapshot()[-1][1]["state"] == art


def test_cereal_is_not_gated_on_a_busy_jev_model(bench):
    api, upstream, _ = bench
    suffix = session()
    activate(api, upstream, suffix)
    count = len(upstream.snapshot())
    gate = threading.Event()
    upstream.reply(ANSWER, gate=gate)
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/jevask" + suffix, ART)
        try:
            for _ in range(100):
                if len(upstream.snapshot()) > count:
                    break
                time.sleep(0.01)
            assert len(upstream.snapshot()) > count
            assert api.call("/v1/cereal", PNG, {"Content-Type": "image/png"})[0] == 200
            assert len(upstream.snapshot()) == count + 1
        finally:
            gate.set()
        assert waiting.result()[0] == 200


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


BOX_OBJECTS = [
    {
        "name": "GPU badge",
        "kind": "where",
        "instructions": "Where is the GPU badge in the picture?",
    },
    {
        "name": "llama logo",
        "kind": "isdoes",
        "instructions": "Does the picture contain the llama logo?",
    },
]


def box_answer(top, bottom, left, right, aggregate="mode"):
    """One native box answer whose four edges agree with its box, as the wire contract requires."""
    return {
        "type": "box",
        "aggregate": aggregate,
        "box": {"top": top, "bottom": bottom, "left": left, "right": right},
        "confidence": 0.5,
        "edges": {
            edge: {"probability": 0.4, "modal": value, "mean": value, "median": value, "moved": False}
            for edge, value in (("top", top), ("bottom", bottom), ("left", left), ("right", right))
        },
    }


def box_reply(top, bottom, left, right, presence=None, aggregate="mode"):
    answers = {"box": box_answer(top, bottom, left, right, aggregate)}
    if presence is not None:
        answers["presence"] = {"type": "noul", "noul": presence, "confidence": presence}
    return {"answers": answers}


def activate_plan(api, upstream, suffix, objects=BOX_OBJECTS, count=None):
    if count is not None:
        assert api.call("/v1/bboxset" + suffix, str(count))[0] == 200
    upstream.reply(bbox_plan(objects))
    assert api.call("/v1/bboxset" + suffix, "Find the things in the picture")[0] == 200
    assert api.call("/v1/bboxset" + suffix, "Y")[0] == 200


def picture(tmp_path, name="picture.png"):
    path = tmp_path / name
    path.write_bytes(PNG)
    return str(path)


def test_bbox_plan_lists_titles_and_gates_activation(bench, tmp_path):
    api, upstream, _ = bench
    suffix = session()
    assert api.call("/v1/bboxask" + suffix, picture(tmp_path))[0] == 409
    assert api.call("/v1/bboxset" + suffix, "Y")[0] == 409
    upstream.reply(bbox_plan(BOX_OBJECTS))
    status, text, _ = api.call("/v1/bboxset" + suffix, "Find the GPU badge and the llama logo")
    assert status == 200
    assert "1: GPU badge (region)" in text
    assert "2: llama logo (yes/no + region)" in text
    assert "Y starts looking" in text
    assert api.call("/v1/bboxask" + suffix, picture(tmp_path))[0] == 409
    assert '"objects":1' in api.call("/v1/bboxset" + suffix)[1]
    assert "the first 3 of these" in api.call("/v1/bboxset" + suffix, "3")[1]
    assert api.call("/v1/bboxset" + suffix, "9")[0] == 400
    assert api.call("/v1/bboxset" + suffix, "0")[0] == 400
    status, text, _ = api.call("/v1/bboxset" + suffix, "Y")
    assert status == 200 and "(the first 2)" in text
    assert api.call("/v1/bboxset" + suffix, "N")[0] == 200
    assert api.call("/v1/bboxset" + suffix, "Y")[0] == 409
    assert api.call("/v1/bboxask" + suffix, picture(tmp_path))[0] == 409


def test_bbox_ask_asks_one_state_per_object_with_the_picture(bench, tmp_path):
    api, upstream, _ = bench
    suffix = session()
    source = picture(tmp_path)
    activate_plan(api, upstream, suffix, count="2")
    upstream.reply(box_reply(0.69, 0.82, 0.09, 0.32))
    upstream.reply(box_reply(0.14, 0.63, 0.49, 0.6, presence=0.99))
    status, text, _ = api.call("/v1/bboxask" + suffix + "&crop=1", source)
    assert status == 200, text
    asked = [call for call in upstream.snapshot() if call[0] == "/v1/systemone"]
    assert len(asked) == 2, "one object is one native request"
    stored = asked[0][1]["images"]
    assert stored == asked[1][1]["images"], "one ask reuses one stored picture, so one cache key"
    assert Path(stored[0]).name.startswith("bbox-") and Path(stored[0]).read_bytes() == PNG
    assert list(asked[0][1]["questions"]) == ["box"]
    assert sorted(asked[1][1]["questions"]) == ["box", "presence"]
    assert "GPU badge" in asked[0][1]["state"] and "llama logo" in asked[1][1]["state"]
    assert "## What was found in the picture: 2 of 2" in text
    assert "| 1 | GPU badge | 0.69 | 0.82 | 0.09 | 0.32 | 0.4000 | 0.5000 |" in text
    assert "| 2 | llama logo | 0.14 | 0.63 | 0.49 | 0.60 | 0.4000 | 0.5000 | 0.9900 |" in text
    assert "Marked-up picture: " in text and "Cut-out: GPU badge" in text and "Cut-out: llama logo" in text
    printed = Path(text.split("Marked-up picture: ", 1)[1].split(" (", 1)[0])
    assert printed.read_bytes().startswith(b"\x89PNG")
    for line in text.splitlines():
        if line.startswith("Cut-out: "):
            assert Path(line.split("\u2192 ", 1)[1].split(" (", 1)[0]).read_bytes().startswith(b"\x89PNG")


def test_bbox_ask_json_keeps_the_picture_out_of_context_until_asked(bench, tmp_path):
    api, upstream, _ = bench
    suffix = session()
    source = picture(tmp_path)
    activate_plan(api, upstream, suffix, count="2")
    for _ in range(3):
        upstream.reply(box_reply(0.69, 0.82, 0.09, 0.32))
        upstream.reply(box_reply(0.14, 0.63, 0.49, 0.6, presence=0.99))
    status, text, headers = api.call("/v1/bboxask" + suffix + "&format=json&crop=1", source)
    assert status == 200, text
    assert headers.get_content_type() == "application/json"
    report = json.loads(text)
    assert (report["asked"], report["answered"], report["failed"]) == (2, 2, 0)
    assert report["objects"][0]["box"] == {"top": 0.69, "bottom": 0.82, "left": 0.09, "right": 0.32}
    assert report["objects"][0].get("presence") is None
    assert report["objects"][0].get("confidence") == 0.5
    assert report["objects"][1]["presence"] == 0.99
    assert report["objects"][1]["kind"] == "isdoes"
    stored = Path(report["picture"]["path"])
    assert stored.is_file() and stored.read_bytes() == PNG, "one canonical copy is what every renderer reads"
    assert "image_base64" not in report["annotated"], "the marked-up picture is gated, not printed"
    assert report["annotated"]["width"] == 1280
    assert len(report["crops"]) == 2 and "image_base64" not in report["crops"][0]
    status, text, _ = api.call("/v1/bboxask" + suffix + "&format=json&inline=1", source)
    assert json.loads(text)["annotated"]["image_base64"], "inline=1 opts into the bytes"
    status, raw, headers = api.call("/v1/bboxask" + suffix + "&format=png", source, binary=True)
    assert status == 200 and headers.get_content_type() == "image/png"
    assert raw.startswith(b"\x89PNG"), "format=png returns the box print itself"
    assert api.call("/v1/bboxask" + suffix + "&format=png&annotate=0", source)[0] == 400


def test_bbox_ask_records_a_refused_box_without_fabricating(bench, tmp_path):
    api, upstream, _ = bench
    suffix = session()
    source = picture(tmp_path)
    activate_plan(api, upstream, suffix, count="2")
    upstream.reply(box_reply(0.9, 0.2, 0.1, 0.5))  # an inverted box the native side never returns
    upstream.reply(box_reply(0.14, 0.63, 0.49, 0.6, presence=0.99))
    status, text, _ = api.call("/v1/bboxask" + suffix + "&format=json", source)
    assert status == 200, text
    report = json.loads(text)
    assert (report["answered"], report["failed"]) == (1, 1)
    assert "inverted" in report["objects"][0]["error"]
    assert "Failed: 1: GPU badge" in api.call("/v1/bboxask" + suffix, source)[1]
    assert report["objects"][1]["box"]["left"] == 0.49


def test_bbox_control_setting_limits_the_titles_asked(bench, tmp_path):
    api, upstream, _ = bench
    suffix = session()
    source = picture(tmp_path)
    activate_plan(api, upstream, suffix, count="1")
    upstream.reply(box_reply(0.69, 0.82, 0.09, 0.32))
    status, text, _ = api.call("/v1/bboxask" + suffix, source)
    assert status == 200, text
    assert [call for call in upstream.snapshot() if call[0] == "/v1/systemone"].__len__() == 1
    assert "## What was found in the picture: 1 of 1" in text


def test_box_frameworks_carry_the_picture_with_every_state(bench, tmp_path):
    api, upstream, _ = bench
    suffix = session()
    source = picture(tmp_path)
    framework = {
        "state": "observation supplied later",
        "images": [source],
        "questions": {"where4090": {"type": "box", "instructions": "Where are the digits 4090?"}},
    }
    upstream.reply(draft(framework))
    assert api.call("/v1/jevset" + suffix, "Where are the digits 4090?")[0] == 200
    assert api.call("/v1/jevset" + suffix, "Y")[0] == 200
    upstream.reply({"answers": {"where4090": box_answer(0.69, 0.82, 0.09, 0.32)}})
    upstream.reply({"answers": {"where4090": box_answer(0.1, 0.3, 0.2, 0.5)}})
    status, text, _ = api.call("/v1/jevask" + suffix + "&batch=1", ["first picture", "second picture"])
    assert status == 200, text
    asked = [call for call in upstream.snapshot() if call[0] == "/v1/systemone"]
    assert len(asked) == 2
    assert [call[1]["images"] for call in asked] == [[source], [source]]
    assert [call[1]["state"] for call in asked] == ["first picture", "second picture"]
    assert "as mode" in text
    # A framework with no picture sends no images key at all, never an explicit null.
    other = session()
    upstream.reply(draft())
    assert api.call("/v1/jevset" + other, "Identify playing cards")[0] == 200
    assert api.call("/v1/jevset" + other, "Y")[0] == 200
    upstream.reply(ANSWER)
    assert api.call("/v1/jevask" + other, ART)[0] == 200
    assert "images" not in [call for call in upstream.snapshot() if call[0] == "/v1/systemone"][-1][1]


def test_box_questions_are_refused_without_a_picture(bench):
    api, upstream, _ = bench
    suffix = session()
    framework = {
        "state": "observation supplied later",
        "questions": {"where4090": {"type": "box", "instructions": "Where are the digits 4090?"}},
    }
    for _ in range(3):
        upstream.reply(draft(framework))
    status, text, _ = api.call("/v1/jevset" + suffix, "Where are the digits 4090?")
    assert status == 502
    assert "send images" in text
    assert len([call for call in upstream.snapshot() if call[0] == "/v1/chat/completions"]) == 3


def test_bboxcrop_crops_by_fraction_and_pixels(bench, tmp_path):
    api, _, _ = bench
    source = picture(tmp_path)
    boxes = json.dumps([{"name": "cta", "box": {"top": 0.68, "bottom": 0.82, "left": 0.09, "right": 0.32}}])
    status, text, _ = api.call(
        f"/v1/bboxcrop?format=json&pad=0.05&boxes={urllib.parse.quote(boxes, safe='')}", source
    )
    assert status == 200, text
    report = json.loads(text)
    assert report["cropped"] == 1 and report["pad"] == 0.05
    crop = Path(report["crops"][0]["path"])
    assert crop.exists() and crop.read_bytes().startswith(b"\x89PNG")
    assert "image_base64" not in report["crops"][0]
    # The slips a model actually makes are repaired, not refused: one object where an array was
    # asked for, a coordinate written as a string.
    single = urllib.parse.quote('{"name": "cta", "box": {"top": 0.68, "bottom": 0.82, "left": "0.09", "right": 0.32}}', safe="")
    status, text, _ = api.call(f"/v1/bboxcrop?format=json&boxes={single}", source)
    assert status == 200, text
    assert json.loads(text)["cropped"] == 1
    # What is left over names the field and what was wrong with it.
    partial = urllib.parse.quote('{"box": {"bottom": 1, "left": 0, "right": 0}}', safe="")
    assert "top is missing" in api.call(f"/v1/bboxcrop?format=json&boxes={partial}", source)[1]
    nonsense = urllib.parse.quote(json.dumps([{"box": {"top": "x", "bottom": 1, "left": 0, "right": 0}}]), safe="")
    assert "top must be a fraction" in api.call(f"/v1/bboxcrop?format=json&boxes={nonsense}", source)[1]
    status, _, headers = api.call(
        "/v1/bboxcrop?top=496&bottom=590&left=115&right=410&name=pixels", source, binary=True
    )
    assert status == 200 and headers.get_content_type() == "image/png"
    assert api.call("/v1/bboxcrop?top=abc&bottom=0.8&left=0.1&right=0.3", source)[0] == 400
    assert api.call("/v1/bboxcrop?boxes=notjson", source)[0] == 400
    assert "boxes names no object" in api.call("/v1/bboxcrop?boxes=%5B%5D", source)[1]


def test_a_broken_draft_is_reprompted_not_refused(bench):
    """The model's own broken JSON costs a reprompt, not a failed request."""
    api, upstream, _ = bench
    upstream.reply(tool("submit_look_plan", "look_json", '{"objects": [{"name": "GPU badge"'))
    upstream.reply(bbox_plan(BOX_OBJECTS))
    status, text, _ = api.call("/v1/bboxset" + session(), "Find the GPU badge and the llama logo")
    assert status == 200, text
    assert "1: GPU badge (region)" in text
    drafting = [call for call in upstream.snapshot() if call[0] == "/v1/chat/completions"]
    assert len(drafting) == 2, "the second attempt carries the complaint about the first"
    assert "not valid JSON" in json.dumps(drafting[1][1])


def test_a_hopeless_draft_fails_without_losing_the_session(bench):
    api, upstream, _ = bench
    suffix = session()
    for _ in range(3):
        upstream.reply(tool("submit_look_plan", "look_json", "{}"))
    status, text, _ = api.call("/v1/bboxset" + suffix, "Find the things in the picture")
    assert status == 502, text
    assert "three attempts" in text
    assert api.call("/v1/bboxset" + suffix, "Y")[0] == 409
    upstream.reply(bbox_plan(BOX_OBJECTS))
    assert api.call("/v1/bboxset" + suffix, "Find the GPU badge")[0] == 200
