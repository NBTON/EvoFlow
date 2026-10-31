"""All network interactions are mocked; no provider credentials or live calls."""

import asyncio
import copy
import json
from pathlib import Path

import httpx
import pytest
from evoflow_worker import providers
from evoflow_worker.decision_routing import DecisionResponseError, validate_answers
from evoflow_worker.fixtures import fixture
from evoflow_worker.models import new_metrics
from evoflow_worker.provider_reasoning import native_reasoning, output_budget
from evoflow_worker.providers import ExecutionModelError, MultiProvider

CONFIG = json.loads((Path(__file__).parents[1] / "config/runtime.json").read_text())


class FakeVault:
    def get(self, name):
        return "test-secret"


@pytest.mark.parametrize(
    "logical,native",
    [
        ("none", "none"),
        ("minimal", "low"),
        ("low", "low"),
        ("medium", "high"),
        ("high", "high"),
        ("xhigh", "high"),
        ("max", "max"),
    ],
)
def test_deepseek_mapping(logical, native):
    payload, submitted = native_reasoning("deepseek", logical)
    assert submitted == native
    assert payload["reasoning_effort"] == native
    assert payload["thinking"]["type"] == ("disabled" if logical == "none" else "enabled")


@pytest.mark.parametrize("logical", ["none", "minimal", "low", "medium", "high", "xhigh", "max"])
def test_mimo_reports_only_native_binary_effort(logical):
    payload, native = native_reasoning("mimo", logical)
    assert native == ("disabled" if logical == "none" else "enabled")
    assert payload == {"thinking": {"type": native}}
    assert output_budget(CONFIG["orchestration"], native) >= (4096 if logical == "none" else 16384)


def setup(
    monkeypatch,
    *,
    primary=200,
    invalid=False,
    chosen="deepseek_flash",
    execution=200,
    stream_error=False,
    generated_plan=None,
):
    requests = []

    def respond(request):
        requests.append(request)
        if request.method == "GET":
            meta = {
                "id": "openai/gpt-6-luna",
                "reasoning": {"supported_efforts": ["none", "low", "medium", "high", "xhigh", "max"]},
                "endpoints": [
                    {
                        "tag": "test",
                        "supported_parameters": ["reasoning", "max_tokens"],
                        "max_completion_tokens": 128000,
                    }
                ],
            }
            return httpx.Response(
                200, json={"data": meta if request.url.path.endswith("endpoints") else [meta]}
            )
        body = json.loads(request.content)
        if request.url.path == "/api/alpha/decisions":
            if body["model"] == "baseten/joey-1":
                if primary != 200:
                    return httpx.Response(primary, json={"error": "test-secret must never be exposed"})
                answers = {
                    "model": {"type": "choice", "choice": chosen},
                    "effort": {"type": "choice", "choice": "medium"},
                    "complexity": {"type": "score", "score": 1},
                }
                if invalid:
                    answers["model"] = {"type": "UNKNOWN"}
            else:
                assert all(q["type"] == "noul" for q in body["questions"].values())
                answers = {
                    k: {"type": "noul", "noul": 0.9 if k in {"model_deepseek_flash", "effort_high"} else 0.1}
                    for k in body["questions"]
                }
            return httpx.Response(
                200,
                json={
                    "model": body["model"],
                    "answers": answers,
                    "usage": {"input_tokens": 10, "output_tokens": 2, "cost": 0.001},
                },
            )
        if execution != 200:
            return httpx.Response(execution, json={"error": "test-secret"})
        plan = json.dumps(generated_plan or {"title": "mock plan", "summary": "Mocked", "program": []})
        chunks = [
            {
                "choices": [
                    {"index": 0, "delta": {"content": "", "reasoning_content": "PRIVATE MUST NOT BE STORED"}}
                ]
            },
            {
                "model": body["model"],
                "choices": [{"index": 0, "delta": {"content": plan}, "finish_reason": "stop"}],
            },
            {
                "choices": [],
                "usage": {
                    "prompt_tokens": 20,
                    "completion_tokens": 30,
                    "completion_tokens_details": {"reasoning_tokens": 10},
                },
            },
        ]
        wire = "".join("data: " + json.dumps(c) + "\n\n" for c in chunks)
        if not stream_error:
            wire += "data: [DONE]\n\n"
        return httpx.Response(200, text=wire, headers={"Content-Type": "text/event-stream"})

    original = httpx.AsyncClient
    monkeypatch.setattr(
        providers.httpx,
        "AsyncClient",
        lambda **kwargs: original(transport=httpx.MockTransport(respond), **kwargs),
    )
    return requests


async def test_primary_typed_selection_and_streamed_deepseek(monkeypatch):
    requests = setup(monkeypatch)
    metrics = new_metrics()
    plan = await MultiProvider(FakeVault(), CONFIG).synthesize(
        fixture(1).prompt_sample(), "Reconcile", [], None, metrics
    )
    assert plan.title == "mock plan"
    posts = [r for r in requests if r.method == "POST"]
    assert len(posts) == 2
    assert posts[0].url.path == "/api/alpha/decisions"
    assert posts[1].url.host == "api.deepseek.com"
    body = json.loads(posts[1].content)
    assert body["reasoning_effort"] == "high" and body["thinking"] == {"type": "enabled"}
    assert body["max_tokens"] == 16384
    route = metrics["routing_decisions"][0]
    assert route["requested_effort"] == "medium" and route["submitted_effort"] == "high"
    assert route["effective_effort"] == "unknown"
    assert metrics["model_calls"] == 2 and metrics["input_tokens"] == 30
    assert not metrics["cost_reported"]  # Direct provider did not return cost.
    assert "PRIVATE" not in json.dumps(metrics) and "test-secret" not in json.dumps(metrics)


