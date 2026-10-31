"""Explicit multi-provider gateway, with bounded typed Decisions routing.

Legacy fixed-policy benchmarks use the original free adapter unchanged. This gateway
never executes generated tools or persists private reasoning content.
"""

from __future__ import annotations

import asyncio
import copy
import json
import math
import time

import httpx

from .decision_routing import (
    DecisionResponseError,
    fallback_questions,
    fallback_selection,
    routing_questions,
    validate_answers,
)
from .models import ModelError, OpenRouter
from .monitoring import observe_model
from .provider_reasoning import native_reasoning, output_budget
from .routing import chat_routing_context, policy_name, routing_context


class DecisionModelError(ModelError):
    pass


class ExecutionModelError(ModelError):
    pass


class MultiProvider(OpenRouter):
    def __init__(self, vault, config):
        super().__init__(vault, config)
        self.settings = config["orchestration"]
        self.registry = self.settings["models"]

    async def choose_route(self, sample, task, evidence, failure, metrics):
        if policy_name(self.config, metrics.get("routing_policy")) == "fixed":
            return await super().choose_route(sample, task, evidence, failure, metrics)
        if not self.settings.get("enabled"):
            raise DecisionModelError("Multi-provider routing is disabled")
        context = copy.deepcopy(
            metrics.get("routing_context") or routing_context(sample, task, evidence, repair=bool(failure))
        )
        context["repair"] = bool(failure)
        return await self._decide_route(
            {
                "task": task[:1500],
                "contract": sample["contract"],
                "evidence_count": len(evidence),
                "required_capabilities": ["JSON relational program synthesis"],
                "execution": "No generated tool execution. Independent verification mandatory.",
            },
            context,
            metrics,
        )

    async def choose_chat_route(self, messages, metrics):
        if policy_name(self.config, metrics.get("routing_policy")) == "fixed":
            return await super().choose_chat_route(messages, metrics)
        if not self.settings.get("enabled"):
            raise DecisionModelError("Multi-provider routing is disabled")
        # Include the entire latest message. Older context is bounded separately;
        # no fixture contract, reconciliation instructions or Memory records enter.
        history, remaining = [], 6000
        for message in reversed(messages[:-1]):
            content = message["content"][-remaining:]
            history.insert(0, {"role": message["role"], "content": content})
            remaining -= len(content)
            if remaining <= 0:
                break
        return await self._decide_route(
            {
                "task": messages[-1]["content"],
                "conversation_context": history,
                "required_capabilities": ["Natural-language response to the latest user request"],
                "execution": "Registered native tools may be used in action requests. No generated scripts or sample datasets.",
            },
            chat_routing_context(messages),
            metrics,
            conversation=True,
        )

    async def _decide_route(self, task_state, context, metrics, *, conversation=False):
        candidates = {}
        for key, entry in self.registry.items():
            if entry.get("enabled") and self.vault.get(entry["provider"]):
                candidates[key] = {
                    "model": entry["model"],
                    "provider": entry["provider"],
                    "profile_intent": entry["description"],
                    "reasoning": entry["efforts"],
                    "quality_validation": "pending live verification",
                    "measured_performance": None,
                    "pricing": "not independently metered; no cost-saving guarantee",
                }
        if not candidates:
            raise DecisionModelError("No configured generation credentials; add provider keys locally")
        state = json.dumps(
            {
                **task_state,
                "context": context,
                "candidates": candidates,
                "history": "No comparable multi-provider performance measurements yet",
                "output_budgets": self.settings["output_budgets"],
            }
        )
        descriptions = {key: json.dumps(value) for key, value in candidates.items()}
        attempts = metrics.setdefault("decision_attempts", [])
        selected = None
        for index, model in enumerate(
            (self.settings["decision_primary"], self.settings["decision_fallback"])
        ):
            questions = (
                routing_questions(descriptions, conversation=conversation)
                if index == 0
                else fallback_questions(descriptions)
            )
            attempt = {"model": model, "fallback": bool(index), "status": "pending"}
            attempts.append(attempt)
            try:
                result = await self._call(
                    "openrouter",
                    model,
                    "decisions",
                    {"model": model, "state": state, "questions": questions},
                    metrics,
                    category="decision",
                )
                answers = validate_answers(result, questions)
                selected = (
                    (answers["model"]["choice"], answers["effort"]["choice"])
                    if index == 0
                    else fallback_selection(answers, descriptions)
                )
                attempt.update(status="selected", answers=answers, request_id=result.get("id"))
                break
            except asyncio.CancelledError:
                attempt["status"] = "canceled"
                raise
            except (ModelError, DecisionResponseError) as exc:
                attempt.update(status="failed", error=str(exc))
        if selected is None:
            raise DecisionModelError(
                "Primary and fallback Decisions routing failed; generation was not attempted"
            )
        key, logical = selected
        entry = self.registry[key]
        native, submitted = native_reasoning(entry["provider"], logical)
        return {
            "policy": "adaptive",
            "policy_version": "decisions-model-effort-v2",
            "context": context,
            "profile": key,
            "reason": (
                "Typed Decisions selection from the latest user message and bounded session context"
                if conversation
                else "Typed Decisions selection; probabilities are advisory, independent checks remain mandatory"
            ),
            "decision_model": attempt["model"],
            "decision_answers": attempt["answers"],
            "requested_model": entry["model"],
            "requested_effort": logical,
            "submitted_effort": submitted,
            "native_reasoning": native,
            "provider_adapter": entry["provider"],
            "effective_model": None,
            "effective_effort": "unknown",
            "effort_status": "pending",
        }

    async def request(self, interface, payload, metrics, *, routing=None):
        if routing is None or "provider_adapter" not in routing:
            return await super().request(interface, payload, metrics, routing=routing)
        provider = routing["provider_adapter"]
        body = copy.deepcopy(payload)
        body.pop("reasoning", None)
        body.pop("temperature", None)
        body.pop("max_tokens", None)
        body.update(routing["native_reasoning"])
        budget = output_budget(self.settings, routing["submitted_effort"])
        body["max_completion_tokens" if provider == "mimo" else "max_tokens"] = budget
        body["stream"] = True
        body["stream_options"] = {"include_usage": True}
        if provider == "openrouter":
            await self._openrouter_capabilities(body, routing, budget)
        routing["effort_status"] = "submitted_unconfirmed"
        result = await self._call(
            provider, body["model"], "chat/completions", body, metrics, category="execution", routing=routing
        )
        choices = result.get("choices", [])
        if not choices or choices[0].get("finish_reason") not in (
            {"stop", "tool_calls", None} if body.get("tools") else {"stop", None}
        ):
            raise ExecutionModelError("Generation did not finish normally; no plan admitted")
        if not choices[0].get("message", {}).get("content") and not (
            body.get("tools") and choices[0].get("message", {}).get("tool_calls")
        ):
            raise ExecutionModelError(
                "Generation completed without a final answer; private reasoning is not a plan"
            )
        return result

    async def _openrouter_capabilities(self, body, routing, budget):
        model = body["model"]
        now = time.monotonic()
        if model not in self.metadata or now - self.metadata_checked_at.get(model, 0) > 300:
            try:
                async with httpx.AsyncClient(
                    timeout=self.config["metadata_timeout_seconds"], follow_redirects=False
                ) as client:
                    catalog, endpoints = await asyncio.gather(
                        client.get("https://openrouter.ai/api/v1/models"),
                        client.get(f"https://openrouter.ai/api/v1/models/{model}/endpoints"),
                    )
                    catalog.raise_for_status()
                    endpoints.raise_for_status()
                    item = next(m for m in catalog.json()["data"] if m["id"] == model)
                    self.metadata[model] = (item, endpoints.json()["data"])
                    self.metadata_checked_at[model] = now
            except (httpx.HTTPError, ValueError, KeyError, StopIteration) as exc:
                raise ExecutionModelError(
                    "Current OpenRouter capability metadata unavailable; no generation sent"
                ) from exc
        item, metadata = self.metadata[model]
        effort = routing["submitted_effort"]
        reasoning = item.get("reasoning")
        supported = reasoning.get("supported_efforts") if isinstance(reasoning, dict) else None
        if not isinstance(supported, list) or effort not in supported:
            raise ExecutionModelError(
                "Current metadata does not confirm the requested reasoning effort; no generation sent"
            )
        tags = sorted(
            {
                e["tag"]
                for e in metadata.get("endpoints", [])
                if e.get("tag")
                and (
                    {"reasoning", "max_tokens", "tools"} if body.get("tools") else {"reasoning", "max_tokens"}
                ).issubset(e.get("supported_parameters", []))
                and e.get("max_completion_tokens", 0) >= budget
            }
        )
        if not tags:
            raise ExecutionModelError("No compatible OpenRouter endpoint for reasoning and output budget")
        body["provider"] = {"only": tags, "allow_fallbacks": False, "require_parameters": True}
        routing["capability_reason"] = "Current model effort allowlist and endpoint parameters verified"
        routing["capabilities_checked_at"] = time.time()

    def _url(self, provider, interface):
        if provider == "openrouter":
            return (
                "https://openrouter.ai/api/alpha/decisions"
                if interface == "decisions"
                else "https://openrouter.ai/api/v1/chat/completions"
            )
        if provider == "deepseek":
            return "https://api.deepseek.com/chat/completions"
        base = self.config.get("mimo_base_url", "https://token-plan-sgp.xiaomimimo.com/v1")
        if base not in {"https://token-plan-sgp.xiaomimimo.com/v1", "https://api.xiaomimimo.com/v1"}:
            raise ExecutionModelError("Unapproved MiMo base URL")
        return base + "/chat/completions"

    @observe_model
    async def _call(self, provider, model, interface, body, metrics, *, category, routing=None):
        error_type = DecisionModelError if category == "decision" else ExecutionModelError
        key = self.vault.get(provider)
        if not key:
            raise error_type(f"{provider} credential missing")
        limit = 25 if metrics.get("connector_turn") else self.settings.get("max_total_calls", 7)
        category_limit = (
            self.settings.get("max_decision_calls", 4)
            if category == "decision"
            else 24
            if metrics.get("connector_turn")
            else 6
            if metrics.get("agent_tools")
            else self.config["max_model_calls_per_run"]
        )
        count = sum(c.get("category") == category for c in metrics["calls"])
        if metrics["model_calls"] >= limit or count >= category_limit:
            raise error_type("Bounded run request budget exhausted")
        call = {
            "category": category,
            "model": model,
            "requested_model": model,
            "provider_adapter": provider,
            "interface": interface,
            "status": "pending",
            "requested_effort": (routing or {}).get("requested_effort"),
            "submitted_effort": (routing or {}).get("submitted_effort"),
            "native_reasoning": copy.deepcopy((routing or {}).get("native_reasoning")),
            "effective_effort": "unknown",
            "effective_model": None,
        }
        metrics["calls"].append(call)
        metrics["model_calls"] += 1
        start = time.perf_counter()
        timeout = (
            self.settings.get("decision_timeout_seconds", 20)
            if category == "decision"
            else self.config["request_timeout_seconds"]
        )
        try:
            async with (
                asyncio.timeout(timeout),
                httpx.AsyncClient(timeout=timeout, follow_redirects=False) as client,
            ):
                async with client.stream(
                    "POST",
                    self._url(provider, interface),
                    headers={"Authorization": f"Bearer {key}"},
                    json=body,
                ) as response:
                    if response.status_code != 200:
                        call["http_status"] = response.status_code
                        # Never expose provider response text: it can echo any input/credential.
                        raise error_type(
                            f"{category} provider HTTP {response.status_code}; explicit retry required"
                        )
                    if body.get("stream"):
                        result = await self._read_stream(response, allow_tools=bool(body.get("tools")))
                    else:
                        raw = await response.aread()
                        if len(raw) > 2_000_000:
                            raise error_type("Decision response exceeds size bound")
                        result = json.loads(raw)
            if not isinstance(result, dict) or result.get("error"):
                raise error_type("Invalid provider response")
            call.update(
                status="received",
                effective_model=result.get("model"),
                provider=result.get("provider", provider),
                request_id=result.get("id"),
            )
            usage = result.get("usage")
            self._usage(usage, metrics, call, category)
            if routing is not None:
                routing.update(
                    effective_model=result.get("model"),
                    provider=result.get("provider", provider),
                    request_id=result.get("id"),
                    usage=copy.deepcopy(usage),
                    effort_status="submitted_unconfirmed",
                )
                call["routing"] = copy.deepcopy(routing)
            return result
        except asyncio.CancelledError:
            call["status"] = "canceled"
            metrics["usage_complete"] = metrics["cost_reported"] = False
            raise
        except (TimeoutError, httpx.HTTPError, ValueError, KeyError, TypeError, ModelError) as exc:
            call["status"] = "failed"
            metrics["usage_complete"] = metrics["cost_reported"] = False
            if isinstance(exc, ModelError):
                raise
            raise error_type(
                f"{category} transport, timeout or response validation failure; usage unconfirmed"
            ) from None
        finally:
            call["latency_ms"] = (time.perf_counter() - start) * 1000
            metrics["model_latency_ms"] += call["latency_ms"]

    @staticmethod
    def _usage(usage, metrics, call, category):
        if not isinstance(usage, dict):
            metrics["usage_complete"] = metrics["cost_reported"] = False
            return
        call["usage"] = {
            k: usage[k]
            for k in (
                "input_tokens",
                "output_tokens",
                "prompt_tokens",
                "completion_tokens",
                "cost",
                "completion_tokens_details",
            )
            if k in usage
        }
        for field, source in (
            ("input_tokens", "input_tokens" if category == "decision" else "prompt_tokens"),
            ("output_tokens", "output_tokens" if category == "decision" else "completion_tokens"),
        ):
            value = usage.get(source)
            if isinstance(value, int) and not isinstance(value, bool) and value >= 0:
                metrics[field] += value
            else:
                metrics["usage_complete"] = False
        cost = usage.get("cost")
        if (
            isinstance(cost, (int, float))
            and not isinstance(cost, bool)
            and math.isfinite(cost)
            and cost >= 0
        ):
            metrics["billed_cost_usd"] += cost
        else:
            metrics["cost_reported"] = False
        details = usage.get("completion_tokens_details")
        reasoning = details.get("reasoning_tokens", 0) if isinstance(details, dict) else 0
        if isinstance(reasoning, int) and reasoning >= 0:
            metrics["reasoning_tokens"] += reasoning

    @staticmethod
    async def _read_stream(response, *, allow_tools=False):
        text = []
        continuation = []
        size = 0
        result = {}
        finish = None
        done = False
        tool_calls = {}
        async for line in response.aiter_lines():
            size += len(line)
            if size > 4_000_000:
                raise ExecutionModelError("Stream exceeds bounded output size")
            if not line.startswith("data:"):
                continue
            data = line[5:].strip()
            if data == "[DONE]":
                done = True
                break
            chunk = json.loads(data)
            if chunk.get("error"):
                raise ExecutionModelError("Provider stream reported an error")
            for key in ("id", "model", "provider", "usage"):
                if chunk.get(key) is not None:
                    result[key] = chunk[key]
            for choice in chunk.get("choices", []):
                if choice.get("index", 0) != 0:
                    continue
                delta = choice.get("delta", {})
                # Provider continuation required by thinking-mode function calling. Ephemeral
                # transport only: never returned as visible content, audit or saved chat state.
                if allow_tools and isinstance(delta.get("reasoning_content"), str):
                    continuation.append(delta["reasoning_content"])
                if delta.get("tool_calls"):
                    if not allow_tools:
                        raise ExecutionModelError(
                            "Generated tool calls are outside the bounded plan interface"
                        )
                    for part in delta["tool_calls"]:
                        index = part.get("index", 0)
                        if not isinstance(index, int) or not 0 <= index < 12:
                            raise ExecutionModelError("Tool-call count exceeds budget")
                        call = tool_calls.setdefault(
                            index, {"id": "", "type": "function", "function": {"name": "", "arguments": ""}}
                        )
                        if part.get("id"):
                            call["id"] = part["id"]
                        function = part.get("function", {})
                        for key in ("name", "arguments"):
                            if function.get(key):
                                call["function"][key] += function[key]
                # Empty visible deltas and private reasoning deltas are normal while thinking.
                if isinstance(delta.get("content"), str):
                    text.append(delta["content"])
                if choice.get("finish_reason"):
                    finish = choice["finish_reason"]
        if not done or finish is None:
            raise ExecutionModelError("Incomplete provider stream; usage unconfirmed")
        message = {"content": "".join(text)}
        if tool_calls:
            message["tool_calls"] = [tool_calls[i] for i in sorted(tool_calls)]
        if allow_tools and continuation:
            message["reasoning_content"] = "".join(continuation)
        result["choices"] = [{"message": message, "finish_reason": finish}]
        return result
