"""Routing and gateway contracts use mocked current metadata, never paid/live inference."""

import copy
import json
from pathlib import Path

import httpx
import pytest
from evoflow_worker import models
from evoflow_worker.engine import Engine
from evoflow_worker.fixtures import fixture
from evoflow_worker.models import ModelError, OpenRouter, new_metrics
from evoflow_worker.routing import RoutingError, reasoning_payload, routing_context, select_route
from evoflow_worker.store import Store

CONFIG = json.loads((Path(__file__).parents[1] / "config/runtime.json").read_text())
MODEL = CONFIG["general_model"]


class TestVault:
    __test__ = False

    def get(self, _):
        return "test-key"


def metadata(reasoning=None):
    return {
        "id": MODEL,
        "pricing": {"prompt": "0", "completion": "0"},
        "reasoning": reasoning
        if reasoning is not None
        else {
            "mandatory": False,
            "supported_efforts": ["high", "medium", "none"],
        },
        "endpoints": [
            {
                "tag": "test-provider",
                "provider_name": "Test provider",
                "pricing": {"prompt": "0", "completion": "0"},
                "supported_parameters": ["temperature", "max_tokens", "reasoning"],
            }
        ],
    }


def mock_gateway(monkeypatch, meta=None, status=200):
    meta = meta or metadata()
    sent = []
    requests = []

    def respond(request):
        requests.append(request)
        if request.method == "GET":
            if request.url.path.endswith("/endpoints"):
                return httpx.Response(200, json={"data": meta})
            return httpx.Response(200, json={"data": [meta]})
        sent.append(json.loads(request.content))
        return httpx.Response(
            status,
            json={
                "id": "test-request",
                "model": "resolved/model-version",
                "provider": "Test provider",
                "choices": [
                    {
                        "message": {
                            "content": json.dumps(
                                {
                                    "title": "Test generated plan",
                                    "summary": "Boundary test",
                                    "program": [],
                                }
                            )
                        }
                    }
                ],
                "usage": {
                    "prompt_tokens": 8,
                    "completion_tokens": 12,
                    "cost": 0,
                    "completion_tokens_details": {"reasoning_tokens": 4},
                },
            },
        )

    original = httpx.AsyncClient
    monkeypatch.setattr(
        models.httpx,
        "AsyncClient",
        lambda **kwargs: original(
            transport=httpx.MockTransport(respond),
            **kwargs,
        ),
    )
    return sent, requests


def test_joint_profiles_cover_novelty_complexity_repair_and_explicit_delegation():
    config = copy.deepcopy(CONFIG)
    context = routing_context(fixture(1).prompt_sample(), "Reconcile", [])
    novel = select_route(config, context)
    assert (novel["profile"], novel["requested_model"], novel["requested_effort"]) == (
        "novel",
        MODEL,
        "medium",
    )
    context.update(task_novelty=False)
    assert select_route(config, context)["requested_effort"] == "none"
    context.update(complexity="high")
    assert select_route(config, context)["profile"] == "complex"
    context.update(schema_novelty=True)
    assert select_route(config, context)["profile"] == "novel"
    context.update(repair=True)
    assert select_route(config, context)["profile"] == "repair"
    context.update(delegated_role="code")
    with pytest.raises(RoutingError, match="not enabled"):
        select_route(config, context)
    config["routing"]["delegated_roles"]["code"]["enabled"] = True
    delegated = select_route(config, context)
    assert delegated["requested_model"] == config["code_model"]
    assert delegated["requested_effort"] == "medium"


def test_fixed_policy_does_not_adapt_settings_to_conditions_or_repairs():
    contexts = [
        {},
        {"task_novelty": True},
        {"schema_novelty": True},
        {"repair": True, "complexity": "high", "delegated_role": "disabled"},
    ]
    decisions = [select_route(CONFIG, context, "fixed") for context in contexts]
    assert {(d["requested_model"], d["requested_effort"]) for d in decisions} == {(MODEL, "none")}
    assert all(d["profile"] == "fixed" for d in decisions)
    with pytest.raises(RoutingError):
        select_route(CONFIG, {}, "unrecognized")


def test_context_uses_visible_schema_not_record_ids_or_final_answers():
    original = fixture(1)
    known = [original.contract()]
    assert not routing_context(fixture(9001).prompt_sample(), "Reconcile", [], known_contracts=known)[
        "schema_novelty"
    ]
    assert routing_context(fixture(2, schema=2).prompt_sample(), "Reconcile", [], known_contracts=known)[
        "schema_novelty"
    ]
    assert routing_context(original.prompt_sample(), "x" * 601, [])["complexity"] == "high"
    reuse = select_route({}, {"reuse": True, "delegated_role": "unavailable"})
    assert reuse["requested_model"] is None and reuse["effort_status"] == "no_inference"


