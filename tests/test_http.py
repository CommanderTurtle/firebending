"""Headless integration bench: real daemon, synthetic model replies, no capture."""

import base64
import concurrent.futures
import json
import os
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]


class Api:
    def __init__(self, url):
        self.url = url

    def call(self, path, body=None, method=None, headers=None, timeout=8):
        headers = dict(headers or {})
        if body is not None and not isinstance(body, (str, bytes)):
            body = json.dumps(body, ensure_ascii=False)
            headers.setdefault("Content-Type", "application/json")
        if isinstance(body, str):
            body = body.encode("utf-8")
        request = urllib.request.Request(self.url + path, body, headers, method=method)
        try:
            response = urllib.request.urlopen(request, timeout=timeout)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.read().decode("utf-8"), response.headers

    def json(self, path):
        status, body, _ = self.call(path)
        assert status == 200, body
        return json.loads(body)

    def pending(self, session_path=None):
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            entries = self.json("/v1/pending")["pending"]
            if entries:
                return entries[0]["request_id"]
            time.sleep(0.02)
        raise AssertionError("Request never became pending")

    def reply(self, request_id, **content):
        if request_id.startswith("msg-"):
            assert self.call("/v1/requests/" + request_id, content)[0] == 202
            return
        status, body, _ = self.call("/v1/buffer", {"request_id": request_id, **content})
        assert status == 200, body
        assert json.loads(body)["matched_request_id"] == request_id


@pytest.fixture(scope="module")
def api(tmp_path_factory):
    directory = tmp_path_factory.mktemp("firebending-http")
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    client = Api(f"http://127.0.0.1:{port}")
    env = dict(
        os.environ,
        OPENAI_BUFFER_Daemon__Url=client.url,
        OPENAI_BUFFER_Daemon__DataDirectory=str(directory),
        OPENAI_BUFFER_Daemon__RequestTimeoutSeconds="1",
        OPENAI_BUFFER_Daemon__MessageTimeoutSeconds="2",
    )
    dll = ROOT / "src/OpenAiBufferDaemon/bin/Release/net10.0/OpenAiBufferDaemon.dll"
    assert dll.is_file(), "Build the daemon in Release first"
    with (directory / "daemon.log").open("wb") as log:
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
                    if client.json("/health")["ok"]:
                        break
                except (OSError, AssertionError):
                    if process.poll() is not None:
                        raise AssertionError((directory / "daemon.log").read_text())
                    time.sleep(0.05)
            else:
                raise AssertionError("Daemon did not start")
            yield client
        finally:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


@pytest.fixture
def session():
    return "?session=" + uuid.uuid4().hex


@pytest.mark.parametrize(
    "body,expected",
    [
        ("# Hello\n\nWorld", "# Hello\n\nWorld"),
        ({"choices": [{"message": {"content": "First\nSecond"}}]}, "First\nSecond"),
        ({"role": "assistant", "content": "First\\nSecond"}, "First\nSecond"),
        (
            {
                "content": [
                    {"type": "text", "text": "One"},
                    {"type": "text", "text": " two"},
                ]
            },
            "One two",
        ),
        (json.dumps(json.dumps({"content": "nested\nJSON"})), "nested\nJSON"),
        ({"content": "```python\nprint('\\n')\n```"}, "```python\nprint('\\n')\n```"),
        ({"content": r"Use C:\new\folder"}, r"Use C:\new\folder"),
        (
            {"content": '{"this": "is the requested JSON answer"}'},
            '{"this": "is the requested JSON answer"}',
        ),
        ({"text": {"value": "One entry"}}, "One entry"),
        ("Turtle 🐢 — café", "Turtle 🐢 — café"),
    ],
)
def test_format_accepts_envelopes_entries_and_nested_strings(api, body, expected):
    status, text, headers = api.call("/v1/format", body)
    assert status == 200, text
    assert text == expected
    assert headers.get_content_type() == "text/markdown"