@pytest.mark.parametrize(
    "primary,invalid", [(401, False), (404, False), (429, False), (503, False), (200, True)]
)
async def test_primary_failure_uses_preserved_binary_fallback(monkeypatch, primary, invalid):
    requests = setup(monkeypatch, primary=primary, invalid=invalid)
    metrics = new_metrics()
    await MultiProvider(FakeVault(), CONFIG).synthesize(
        fixture(1).prompt_sample(), "Reconcile", [], None, metrics
    )
    assert len([r for r in requests if r.method == "POST"]) == 3
    assert metrics["routing_decisions"][0]["decision_model"] == "respan/span-01-lite:free"
    assert metrics["decision_attempts"][0]["status"] == "failed"
    if primary != 200:
        assert not metrics["usage_complete"]


async def test_worker_pipeline_404_fallback_verifies_and_then_reuses(monkeypatch, tmp_path):
    from evoflow_worker.engine import Engine
    from evoflow_worker.store import Store
    from test_runtime import TEST_SQL

    requests = setup(
        monkeypatch,
        primary=404,
        generated_plan={
            "title": "TEST ONLY",
            "summary": "Mocked synthesis; never installed outside temporary test state",
            "query": TEST_SQL,
        },
    )
    engine = Engine(Store(tmp_path), MultiProvider(FakeVault(), CONFIG))
    first = await engine.run(fixture(1))
    assert first["status"] == "complete", first.get("error")
    assert first["verification"]["passed"]
    assert first["metrics"]["model_calls"] == 3
    assert first["routing"]["decisions"][0]["decision_model"] == "respan/span-01-lite:free"
    assert not first["metrics"]["usage_complete"]
    count = len(requests)
    second = await engine.run(fixture(2))
    assert second["status"] == "complete" and second["reused"]
    assert second["metrics"]["model_calls"] == 0 and len(requests) == count


@pytest.mark.parametrize(
    "candidate,host,parameter",
    [
        ("mimo_flash", "token-plan-sgp.xiaomimimo.com", "max_completion_tokens"),
        ("mimo_pro", "token-plan-sgp.xiaomimimo.com", "max_completion_tokens"),
        ("gpt_luna", "openrouter.ai", "max_tokens"),
    ],
)
async def test_all_generators_have_native_parameters(monkeypatch, candidate, host, parameter):
    requests = setup(monkeypatch, chosen=candidate)
    metrics = new_metrics()
    await MultiProvider(FakeVault(), CONFIG).synthesize(
        fixture(1).prompt_sample(), "Reconcile", [], None, metrics
    )
    execution = [r for r in requests if r.method == "POST"][-1]
    body = json.loads(execution.content)
    assert execution.url.host == host and body[parameter] == 16384
    assert "temperature" not in body
    if candidate.startswith("mimo"):
        assert body["thinking"] == {"type": "enabled"} and "reasoning_effort" not in body
        assert metrics["routing_decisions"][0]["submitted_effort"] == "enabled"
    else:
        assert body["reasoning"] == {"effort": "medium"}
        assert body["provider"]["allow_fallbacks"] is False


async def test_generation_failure_does_not_reinvoke_decision_fallback(monkeypatch):
    requests = setup(monkeypatch, execution=401)
    metrics = new_metrics()
    with pytest.raises(ExecutionModelError, match="HTTP 401"):
        await MultiProvider(FakeVault(), CONFIG).synthesize(
            fixture(1).prompt_sample(), "Reconcile", [], None, metrics
        )
    assert len(requests) == 2 and metrics["model_calls"] == 2
    assert len(metrics["decision_attempts"]) == 1


async def test_incomplete_stream_is_not_success(monkeypatch):
    setup(monkeypatch, stream_error=True)
    metrics = new_metrics()
    with pytest.raises(ExecutionModelError, match="Incomplete"):
        await MultiProvider(FakeVault(), CONFIG).synthesize(
            fixture(1).prompt_sample(), "Reconcile", [], None, metrics
        )
    assert not metrics["usage_complete"]


async def test_budget_caps_both_decision_and_generation(monkeypatch):
    requests = setup(monkeypatch)
    config = copy.deepcopy(CONFIG)
    config["orchestration"]["max_total_calls"] = 1
    metrics = new_metrics()
    with pytest.raises(ExecutionModelError, match="budget"):
        await MultiProvider(FakeVault(), config).synthesize(
            fixture(1).prompt_sample(), "Reconcile", [], None, metrics
        )
    assert len(requests) == 1


async def test_decision_cancellation_never_falls_back(monkeypatch):
    gateway = MultiProvider(FakeVault(), CONFIG)

    async def cancel(*args, **kwargs):
        raise asyncio.CancelledError

    monkeypatch.setattr(gateway, "_call", cancel)
    metrics = new_metrics()
    with pytest.raises(asyncio.CancelledError):
        await gateway.choose_route(fixture(1).prompt_sample(), "Reconcile", [], None, metrics)
    assert len(metrics["decision_attempts"]) == 1
    assert metrics["decision_attempts"][0]["status"] == "canceled"


@pytest.mark.parametrize(
    "answer",
    [
        {"type": "UNKNOWN"},
        {"type": "choice", "choice": "not-offered"},
        {"type": "choice", "choice": "a", "confidence": float("nan")},
        {"type": "choice", "choice": "a", "probabilities": {"a": 2}},
    ],
)
def test_invalid_answers_fail_closed(answer):
    with pytest.raises(DecisionResponseError):
        validate_answers(
            {"answers": {"model": answer}}, {"model": {"type": "choice", "criteria": {"a": "candidate"}}}
        )