@pytest.mark.parametrize(
    "reasoning,effort,expected",
    [
        ({"mandatory": False, "supported_efforts": ["high"]}, "medium", None),
        ({"mandatory": True, "supported_efforts": ["none", "high"]}, "none", None),
        ({"mandatory": False}, "medium", None),
        ({"mandatory": False}, "none", {"enabled": False}),
        ({"supported_efforts": None}, "low", {"effort": "low"}),
        ({"supported_efforts": None}, "fictional", None),
    ],
)
def test_reasoning_capability_omitted_null_allowlist_and_mandatory(reasoning, effort, expected):
    assert reasoning_payload(metadata(reasoning), effort)[0] == expected


async def test_submitted_controls_and_actual_model_usage_are_auditable(monkeypatch):
    sent, requests = mock_gateway(monkeypatch)
    router = OpenRouter(TestVault(), CONFIG)
    metrics = new_metrics()
    await router.synthesize(fixture(1).prompt_sample(), "Reconcile", [], None, metrics)
    assert sent[0]["reasoning"] == {"effort": "medium"}
    assert sent[0]["provider"] == {
        "only": ["test-provider"],
        "require_parameters": True,
        "max_price": {"prompt": 0, "completion": 0},
    }
    assert len([r for r in requests if r.method == "GET"]) == 2
    decision = metrics["routing_decisions"][0]
    call = metrics["calls"][0]
    assert decision["requested_model"] == MODEL
    assert call["effective_model"] == decision["effective_model"] == "resolved/model-version"
    assert call["effective_effort"] == decision["effective_effort"] == "unknown"
    assert decision["effort_status"] == "submitted_unconfirmed"
    assert decision["provider"] == "Test provider" and decision["usage"]["cost"] == 0
    assert metrics["reasoning_tokens"] == 4


async def test_unsupported_effort_is_suppressed_and_never_reported_applied(monkeypatch):
    sent, _ = mock_gateway(monkeypatch, metadata({"mandatory": False, "supported_efforts": ["high"]}))
    router, metrics = OpenRouter(TestVault(), CONFIG), new_metrics()
    await router.synthesize(fixture(1).prompt_sample(), "Reconcile", [], None, metrics)
    assert "reasoning" not in sent[0]
    decision = metrics["routing_decisions"][0]
    assert decision["requested_effort"] == "medium" and decision["submitted_effort"] is None
    assert decision["effort_status"] == "suppressed_unsupported" and decision["effective_effort"] == "unknown"


async def test_provider_restricted_to_endpoints_supporting_submitted_controls(monkeypatch):
    meta = metadata()
    incompatible = copy.deepcopy(meta["endpoints"][0])
    incompatible.update(tag="no-reasoning", supported_parameters=["temperature", "max_tokens"])
    meta["endpoints"].append(incompatible)
    sent, _ = mock_gateway(monkeypatch, meta)
    router, metrics = OpenRouter(TestVault(), CONFIG), new_metrics()
    await router.synthesize(fixture(1).prompt_sample(), "Reconcile", [], None, metrics)
    assert sent[0]["provider"]["only"] == ["test-provider"]


async def test_paid_or_unknown_price_metadata_stops_before_inference(monkeypatch):
    meta = metadata()
    meta["endpoints"][0]["pricing"]["completion"] = "0.00001"
    sent, _ = mock_gateway(monkeypatch, meta)
    with pytest.raises(ModelError, match="pricing is not zero"):
        await OpenRouter(TestVault(), CONFIG).inspect(MODEL)
    assert not sent
    with pytest.raises(ModelError, match="free models"):
        await OpenRouter(TestVault(), CONFIG).inspect("paid/example")


async def test_capability_deadline_stops_without_inference(monkeypatch):
    original = httpx.AsyncClient
    sent = []

    def timeout(request):
        sent.append(request.method)
        raise httpx.ReadTimeout("Metadata timeout")

    monkeypatch.setattr(
        models.httpx,
        "AsyncClient",
        lambda **kwargs: original(
            transport=httpx.MockTransport(timeout),
            **kwargs,
        ),
    )
    router, metrics = OpenRouter(TestVault(), CONFIG), new_metrics()
    with pytest.raises(ModelError, match="metadata unavailable"):
        await router.request("chat/completions", {"model": MODEL}, metrics)
    assert metrics["model_calls"] == 0 and "POST" not in sent