@pytest.mark.parametrize(
    "body",
    [
        {
            "choices": [
                {
                    "message": {
                        "reasoning_content": "Consider\nthis",
                        "content": "# Answer\n\n42",
                    }
                }
            ]
        },
        {
            "content": [
                {"type": "thinking", "thinking": "Consider\nthis"},
                {"type": "text", "text": "# Answer\n\n42"},
            ]
        },
        {
            "output": [
                {"type": "reasoning", "summary": [{"text": "Consider\nthis"}]},
                {
                    "type": "message",
                    "content": [{"type": "output_text", "text": "# Answer\n\n42"}],
                },
            ]
        },
        "<think>Consider\nthis</think>\n# Answer\n\n42",
    ],
)
def test_reasoning_is_a_disclosure_not_json(api, body):
    status, text, _ = api.call("/v1/format", body)
    assert status == 200, text
    assert (
        text
        == "<details>\n<summary>Reasoning</summary>\n\nConsider\nthis\n\n</details>\n\n# Answer\n\n42"
    )


def test_format_modes_and_errors_do_not_echo_unknown_json(api):
    assert api.call("/v1/format?escapes=off", r"One\nTwo")[1] == r"One\nTwo"
    assert api.call("/v1/format?escapes=literal", r"`One\nTwo`")[1] == "`One\nTwo`"
    assert api.call("/v1/format?escapes=invalid", "text")[0] == 400
    assert api.call("/v1/format", {"arbitrary": "not a model response"})[0] == 400
    assert api.call("/v1/format", {"error": {"message": "model unavailable"}})[0] == 400


def test_format_has_no_reply_truncation(api):
    reply = "line 🐢\n" * 100000
    assert api.call("/v1/format", {"content": reply})[:2] == (200, reply)


def test_message_waits_independently_and_clears_only_consumed_paths(
    api, session, tmp_path
):
    file = tmp_path / "notes with spaces.md"
    file.write_text("before send", encoding="utf-8")
    assert api.call("/v1/file" + session, "'" + str(file) + "'")[0] == 200
    file.write_text("read at send 🐢", encoding="utf-8")
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/message" + session, "Explain")
        request_id = api.pending()
        request = api.json("/v1/requests/" + request_id)["body"]
        assert "read at send 🐢" in request["messages"][-1]["content"][1]["text"]
        assert not any(
            key in request for key in ("tools", "max_tokens", "max_completion_tokens")
        )
        latest = api.json("/v1/requests/latest")["body"]
        assert "read at send" not in json.dumps(latest)
        assert str(file) not in json.dumps(latest)
        assert latest["request_url"].endswith(request_id)
        # New files staged during the wait belong to the following message.
        assert api.call("/v1/file" + session, str(file))[0] == 200
        assert api.call("/v1/message" + session, "second overlapping message")[0] == 409
        time.sleep(1.1)  # The message deadline is independent of legacy chat's 1s.
        assert not waiting.done()
        api.reply(request_id, reasoning_content="Think", content="**Done**\n\nComplete")
        status, response, _ = waiting.result()
    assert status == 200, response
    assert response.endswith("**Done**\n\nComplete")
    assert len(api.json("/v1/file" + session)["files"]) == 1
    assert api.call("/v1/file" + session, method="DELETE")[0] == 200


@pytest.mark.parametrize(
    "suffix,data,part_type,mime",
    [
        (".unknown", b"\x89PNG\r\n\x1a\nimage fixture", "image_url", "image/png"),
        (".pdf", b"%PDF-1.7\nfixture", "file", "application/pdf"),
        (".bin", b"\x00\x01\x02\xff", "file", "application/octet-stream"),
        (".wav", b"RIFF0000WAVEfixture", "input_audio", "audio/wav"),
    ],
)
def test_attachments_have_real_mime_and_native_content_parts(
    api, session, tmp_path, suffix, data, part_type, mime
):
    path = tmp_path / ("attachment" + suffix)
    path.write_bytes(data)
    assert api.call("/v1/file" + session, '"' + str(path) + '"')[0] == 200
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/message" + session, "Read this")
        request_id = api.pending()
        part = api.json("/v1/requests/" + request_id)["body"]["messages"][0]["content"][
            1
        ]
        assert part["type"] == part_type
        encoded = base64.b64encode(data).decode()
        if part_type == "image_url":
            assert part["image_url"]["url"] == f"data:{mime};base64,{encoded}"
        elif part_type == "file":
            assert part["file"]["file_data"] == f"data:{mime};base64,{encoded}"
        else:
            assert part["input_audio"] == {"format": "wav", "data": encoded}
        api.reply(request_id, content="accepted")
        assert waiting.result()[0] == 200
    assert api.json("/v1/file" + session)["files"] == []


