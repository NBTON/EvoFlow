import asyncio
import json
import sys
from pathlib import Path

import httpx
import pytest
from evoflow_worker.document_handler import DocumentRequest, handle
from evoflow_worker.local_tools import NativeTools, agent_turn
from evoflow_worker.models import ModelError, new_metrics
from evoflow_worker.providers import MultiProvider


@pytest.mark.parametrize(
    "fmt,edit,expected",
    [
        ("docx", {"operation": "replace_text", "find": "Original", "text": "Revised"}, "Revised"),
        (
            "pptx",
            {"operation": "set_slide", "index": 0, "title": "Revised", "text": "Edited slide"},
            "Edited slide",
        ),
        ("xlsx", {"operation": "set_cell", "sheet": "Sheet1", "cell": "B2", "value": "Revised"}, "Revised"),
        ("pdf", {"operation": "append_page", "title": "Revised", "text": "Edited PDF"}, "Edited PDF"),
    ],
)
def test_document_create_edit_and_independent_reopen(tmp_path, fmt, edit, expected):
    path = str(tmp_path / ("result." + fmt))
    original = handle(
        DocumentRequest(
            operation="create", format=fmt, path=path, title="Original", paragraphs=["Original body"]
        )
    )
    assert original["verified"] and original["bytes"] > 100
    edited = handle(DocumentRequest(operation="edit", format=fmt, path=path, edits=[edit]))
    assert edited["verified"] and original["sha256"] != edited["sha256"]
    inspected = handle(DocumentRequest(operation="inspect", format=fmt, path=path))
    assert expected in json.dumps(inspected)
    if fmt == "docx":
        from docx import Document

        assert Document(path).paragraphs[0].text == "Revised"
    elif fmt == "xlsx":
        from openpyxl import load_workbook

        assert load_workbook(path).active["B2"].value == "Revised"
    elif fmt == "pdf":
        import pdfplumber

        with pdfplumber.open(path) as pdf:
            assert len(pdf.pages) == 2


def test_document_validation_formula_injection_and_atomic_failure(tmp_path):
    path = str(tmp_path / "safe.xlsx")
    handle(
        DocumentRequest(
            operation="create",
            format="xlsx",
            path=path,
            sheets=[{"name": "Sheet1", "rows": [['=HYPERLINK("https://example.com")']]}],
        )
    )
    from openpyxl import load_workbook

    assert load_workbook(path).active["A1"].data_type == "s"
    before = Path(path).read_bytes()
    with pytest.raises(ValueError):
        handle(
            DocumentRequest(
                operation="edit",
                format="xlsx",
                path=path,
                edits=[{"operation": "set_cell", "sheet": "missing", "value": "bad"}],
            )
        )
    assert Path(path).read_bytes() == before
    with pytest.raises(ValueError):
        DocumentRequest.model_validate(
            {"operation": "create", "format": "pdf", "path": "x.pdf", "code": "bad"}
        )


async def test_bridge_validates_delivery_nonce_and_cancellation():
    tools = NativeTools()
    assert not tools.schemas()
    tools.workspace = "C:\\EvoFlow Files"
    rejected = await tools.execute("run", "application_launch", {"application": "chrome", "approved": True})
    assert rejected["status"] == "failed" and not tools.pending()
    task = asyncio.create_task(tools.execute("run", "application_launch", {"application": "chrome"}))
    await asyncio.sleep(0)
    call = tools.pending()[0]
    result = {
        "status": "succeeded",
        "verified": True,
        "summary": "Chrome window available",
        "data": {"pid": 5},
    }
    with pytest.raises(ValueError):
        tools.finish(call["callId"], "wrong", result)
    tools.finish(call["callId"], call["deliveryToken"], result)
    assert (await task)["verified"]
    with pytest.raises(ValueError):
        tools.finish(call["callId"], call["deliveryToken"], result)
    canceled = asyncio.create_task(
        tools.execute("run", "filesystem", {"operation": "write", "path": "x.txt", "content": "sensitive"})
    )
    await asyncio.sleep(0)
    canceled.cancel()
    with pytest.raises(asyncio.CancelledError):
        await canceled
    assert tools.pending()[-1]["status"] == "canceled"
    assert "sensitive" not in json.dumps(tools.pending())


