"""Real SDK serialization, privacy, correlation and optional monitoring failures."""

import asyncio
import copy
import json
from pathlib import Path
from types import SimpleNamespace

import httpx
import pytest
from evoflow_worker.credentials import ProviderConfigError, Vault, load_provider_config
from evoflow_worker.models import new_metrics
from evoflow_worker.monitoring import Monitoring, observe_run, usage_metadata
from evoflow_worker.providers import MultiProvider
from langsmith.run_helpers import get_tracing_context


class RecordingClient:
    def __init__(self, fail=False):
        self.created, self.updated = [], []
        self.fail = fail

    def create_run(self, **data):
        if self.fail:
            raise RuntimeError("secret transport details")
        self.created.append(copy.deepcopy(data))

    def update_run(self, **data):
        self.updated.append(copy.deepcopy(data))

    def flush(self, **kwargs):
        pass


def monitor(client):
    result = Monitoring(SimpleNamespace(get=lambda _: None), {"enabled": False})
    result.client = client
    return result


@pytest.mark.asyncio
async def test_real_provider_receipt_is_nested_correlated_and_content_free(monkeypatch):
    config = json.loads(Path("config/runtime.json").read_text())
    gateway = MultiProvider(SimpleNamespace(get=lambda _: "credential-secret"), config)
    client = RecordingClient()
    gateway.monitoring = monitor(client)
    real_client = httpx.AsyncClient
    transport = httpx.MockTransport(
        lambda request: httpx.Response(
            200,
            json={
                "id": "provider-id",
                "model": "returned-model",
                "provider": "deepseek",
                "usage": {
                    "prompt_tokens": 12,
                    "completion_tokens": 7,
                    "cost": 0.002,
                    "completion_tokens_details": {"reasoning_tokens": 3},
                },
                "choices": [{"message": {"content": "response-secret"}}],
            },
        )
    )
    monkeypatch.setattr(httpx, "AsyncClient", lambda **kw: real_client(transport=transport, **kw))
    monkeypatch.setenv("LANGSMITH_TRACING", "true")

    class Conversation:
        model = gateway

        @observe_run("conversation")
        async def execute(self, record, *_):
            assert get_tracing_context()["enabled"] is False
            result = await self.model._call(
                "deepseek",
                "deepseek-flash",
                "chat/completions",
                {"messages": [{"role": "user", "content": "prompt-secret"}]},
                record["metrics"],
                category="execution",
            )
            assert result["choices"][0]["message"]["content"] == "response-secret"
            return {**record, "status": "complete", "assistant_response": "response-secret"}

    await Conversation().execute(
        {"id": "run-1", "session_id": "chat-1", "metrics": new_metrics()},
        "private-owner",
        "private-workspace",
    )
    assert len(client.created) == len(client.updated) == 2
    root, child = client.created
    assert child["parent_run_id"] == root["id"]
    assert child["trace_id"] == root["trace_id"]
    assert child["extra"]["metadata"]["session_id"] == "chat-1"
    assert child["extra"]["metadata"]["ls_model_name"] == "deepseek-flash"
    assert child["run_type"] == "llm"
    llm = client.updated[0]
    assert llm["outputs"]["usage_metadata"] == {
        "input_tokens": 12,
        "output_tokens": 7,
        "total_tokens": 19,
        "total_cost": 0.002,
        "output_token_details": {"reasoning": 3},
    }
    assert llm["extra"]["metadata"]["effective_model"] == "returned-model"
    serialized = json.dumps([client.created, client.updated], default=str)
    for secret in (
        "prompt-secret",
        "response-secret",
        "credential-secret",
        "private-owner",
        "private-workspace",
    ):
        assert secret not in serialized