def test_missing_attachment_and_bad_model_reply_preserve_the_buffer(
    api, session, tmp_path
):
    path = tmp_path / "later.txt"
    path.write_text("text", encoding="utf-8")
    assert api.call("/v1/file" + session, str(path))[0] == 200
    path.unlink()
    assert api.call("/v1/message" + session, "hello")[0] == 400
    assert len(api.json("/v1/file" + session)["files"]) == 1
    path.write_text("back", encoding="utf-8")
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/message" + session, "hello")
        request_id = api.pending()
        api.reply(request_id, choices=[])
        assert waiting.result()[0] == 502
    assert len(api.json("/v1/file" + session)["files"]) == 1


def test_multichat_replays_native_roles_and_off_clears_it(api, session):
    assert api.call("/v1/multichat" + session, "1")[0] == 200
    with concurrent.futures.ThreadPoolExecutor() as pool:
        first = pool.submit(api.call, "/v1/message" + session, "Hello")
        request_id = api.pending()
        api.reply(request_id, content="Hi", reasoning_content="Think first")
        assert first.result()[0] == 200
        second = pool.submit(api.call, "/v1/message" + session, "Continue")
        request_id = api.pending()
        messages = api.json("/v1/requests/" + request_id)["body"]["messages"]
        assert [item["role"] for item in messages] == ["user", "assistant", "user"]
        assert messages[1]["content"] == "Hi"
        assert "<details>" not in json.dumps(messages)
        assert api.call("/v1/multichat" + session, "0")[0] == 200
        # Even enabling it again must not resurrect the in-flight old context.
        assert api.call("/v1/multichat" + session, "1")[0] == 200
        api.reply(request_id, content="Old reply")
        assert second.result()[0] == 200
    assert "messages: 0" in api.call("/v1/multichat" + session)[1]
    assert api.call("/v1/multichat" + session, "2")[0] == 400


def test_default_messages_remain_single_turn(api, session):
    with concurrent.futures.ThreadPoolExecutor() as pool:
        for prompt in ("First", "Second"):
            waiting = pool.submit(api.call, "/v1/message" + session, prompt)
            request_id = api.pending()
            assert len(api.json("/v1/requests/" + request_id)["body"]["messages"]) == 1
            api.reply(request_id, content="Reply")
            assert waiting.result()[0] == 200


def test_disconnected_client_is_removed_and_files_are_retained(api, session, tmp_path):
    path = tmp_path / "retain.txt"
    path.write_text("retain", encoding="utf-8")
    assert api.call("/v1/file" + session, str(path))[0] == 200
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/message" + session, "Wait", timeout=0.4)
        request_id = api.pending()
        with pytest.raises((TimeoutError, OSError)):
            waiting.result()
    for _ in range(100):
        if api.call("/v1/requests/" + request_id)[0] == 404:
            break
        time.sleep(0.02)
    else:
        raise AssertionError("Disconnected request stayed in the queue")
    assert len(api.json("/v1/file" + session)["files"]) == 1


def test_unknown_buffer_id_cannot_complete_another_chat(api):
    body = {
        "model": "manual-buffer-model",
        "messages": [{"role": "user", "content": "legacy"}],
    }
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/chat/completions", body)
        request_id = api.pending()
        assert api.json("/v1/requests/latest")["body"] == body
        status, response, _ = api.call(
            "/v1/buffer", {"request_id": "stale-id", "content": "wrong"}
        )
        assert status == 200
        assert json.loads(response).get("matched_request_id") is None
        assert not waiting.done()
        response = {
            "choices": [{"message": {"role": "assistant", "content": "right"}}],
            "custom_metadata": 42,
        }
        api.reply(request_id, **response)
        assert json.loads(waiting.result()[1]) == {"request_id": request_id, **response}