async def test_agent_multi_step_result_delivery_failure_and_iteration_limit():
    class Tools:
        workspace = "C:\\EvoFlow Files"

        def schemas(self):
            return [
                {"type": "function", "function": {"name": "application_launch"}},
                {"type": "function", "function": {"name": "filesystem"}},
            ]

        async def execute(self, run, name, arguments):
            return {"status": "failed", "verified": False, "summary": "Application unavailable"}

    class Model:
        def __init__(self):
            self.messages = []

        async def chat_step(self, messages, metrics, schemas, **kwargs):
            self.messages.append(list(messages))
            if messages[-1]["role"] == "tool":
                assert not json.loads(messages[-1]["content"])["verified"]
                return {"content": "Chrome could not be opened.", "routing": {}}
            return {
                "content": None,
                "routing": {},
                "tool_calls": [
                    {
                        "id": "c",
                        "type": "function",
                        "function": {"name": "application_launch", "arguments": '{"application":"chrome"}'},
                    }
                ],
            }

    model = Model()
    response, evidence = await agent_turn(
        model, [{"role": "user", "content": "Open Chrome"}], new_metrics(), Tools(), "r", lambda *a, **k: None
    )
    assert "could not" in response["content"] and evidence[0]["status"] == "failed"
    assert len(model.messages) == 2

    class Loop(Model):
        async def chat_step(self, *a, **k):
            return {
                "content": None,
                "routing": {},
                "tool_calls": [
                    {"id": "c", "type": "function", "function": {"name": "filesystem", "arguments": "{}"}}
                ],
            }

    with pytest.raises(ModelError, match="limit"):
        await agent_turn(Loop(), [], new_metrics(), Tools(), "r", lambda *a, **k: None)


async def test_stream_tool_fragments_are_assembled_only_for_tools_enabled():
    chunks = [
        {
            "choices": [
                {
                    "index": 0,
                    "delta": {
                        "tool_calls": [
                            {
                                "index": 0,
                                "id": "c1",
                                "function": {"name": "application_launch", "arguments": '{"app'},
                            }
                        ]
                    },
                    "finish_reason": None,
                }
            ]
        },
        {
            "choices": [
                {
                    "index": 0,
                    "delta": {"tool_calls": [{"index": 0, "function": {"arguments": 'lication":"chrome"}'}}]},
                    "finish_reason": "tool_calls",
                }
            ],
            "usage": {"prompt_tokens": 5, "completion_tokens": 2},
        },
    ]
    wire = "".join("data: " + json.dumps(c) + "\n\n" for c in chunks) + "data: [DONE]\n\n"
    response = httpx.Response(200, text=wire)
    result = await MultiProvider._read_stream(response, allow_tools=True)
    assert json.loads(result["choices"][0]["message"]["tool_calls"][0]["function"]["arguments"]) == {
        "application": "chrome"
    }
    with pytest.raises(ModelError, match="bounded plan"):
        await MultiProvider._read_stream(httpx.Response(200, text=wire))


HOST = (
    Path(__file__).resolve().parents[1]
    / "native/EvoFlow.ToolHost/bin/x64/Debug/net10.0-windows/win-x64/EvoFlow.ToolHost.exe"
)


async def host_call(tmp_path, tool, arguments, approved=True, version=1):
    if sys.platform != "win32" or not HOST.exists():
        pytest.skip("Build native ToolHost x64 before native integration checks")
    workspace = tmp_path / "workspace"
    home = tmp_path / "audit"
    workspace.mkdir(exist_ok=True)
    process = await asyncio.create_subprocess_exec(
        str(HOST),
        str(workspace),
        str(home),
        sys.executable,
        str(Path(__file__).resolve().parents[1] / "worker/evoflow_worker/document_handler.py"),
        stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE,
    )
    request = {"callId": "call", "runId": "run", "tool": tool, "version": version, "arguments": arguments}
    stdout, _ = await process.communicate(
        (json.dumps(request) + "\n" + json.dumps({"approved": approved}) + "\n").encode()
    )
    lines = [json.loads(line) for line in stdout.decode().splitlines()]
    return lines


