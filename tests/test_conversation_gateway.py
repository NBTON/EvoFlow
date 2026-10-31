"""Exercise the real chat gateway with mocked HTTP; no credentials or live inference."""

import json

import httpx
import pytest
from evoflow_worker.models import new_metrics
from evoflow_worker.providers import ExecutionModelError, MultiProvider
from test_providers import CONFIG, FakeVault


def gateway_transport(monkeypatch, *, fail_execution=False):
    requests = []

    def respond(request):
        body = json.loads(request.content)
        requests.append(body)
        if request.url.path == "/api/alpha/decisions":
            task = json.loads(body["state"])["task"]
            effort = "high" if "Prove" in task else "none"
            return httpx.Response(
                200,
                json={
                    "id": "decision-receipt",
                    "model": body["model"],
                    "answers": {
                        "model": {"type": "choice", "choice": "deepseek_flash"},
                        "effort": {"type": "choice", "choice": effort},
                        "complexity": {"type": "score", "score": 1},
                    },
                    "usage": {"input_tokens": 4, "output_tokens": 2, "cost": 0},
                },
            )
        if fail_execution:
            return httpx.Response(401, json={"error": "test-secret"})
        chunks = [
            {
                "id": "generation-receipt",
                "model": body["model"],
                "choices": [{"index": 0, "delta": {"content": "Hello Omar!"}, "finish_reason": "stop"}],
                "usage": {"prompt_tokens": 20, "completion_tokens": 4},
            }
        ]
        wire = "".join("data: " + json.dumps(c) + "\n\n" for c in chunks) + "data: [DONE]\n\n"
        return httpx.Response(200, text=wire, headers={"Content-Type": "text/event-stream"})

    original = httpx.AsyncClient
    monkeypatch.setattr(
        "evoflow_worker.providers.httpx.AsyncClient",
        lambda **kwargs: original(transport=httpx.MockTransport(respond), **kwargs),
    )
    return requests


async def test_actual_latest_message_and_context_reach_both_models(monkeypatch):
    requests = gateway_transport(monkeypatch)
    messages = [
        {"role": "user", "content": "Hi, my name is Omar."},
        {"role": "assistant", "content": "Hello Omar!"},
        {"role": "user", "content": "What is my name?"},
    ]
    metrics = new_metrics()
    result = await MultiProvider(FakeVault(), CONFIG).chat(messages, metrics)
    assert result["content"] == "Hello Omar!"
    state = json.loads(requests[0]["state"])
    assert state["task"] == "What is my name?"
    assert state["conversation_context"] == messages[:-1]
    assert "contract" not in state
    assert requests[1]["messages"][1:] == messages
    assert "tools" not in requests[1]
    assert "RELATIONAL PROGRAM" not in requests[1]["messages"][0]["content"]
    assert "greetings" in requests[0]["questions"]["model"]["instructions"]
    assert metrics["tool_calls"] == 0
    assert result["routing"]["requested_effort"] == "none"
    assert requests[1]["thinking"] == {"type": "disabled"}


async def test_complex_message_selects_and_submits_reasoning_from_decisions(monkeypatch):
    requests = gateway_transport(monkeypatch)
    result = await MultiProvider(FakeVault(), CONFIG).chat(
        [{"role": "user", "content": "Prove the optimal algorithm and analyze its complexity."}],
        new_metrics(),
    )
    assert result["routing"]["requested_effort"] == "high"
    assert requests[1]["reasoning_effort"] == "high"
    assert requests[1]["thinking"] == {"type": "enabled"}
    assert result["routing"]["effective_effort"] == "unknown"


async def test_chat_provider_auth_failure_is_error_without_sample_substitution(monkeypatch):
    requests = gateway_transport(monkeypatch, fail_execution=True)
    with pytest.raises(ExecutionModelError, match="HTTP 401"):
        await MultiProvider(FakeVault(), CONFIG).chat(
            [{"role": "user", "content": "Hi, my name is Omar."}], new_metrics()
        )
    assert len(requests) == 2


async def test_long_latest_task_is_not_replaced_or_truncated_for_routing(monkeypatch):
    requests = gateway_transport(monkeypatch)
    task = "Please consider: " + "a" * 5000 + " What is Python?"
    await MultiProvider(FakeVault(), CONFIG).chat([{"role": "user", "content": task}], new_metrics())
    assert json.loads(requests[0]["state"])["task"] == task
    assert requests[1]["messages"][-1]["content"] == task