async def test_inference_budget_cannot_be_bypassed_by_routing_or_retries(monkeypatch):
    sent, _ = mock_gateway(monkeypatch)
    router, metrics = OpenRouter(TestVault(), CONFIG), new_metrics()
    metrics["model_calls"] = CONFIG["max_model_calls_per_run"]
    with pytest.raises(ModelError, match="inference budget reached"):
        await router.synthesize(fixture(1).prompt_sample(), "Reconcile", [], None, metrics)
    assert not sent and metrics["model_calls"] == CONFIG["max_model_calls_per_run"]


async def test_retry_error_usage_stays_unknown_after_a_successful_receipt(monkeypatch):
    original = httpx.AsyncClient
    meta = metadata()
    attempts = []

    def respond(request):
        if request.method == "GET":
            return httpx.Response(
                200, json={"data": meta if request.url.path.endswith("/endpoints") else [meta]}
            )
        attempts.append(request)
        if len(attempts) == 1:
            return httpx.Response(
                503, json={"error": {"message": "Capacity unavailable"}}, headers={"Retry-After": "1"}
            )
        return httpx.Response(
            200, json={"model": MODEL, "usage": {"prompt_tokens": 2, "completion_tokens": 3, "cost": 0}}
        )

    monkeypatch.setattr(
        models.httpx,
        "AsyncClient",
        lambda **kwargs: original(transport=httpx.MockTransport(respond), **kwargs),
    )
    router, metrics = OpenRouter(TestVault(), CONFIG), new_metrics()
    await router.request("chat/completions", {"model": MODEL}, metrics)
    assert metrics["model_calls"] == 2 and metrics["retries"] == 1
    assert metrics["input_tokens"] == 2 and metrics["output_tokens"] == 3
    assert not metrics["usage_complete"] and not metrics["cost_reported"]
    assert metrics["calls"][0]["usage_status"] == "unreported_error_attempt"


async def test_specialized_decisions_endpoint_may_be_absent_from_text_catalog(monkeypatch):
    original = httpx.AsyncClient
    model = CONFIG["decision_fallback_model"]
    meta = metadata()
    meta.update(id=model, architecture={"output_modalities": ["decisions"]})

    def respond(request):
        return httpx.Response(200, json={"data": meta if request.url.path.endswith("/endpoints") else []})

    monkeypatch.setattr(
        models.httpx,
        "AsyncClient",
        lambda **kwargs: original(
            transport=httpx.MockTransport(respond),
            **kwargs,
        ),
    )
    assert (await OpenRouter(TestVault(), CONFIG).inspect(model))["id"] == model


async def test_provider_400_reason_is_bounded_and_credentials_are_redacted(monkeypatch):
    original = httpx.AsyncClient
    meta = metadata()

    def respond(request):
        if request.method == "GET":
            return httpx.Response(
                200, json={"data": meta if request.url.path.endswith("/endpoints") else [meta]}
            )
        return httpx.Response(400, json={"error": {"message": "Unsupported state test-key " + "x" * 600}})

    monkeypatch.setattr(
        models.httpx,
        "AsyncClient",
        lambda **kwargs: original(
            transport=httpx.MockTransport(respond),
            **kwargs,
        ),
    )
    router, metrics = OpenRouter(TestVault(), CONFIG), new_metrics()
    with pytest.raises(ModelError, match="Unsupported state") as exc:
        await router.request("decisions", {"model": MODEL}, metrics)
    assert "test-key" not in str(exc.value) and len(metrics["calls"][0]["error"]) <= 500
    assert metrics["model_calls"] == 1


async def test_reuse_run_records_route_without_inference_or_metadata(tmp_path):
    # Reuse of independently admitted test-only procedures stays entirely local.
    from test_runtime import TestModel

    store = Store(tmp_path)
    engine = Engine(store, TestModel())
    await engine.run(fixture(1), routing_policy="fixed")
    result = await engine.run(fixture(2), routing_policy="adaptive")
    assert result["metrics"]["model_calls"] == 0
    assert result["routing"]["policy"] == "adaptive"
    assert result["routing"]["decisions"][0]["profile"] == "reuse"
    assert result["routing"]["decisions"][0]["effective_effort"] == "not_applicable"
