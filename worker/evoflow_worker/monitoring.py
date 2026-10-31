"""Optional, metadata-only LangSmith monitoring using explicit SDK run trees.

Never pass a task, transcript, fixture, plan, tool payload, credential or exception
text to this module. Automatic framework tracing is suppressed even when the host
environment enables it: sealed evaluations and raw product data must stay local.
"""

from __future__ import annotations

import asyncio
import inspect
import math
from contextlib import contextmanager
from contextvars import ContextVar
from functools import wraps

from langsmith import Client, tracing_context
from langsmith.run_trees import RunTree

_active = ContextVar("evoflow_monitoring_span", default=None)


def _number(value, *, integer=False):
    return (
        isinstance(value, int if integer else (int, float))
        and not isinstance(value, bool)
        and (isinstance(value, int) or math.isfinite(value))
        and value >= 0
    )


def usage_metadata(calls):
    """Only provider receipts; absent counts/costs never become measured zeroes."""
    result = {}
    complete = bool(calls)
    cost_complete = bool(calls)
    for call in calls:
        usage = call.get("usage")
        usage = usage if isinstance(usage, dict) else {}
        for target, sources in (
            ("input_tokens", ("input_tokens", "prompt_tokens")),
            ("output_tokens", ("output_tokens", "completion_tokens")),
        ):
            value = next((usage[k] for k in sources if k in usage), None)
            if _number(value, integer=True):
                result[target] = result.get(target, 0) + value
            else:
                complete = False
        cost = usage.get("cost")
        if _number(cost):
            result["total_cost"] = result.get("total_cost", 0) + cost
        else:
            cost_complete = False
        details = usage.get("completion_tokens_details")
        reasoning = details.get("reasoning_tokens") if isinstance(details, dict) else None
        if _number(reasoning, integer=True):
            result.setdefault("output_token_details", {}).setdefault("reasoning", 0)
            result["output_token_details"]["reasoning"] += reasoning
    # Partial receipts remain metadata, not complete billable run totals.
    if not complete:
        result = {k: v for k, v in result.items() if k not in {"input_tokens", "output_tokens"}}
    if not cost_complete:
        result.pop("total_cost", None)
    if complete:
        result["total_tokens"] = result["input_tokens"] + result["output_tokens"]
    return result, complete, cost_complete


class Monitoring:
    def __init__(self, vault, config):
        self.project = config.get("project", "EvoFlow")
        self.client = None
        self.delivery_failed = False
        self.enabled = bool(config.get("enabled"))
        if self.enabled:
            try:
                key = vault.get("langsmith")
                if not key:
                    return
                self.client = Client(
                    api_key=key,
                    api_url=config.get("endpoint", "https://api.smith.langchain.com"),
                    workspace_id=config.get("workspace_id"),
                    timeout_ms=3000,
                    omit_traced_runtime_info=True,
                    tracing_error_callback=lambda _: self._failed(),
                )
            except Exception:
                self._failed()

    def _failed(self):
        self.delivery_failed = True

    def status(self):
        return {
            "enabled": self.enabled,
            "configured": self.client is not None,
            "project": self.project,
            "delivery": "unconfirmed" if not self.delivery_failed else "failed; app continues locally",
            "content": "operational metadata only",
        }

    @contextmanager
    def span(self, name, *, run_type="chain", metadata=None):
        run, marker = None, None
        if self.client is not None:
            try:
                parent = _active.get()
                # Session correlation is explicitly copied onto each LLM child.
                inherited = dict(parent[1].extra.get("metadata", {})) if parent and parent[0] is self else {}
                details = {**inherited, **(metadata or {})}
                if parent and parent[0] is self:
                    run = parent[1].create_child(name, run_type, inputs={}, extra={"metadata": details})
                else:
                    run = RunTree(
                        name=name,
                        run_type=run_type,
                        inputs={},
                        project_name=self.project,
                        ls_client=self.client,
                        extra={"metadata": details},
                        tags=["evoflow", "metadata-only"],
                    )
                run.post()
                marker = _active.set((self, run))
            except Exception:
                self._failed()
                run = None
        try:
            yield run
        except BaseException as exc:
            if run is not None:
                # SDK context managers can capture traceback text; never send it.
                self.finish(
                    run, error="canceled" if isinstance(exc, asyncio.CancelledError) else "operation failed"
                )
            raise
        finally:
            if marker is not None:
                _active.reset(marker)
            if run is not None:
                try:
                    if run.end_time is None:
                        run.end(outputs={})
                    run.patch()
                except Exception:
                    self._failed()

    def finish(self, run, *, outputs=None, metadata=None, error=None):
        if run is not None:
            try:
                run.end(outputs=outputs or {}, metadata=metadata, error=error)
            except Exception:
                self._failed()

    def flush(self):
        if self.client is not None:
            try:
                self.client.flush(timeout=2)
            except Exception:
                self._failed()