def test_legacy_oldest_buffer_and_sse_contracts_are_unchanged(api):
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(
            api.call, "/v1/chat/completions", {"stream": True, "messages": []}
        )
        api.pending()
        assert api.call("/v1/buffer", {"content": "streamed"})[0] == 200
        status, response, headers = waiting.result()
        assert status == 200
        assert headers.get_content_type() == "text/event-stream"
        assert "streamed" in response
        assert "data: [DONE]" in response


@pytest.mark.parametrize(
    "path",
    [
        "/v1/file",
        "/v1/message",
        "/v1/format",
        "/v1/multichat",
        "/v1/delimiter",
        "/v1/check",
        "/v1/requests/fake",
    ],
)
def test_new_endpoints_reject_cross_site_requests(api, path):
    body = None if path.startswith("/v1/requests/") else "text"
    assert (
        api.call(path, body, headers={"Origin": "https://untrusted.example"})[0] == 403
    )
    assert api.call(path, body, headers={"Host": "rebound.example"})[0] == 403


def test_bounded_timeout_and_safe_retry_keep_files_and_reject_late_replies(
    api, session, tmp_path
):
    file = tmp_path / "retry.txt"
    file.write_text("retry me", encoding="utf-8")
    assert api.call("/v1/file" + session, str(file))[0] == 200
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/message" + session, "No model reply")
        old_id = api.pending()
        status, text, headers = waiting.result()
        assert status == 504, text
        assert headers["Retry-After"] == "1"
        assert api.json("/v1/pending")["pending_count"] == 0
        assert len(api.json("/v1/file" + session)["files"]) == 1
        retry = pool.submit(api.call, "/v1/message" + session, "Retry")
        new_id = api.pending()
        assert new_id != old_id
        response = api.call("/v1/buffer", {"request_id": old_id, "content": "late"})
        assert json.loads(response[1]).get("matched_request_id") is None
        assert not retry.done()
        api.reply(new_id, content="Recovered")
        assert retry.result()[:2] == (200, "Recovered")
    assert api.json("/v1/file" + session)["files"] == []


def test_delimiter_last_answer_is_one_use_and_reasoning_is_not_captured(api, session):
    assert api.call("/v1/delimiter" + session, "HERE")[0] == 200
    table = "| Key | Value |\n| --- | --- |\n| 🐢 | 42 |"
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/message" + session, "A final table")
        request_id = api.pending()
        api.reply(
            request_id,
            reasoning_content="HERE\nNot the result\nHERE",
            content="Intro\nHERE\n" + table + "\nHERE\nAfter",
        )
        status, text, _ = waiting.result()
        assert status == 200
        assert text.endswith("\nHERE\nAfter")  # Full reply is still delivered.
    assert api.call("/v1/check" + session)[:2] == (200, table)
    assert api.call("/v1/check" + session)[0] == 404
    assert api.call("/v1/delimiter" + session, "0")[0] == 200
    assert api.call("/v1/check" + session)[0] == 409


def test_manual_check_pipe_preserves_indentation_and_ignores_reasoning(api, session):
    assert api.call("/v1/delimiter" + session, "HERE")[0] == 200
    body = "<details>\n<summary>Reasoning</summary>\n\nHERE\nwrong\nHERE\n</details>\n\nHERE\n    indented\n\nnext\nHERE"
    assert api.call("/v1/check" + session, body)[:2] == (200, "    indented\n\nnext")
    assert api.call("/v1/check" + session, "prefix HEREinline\nresultHERE suffix")[
        :2
    ] == (200, "inline\nresult")
    assert api.call("/v1/check" + session, "HERE but never closed")[0] == 404