async def test_native_dispatch_approval_redaction_and_failures(tmp_path):
    first = await host_call(
        tmp_path, "filesystem", {"operation": "write", "path": "a.txt", "content": "secret-token-123"}
    )
    assert first[-1]["result"]["verified"]
    second = await host_call(
        tmp_path, "filesystem", {"operation": "write", "path": "a.txt", "content": "changed"}, approved=False
    )
    assert second[0]["action"]["requiresApproval"] and second[-1]["result"]["status"] == "denied"
    assert (tmp_path / "workspace/a.txt").read_text() == "secret-token-123"
    audit = (tmp_path / "audit/tool-audit.jsonl").read_text()
    assert "secret-token" not in audit and "changed" not in audit and "a.txt" not in audit
    for tool, args in [
        ("filesystem", {"operation": "read", "path": "missing.txt"}),
        ("terminal_run", {"executable": "powershell", "arguments": ["-Command", "x"]}),
        ("application_launch", {"application": "chrome", "arguments": ["--remote-debugging-port=9222"]}),
        ("filesystem", {"operation": "read", "path": "C:\\Users\\test\\.ssh\\id_rsa"}),
    ]:
        lines = await host_call(tmp_path, tool, args)
        assert lines[-1]["result"]["status"] == "failed"
    terminal = await host_call(tmp_path, "terminal_run", {"executable": "hostname", "arguments": []})
    assert terminal[-1]["result"]["data"]["exitCode"] == 0
    assert terminal[-1]["result"]["data"]["stdout"].strip()
    unknown = await host_call(tmp_path, "filesystem", {"operation": "read", "path": "a.txt"}, version=99)
    assert unknown[-1]["result"]["errorCode"] == "validation"


async def test_native_document_output_uses_maintained_handler(tmp_path):
    path = str(tmp_path / "workspace/native.docx")
    created = await host_call(
        tmp_path,
        "document",
        {"operation": "create", "format": "docx", "path": path, "paragraphs": ["Real native handler"]},
    )
    assert created[-1]["result"]["verified"]
    assert Path(path).is_file()


def test_pdf_rich_edits_and_text_replacement_are_reopened(tmp_path):
    path = str(tmp_path / "rich.pdf")
    handle(
        DocumentRequest(
            operation="create", format="pdf", path=path, title="Report", paragraphs=["Original content"]
        )
    )
    result = handle(
        DocumentRequest(
            operation="edit",
            format="pdf",
            path=path,
            edits=[
                {"operation": "replace_text", "find": "Original", "text": "Revised"},
                {"operation": "stamp_text", "text": "APPROVED", "x": 36.0, "y": 200.0},
                {"operation": "rotate_page", "degrees": 90},
                {"operation": "insert_page", "index": 1, "title": "Added", "text": "Second page"},
            ],
        )
    )
    assert result["verified"] and result["inspection"]["pages"] == 2
    text = "\n".join(result["inspection"]["text"])
    text = " ".join(text.split())
    assert "Revised content" in text and "Original content" not in text and "APPROVED" in text
    from pypdf import PdfReader

    assert PdfReader(path).pages[0].rotation == 90
    handle(
        DocumentRequest(
            operation="edit", format="pdf", path=path, edits=[{"operation": "delete_page", "index": 1}]
        )
    )
    assert len(PdfReader(path).pages) == 1


async def test_general_app_catalog_and_approved_native_command(tmp_path):
    from evoflow_worker.local_tools import LaunchArgs, TerminalArgs

    assert LaunchArgs(application="Microsoft Word").application == "Microsoft Word"
    assert TerminalArgs(executable="ipconfig", arguments=["/all"]).executable == "ipconfig"
    catalog = await host_call(tmp_path, "application_search", {"query": "Word"})
    assert catalog[-1]["result"]["verified"]
    assert any("Word" in item["name"] for item in catalog[-1]["result"]["data"]["applications"])
    command = await host_call(
        tmp_path, "terminal_run", {"executable": "ipconfig", "arguments": []}, approved=False
    )
    assert command[0]["action"]["requiresApproval"] and command[-1]["result"]["status"] == "denied"
    protected = await host_call(
        tmp_path, "terminal_run", {"executable": "findstr", "arguments": ["x", ".env"]}
    )
    assert protected[-1]["result"]["status"] == "failed"