@pytest.mark.asyncio
async def test_no_key_and_monitoring_failure_do_not_break_work(monkeypatch):
    calls = []
    monkeypatch.setattr("evoflow_worker.monitoring.Client", lambda **kw: calls.append(kw))
    missing = Monitoring(SimpleNamespace(get=lambda _: None), {"enabled": True})
    disabled = Monitoring(SimpleNamespace(get=lambda _: "secret"), {"enabled": False})
    assert missing.client is disabled.client is None and calls == []

    class Workflow:
        model = SimpleNamespace(monitoring=monitor(RecordingClient(fail=True)))

        @observe_run("workflow")
        async def run(self, data, **kw):
            return {"status": "complete", "run_id": kw["run_id"], "metrics": {}}

    result = await Workflow().run("dataset-secret")
    assert result["status"] == "complete" and result["run_id"]
    assert Workflow.model.monitoring.delivery_failed


@pytest.mark.asyncio
async def test_cancellation_and_failure_never_send_exception_text():
    client = RecordingClient()
    instance = monitor(client)
    for exception in (ValueError("credential-secret"), asyncio.CancelledError("private-data")):
        with pytest.raises(type(exception)):
            with instance.span("test"):
                raise exception
    assert [x["error"] for x in client.updated] == ["operation failed", "canceled"]
    assert "credential-secret" not in json.dumps(client.updated, default=str)


@pytest.mark.asyncio
async def test_concurrent_sessions_keep_child_spans_under_their_own_root():
    client = RecordingClient()
    instance = monitor(client)

    class Conversation:
        model = SimpleNamespace(monitoring=instance)

        @observe_run("conversation")
        async def execute(self, record):
            await asyncio.sleep(0)
            with instance.span("provider", run_type="llm"):
                await asyncio.sleep(0)
            return {**record, "status": "complete"}

    await asyncio.gather(
        *(Conversation().execute({"id": f"run-{n}", "session_id": f"session-{n}"}) for n in range(2))
    )
    roots = {r["id"]: r for r in client.created if r["run_type"] == "chain"}
    children = [r for r in client.created if r["run_type"] == "llm"]
    assert len(roots) == len(children) == 2
    for child in children:
        parent = roots[child["parent_run_id"]]
        assert child["extra"]["metadata"]["session_id"] == parent["extra"]["metadata"]["session_id"]


def test_usage_never_invents_missing_counts_costs_or_invalid_values():
    assert usage_metadata([{"usage": ["malformed"]}]) == ({}, False, False)
    assert usage_metadata([{"usage": {"completion_tokens_details": "malformed"}}]) == ({}, False, False)
    assert usage_metadata([{"usage": {"prompt_tokens": 5, "completion_tokens": 2}}]) == (
        {"input_tokens": 5, "output_tokens": 2, "total_tokens": 7},
        True,
        False,
    )
    assert usage_metadata([{"usage": {"prompt_tokens": 5, "completion_tokens": 2, "cost": None}}, {}]) == (
        {},
        False,
        False,
    )
    assert usage_metadata([{"usage": {"input_tokens": True, "output_tokens": -1, "cost": float("nan")}}]) == (
        {},
        False,
        False,
    )


def test_monitoring_configuration_is_validated_before_key_import(monkeypatch, tmp_path):
    monkeypatch.setattr("evoflow_worker.credentials._crypt", lambda data, decrypt: data[::-1])
    path = tmp_path / "keys.env"
    monkeypatch.setenv("EVOFLOW_KEYS_FILE", str(path))
    vault = Vault(tmp_path)
    path.write_text("LANGSMITH_API_KEY=fake-monitor-key\nLANGSMITH_TRACING=true\nLANGSMITH_PROJECT=Test\n")
    assert load_provider_config(vault)["langsmith"]["enabled"]
    assert vault.get("langsmith") == "fake-monitor-key"
    assert "fake-monitor-key" not in json.dumps(load_provider_config(vault))
    for field in (
        "LANGSMITH_TRACING=invalid",
        "LANGSMITH_ENDPOINT=https://attacker.example",
        "LANGSMITH_WORKSPACE_ID=not-a-uuid",
    ):
        path.write_text("LANGSMITH_API_KEY=must-not-import\n" + field)
        with pytest.raises(ProviderConfigError):
            load_provider_config(vault)
        assert vault.get("langsmith") == "fake-monitor-key"