def test_delimiter_clear_cannot_be_undone_by_an_old_inflight_reply(api, session):
    assert api.call("/v1/delimiter" + session, "HERE")[0] == 200
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/message" + session, "wait")
        request_id = api.pending()
        assert api.call("/v1/delimiter" + session, "0")[0] == 200
        assert api.call("/v1/delimiter" + session, "HERE")[0] == 200
        api.reply(request_id, content="HERE\nstale\nHERE")
        assert waiting.result()[0] == 200
    assert api.call("/v1/check" + session)[0] == 404


def test_format_accepts_a_curl_sse_capture(api):
    body = ': heartbeat\n\ndata: {"choices":[{"delta":{"reasoning_content":"Reason"}}]}\n\ndata: {"choices":[{"delta":{"content":"Final\\nanswer"}}]}\n\ndata: [DONE]\n\n'
    status, text, _ = api.call("/v1/format", body)
    assert status == 200
    assert "Reason" in text
    assert text.endswith("Final\nanswer")


def test_new_endpoints_allow_same_origin_and_do_not_enable_wildcard_cors(api):
    status, text, headers = api.call("/v1/format", "hello", headers={"Origin": api.url})
    assert (status, text) == (200, "hello")
    assert headers.get("Access-Control-Allow-Origin") != "*"


def test_format_preserves_json_answers_even_with_content_and_text_keys(api):
    answer = '{"content":"requested JSON", "text":"not an envelope"}'
    assert api.call("/v1/format", {"content": answer})[:2] == (200, answer)
    assert api.call("/v1/format", "42")[:2] == (200, "42")


def test_utf16_text_attachment_and_private_plain_reply(api, session, tmp_path):
    path = tmp_path / "notes.txt"
    path.write_bytes("Unicode 🐢\nsecond line".encode("utf-16"))
    assert api.call("/v1/file" + session, str(path))[0] == 200
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/message" + session, "read")
        request_id = api.pending()
        part = api.json("/v1/requests/" + request_id)["body"]["messages"][0]["content"][
            1
        ]
        assert "Unicode 🐢\nsecond line" in part["text"]
        assert (
            api.call(
                "/v1/requests/" + request_id, "**Plain reply**\n\nNo JSON wrapper"
            )[0]
            == 202
        )
        assert waiting.result()[:2] == (200, "**Plain reply**\n\nNo JSON wrapper")
        assert api.call("/v1/requests/" + request_id, "duplicate callback")[0] == 404


def test_invalid_message_format_option_never_queues_a_request(api, session):
    assert api.call("/v1/message" + session + "&escapes=invalid", "hello")[0] == 400
    assert api.json("/v1/pending")["pending_count"] == 0


def test_message_without_markers_retires_an_old_capture(api, session):
    assert api.call("/v1/delimiter" + session, "HERE")[0] == 200
    with concurrent.futures.ThreadPoolExecutor() as pool:
        for reply in ("HERE\nold table\nHERE", "No table in this answer"):
            waiting = pool.submit(api.call, "/v1/message" + session, "next")
            request_id = api.pending()
            api.reply(request_id, content=reply)
            assert waiting.result()[0] == 200
    assert api.call("/v1/check" + session)[0] == 404


def test_format_repairs_only_bare_json_string_controls(api):
    malformed = '{"content":"First\n\tSecond", "reasoning_content":"Why\nthere"}'
    status, text, _ = api.call("/v1/format", malformed)
    assert status == 200, text
    assert "Why\nthere" in text
    assert text.endswith("First\n\tSecond")
    # Repair must not double-decode paths or replace text in a valid code field.
    body = {"content": "```json\n" + r'{"path":"C:\\new\\file"}' + "\n```"}
    assert api.call("/v1/format", body)[:2] == (200, body["content"])


def test_plain_sse_callback_uses_the_shared_formatter(api, session):
    capture = 'data: {"choices":[{"delta":{"content":"One\\n"}}]}\n\ndata: {"choices":[{"delta":{"content":"two"}}]}\n\ndata: [DONE]\n\n'
    with concurrent.futures.ThreadPoolExecutor() as pool:
        waiting = pool.submit(api.call, "/v1/message" + session, "reply")
        request_id = api.pending()
        assert api.call("/v1/requests/" + request_id, capture)[0] == 202
        assert waiting.result()[:2] == (200, "One\ntwo")