def test_pdf_forms_merge_crop_and_readback(tmp_path):
    from pypdf import PdfReader
    from reportlab.pdfgen import canvas

    form = tmp_path / "form.pdf"
    document = canvas.Canvas(str(form))
    document.drawString(36, 740, "Status")
    document.acroForm.textfield(name="status", value="Pending", x=36, y=700)
    document.save()
    updated = handle(
        DocumentRequest(
            operation="edit",
            format="pdf",
            path=str(form),
            edits=[{"operation": "set_field", "field": "status", "value": "Approved"}],
        )
    )
    assert updated["inspection"]["fields"]["status"] == "Approved"
    second = tmp_path / "second.pdf"
    handle(DocumentRequest(operation="create", format="pdf", path=str(second), paragraphs=["Second source"]))
    merged = handle(
        DocumentRequest(
            operation="edit",
            format="pdf",
            path=str(form),
            edits=[
                {"operation": "merge_pdf", "source": str(second)},
                {"operation": "crop_page", "index": 1, "rectangle": [0.0, 0.0, 300.0, 400.0]},
            ],
        )
    )
    assert merged["inspection"]["pages"] == 2
    assert float(PdfReader(form).pages[1].cropbox.width) == 300


async def test_authenticated_agent_api_delivers_native_results_and_rejects_spoofing(tmp_path):
    from evoflow_worker.api import create_app

    class Model:
        async def chat_step(self, messages, metrics, schemas, **kwargs):
            if messages[-1]["role"] == "tool":
                result = json.loads(messages[-1]["content"])
                assert result["status"] == "denied" and not result["verified"]
                return {"content": "Action was declined; no file was changed.", "routing": {}}
            return {
                "content": None,
                "routing": {},
                "tool_calls": [
                    {
                        "id": "c",
                        "type": "function",
                        "function": {
                            "name": "filesystem",
                            "arguments": '{"operation":"write","path":"existing.txt","content":"private-body"}',
                        },
                    }
                ],
            }

    token = "t" * 64
    app = create_app(tmp_path, token, model=Model())
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app),
        base_url="http://127.0.0.1",
        headers={"Authorization": "Bearer " + token},
    ) as client:
        assert (await client.get("/tools/pending", headers={"Authorization": "bad"})).status_code == 401
        assert (
            await client.get("/tools/pending", headers={"Origin": "https://evil.example"})
        ).status_code == 403
        await client.post("/tools/register", json={"workspace": "C:\\EvoFlow Files"})
        session = (await client.post("/chats")).json()["session_id"]
        run = (await client.post(f"/chats/{session}/messages", json={"message": "Edit a file"})).json()[
            "run_id"
        ]
        for _ in range(30):
            calls = (await client.get("/tools/pending")).json()["calls"]
            if calls:
                break
            await asyncio.sleep(0)
        call = calls[0]
        result = {"status": "denied", "verified": False, "summary": "User declined", "data": {}}
        endpoint = f"/tools/{call['callId']}/result"
        assert (
            await client.post(endpoint, json={"deliveryToken": "wrong", "result": result})
        ).status_code == 400
        assert (
            await client.post(endpoint, json={"deliveryToken": call["deliveryToken"], "result": result})
        ).status_code == 200
        await app.state.jobs[run]
        saved = (await client.get("/runs/" + run)).json()
        assert saved["tool_evidence"][0]["status"] == "denied"
        assert "private-body" not in json.dumps(saved)
        assert "private-body" not in json.dumps(app.state.store.events(run))


async def test_native_paths_cannot_follow_a_junction(tmp_path):
    import os
    import subprocess

    if sys.platform != "win32":
        pytest.skip("Windows junction policy")
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    target = tmp_path / "outside"
    target.mkdir()
    (target / "read.txt").write_text("outside")
    link = workspace / "linked"
    process = subprocess.run(
        [os.environ["COMSPEC"], "/c", "mklink", "/J", str(link), str(target)], capture_output=True
    )
    assert process.returncode == 0
    try:
        result = await host_call(tmp_path, "filesystem", {"operation": "read", "path": "linked/read.txt"})
        assert result[-1]["result"]["errorCode"] == "validation"
    finally:
        os.rmdir(link)  # Remove only the junction itself, never its target.