def observe_run(kind):
    """Observe workflow/conversation lifetime without observing function arguments."""

    def decorate(function):
        @wraps(function)
        async def wrapped(self, *args, **kwargs):
            monitor = getattr(self.model, "monitoring", None)
            with tracing_context(enabled=False):
                if monitor is None:
                    return await function(self, *args, **kwargs)
                if kind == "conversation":
                    record = args[0] if args else kwargs["record"]
                    identifier, session = record["id"], record["session_id"]
                else:
                    import uuid

                    identifier = kwargs.get("run_id") or str(uuid.uuid4())
                    kwargs["run_id"] = identifier
                    session = kwargs.get("session_id") or identifier
                with monitor.span(
                    f"EvoFlow · {kind}",
                    metadata={
                        "evoflow_run_id": identifier,
                        "session_id": session,
                        "thread_id": session,
                        "run_kind": kind,
                        "content_capture": False,
                    },
                ) as span:
                    result = await function(self, *args, **kwargs)
                    metrics = result.get("metrics", {})
                    output = {k: result[k] for k in ("status", "reused", "skill_version") if k in result}
                    output.update(
                        {
                            k: metrics[k]
                            for k in (
                                "model_calls",
                                "tool_calls",
                                "usage_complete",
                                "cost_reported",
                                "end_to_end_ms",
                            )
                            if k in metrics
                        }
                    )
                    if result.get("verification"):
                        output["verification_passed"] = bool(result["verification"].get("passed"))
                    monitor.finish(
                        span,
                        outputs=output,
                        error=None if result.get("status") == "complete" else "operation incomplete",
                    )
                    return result

        return wrapped

    return decorate


def observe_model(function):
    """Observe adapter calls, including streaming, failure and legacy retry receipts."""
    signature = inspect.signature(function)

    @wraps(function)
    async def wrapped(self, *args, **kwargs):
        monitor = getattr(self, "monitoring", None)
        with tracing_context(enabled=False):
            if monitor is None or monitor.client is None:
                return await function(self, *args, **kwargs)
            arguments = signature.bind(self, *args, **kwargs).arguments
            metrics = arguments["metrics"]
            before = len(metrics["calls"])
            model = arguments.get("model") or arguments.get("payload", {}).get("model")
            provider = arguments.get("provider", "openrouter")
            category = arguments.get("category") or (
                "decision" if arguments["interface"] == "decisions" else "execution"
            )
            with monitor.span(
                f"EvoFlow · {category} · {model}",
                run_type="llm",
                metadata={
                    "ls_provider": provider,
                    "ls_model_name": model,
                    "category": category,
                    "cost_source": "provider receipt only; dashboard prices may be estimates",
                },
            ) as span:
                try:
                    return await function(self, *args, **kwargs)
                finally:
                    calls = metrics["calls"][before:]
                    usage, complete, cost_complete = usage_metadata(calls)
                    last = calls[-1] if calls else {}
                    details = {
                        k: last[k]
                        for k in (
                            "effective_model",
                            "provider",
                            "requested_effort",
                            "submitted_effort",
                            "effective_effort",
                            "http_status",
                        )
                        if k in last
                    }
                    details.update(attempts=len(calls), usage_complete=complete, cost_reported=cost_complete)
                    monitor.finish(span, outputs={"usage_metadata": usage}, metadata=details)

    return wrapped
