"""Deterministic model/effort policy. No model calls or evaluation answers enter routing."""

from __future__ import annotations

import re
from copy import deepcopy

EFFORTS = {"none", "minimal", "low", "medium", "high", "xhigh", "max"}
POLICY_VERSION = "model-effort-v1"


class RoutingError(ValueError):
    pass


def chat_routing_context(messages: list[dict]) -> dict:
    """Legacy single-provider policy uses the actual conversation, never fixture shape."""
    task = messages[-1]["content"]
    complex_request = len(task) > 2000 or bool(
        re.search(
            r"\b(prove|proof|optimi[sz]e|algorithm|complex|debug|concurren\w*|dynamic programming)\b",
            task,
            re.IGNORECASE,
        )
    )
    return {
        "task_kind": "conversation",
        "complexity": "high" if complex_request else "standard",
        "history_messages": len(messages) - 1,
        "task_novelty": False,
        "schema_novelty": False,
        "repair": False,
        "reuse": False,
    }


def policy_name(config: dict, policy: str | None = None) -> str:
    policy = policy or config.get("routing", {}).get("default_policy", "adaptive")
    if policy not in {"adaptive", "fixed"}:
        raise RoutingError("Routing policy must be adaptive or fixed")
    return policy


def routing_context(sample, task, evidence, *, known_contracts=(), repair=False, delegated_role=None):
    """Use visible input shape and prior contracts, never expected/verified answers."""
    contract = sample["contract"]
    known = list(known_contracts)
    # Input size alone does not make the reusable relational program more complex.
    columns = sum(len(value) for value in contract.get("tables", {}).values())
    complexity = "high" if columns > 20 or len(task) > 600 or len(evidence) > 2 else "standard"
    return {
        "task_novelty": not known,
        "schema_novelty": bool(known) and not any(c == contract for c in known),
        "repair": bool(repair),
        "complexity": complexity,
        "delegated_role": delegated_role,
        "reuse": False,
    }


def select_route(config: dict, context: dict, policy: str | None = None) -> dict:
    """Choose a joint model/effort profile without spending inference budget."""
    policy = policy_name(config, policy)
    settings = config.get("routing", {})
    decision = {
        "policy": policy,
        "policy_version": settings.get("policy_version", POLICY_VERSION),
        "context": deepcopy(context),
        "requested_model": None,
        "requested_effort": None,
        "submitted_effort": None,
        "effective_model": None,
        "effective_effort": "unknown",
        "effort_status": "pending",
    }
    if context.get("reuse"):
        decision.update(
            profile="reuse",
            reason="Applicable verified skill; no inference requested",
            effective_effort="not_applicable",
            effort_status="no_inference",
        )
        return decision
    if policy == "fixed":
        profile = "fixed"
        selected = settings.get(
            "fixed", {"model_role": config.get("synthesis_role", "general_model"), "effort": "none"}
        )
        reason = "Fixed benchmark model and effort; input novelty and repairs do not change settings"
    else:
        role = context.get("delegated_role")
        if role:
            selected = settings.get("delegated_roles", {}).get(role)
            if not selected or not selected.get("enabled", False):
                raise RoutingError(f"Delegated routing role {role!r} is not enabled and validated")
            profile, reason = f"delegated:{role}", f"Explicit configured delegated role: {role}"
        else:
            profile, reason = "routine", "Known task and contract; routine synthesis"
            for flag, candidate, explanation in (
                (context.get("repair"), "repair", "Repair after an independent executable check failed"),
                (context.get("schema_novelty"), "novel", "Input contract differs from prior skill contracts"),
                (context.get("complexity") == "high", "complex", "Bounded task/evidence/schema complexity"),
                (context.get("task_novelty"), "novel", "No prior skill contract for this task family"),
            ):
                if flag:
                    profile, reason = candidate, explanation
                    break
            selected = settings.get("profiles", {}).get(
                profile,
                {
                    "model_role": config.get("synthesis_role", "general_model"),
                    "effort": "none",
                },
            )
    model = config.get(selected.get("model_role", "general_model"))
    effort = selected.get("effort", "none")
    if not isinstance(model, str) or not model.endswith(":free"):
        raise RoutingError("Routing requires an explicitly configured free model; no paid fallback")
    if effort not in EFFORTS:
        raise RoutingError("Routing effort is not a supported gateway effort value")
    decision.update(profile=profile, reason=reason, requested_model=model, requested_effort=effort)
    return decision


def reasoning_payload(metadata: dict, effort: str) -> tuple[dict | None, str]:
    """Never map unsupported effort to another value or infer support from a model name."""
    if effort not in EFFORTS:
        return None, "Requested effort is not a supported gateway effort value"
    endpoints = metadata.get("endpoints", [])
    if not any("reasoning" in endpoint.get("supported_parameters", []) for endpoint in endpoints):
        return None, "Endpoint metadata does not advertise the reasoning parameter"
    reasoning = metadata.get("reasoning")
    if not isinstance(reasoning, dict):
        return None, "Model metadata does not expose reasoning controls"
    if effort == "none":
        if reasoning.get("mandatory") is True:
            return None, "Model metadata marks reasoning mandatory; disable request suppressed"
        if reasoning.get("mandatory") is False:
            return {"enabled": False}, "Disable control supported by current model and endpoint metadata"
    if "supported_efforts" not in reasoning:
        return None, "Model metadata does not expose effort selection"
    allowed = reasoning["supported_efforts"]
    if allowed is not None and effort not in allowed:
        return None, f"Requested effort {effort!r} is absent from the model effort allowlist"
    return {"effort": effort}, "Effort supported by current model and endpoint metadata"
