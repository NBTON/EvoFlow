from __future__ import annotations

import asyncio
import json
import time
import uuid
from threading import Event
from typing import TypedDict

from langgraph.checkpoint.sqlite.aio import AsyncSqliteSaver
from langgraph.graph import END, START, StateGraph

from .domain import Dataset, Plan, digest, verify
from .fixtures import development_cases
from .interpreter import execute
from .models import OpenRouter, new_metrics
from .monitoring import observe_run
from .routing import POLICY_VERSION, policy_name, routing_context, select_route
from .store import Store


class RunState(TypedDict, total=False):
    run_id: str
    input_ref: str
    contract: dict
    evidence_ids: list[str]
    skill_id: str | None
    plan: dict
    attempts: int
    error: str | None
    result_ref: str
    verified: bool


class Engine:
    def __init__(self, store: Store, model: OpenRouter):
        self.store, self.model = store, model
        self.cancellations: dict[str, Event] = {}

    @observe_run("workflow")
    async def run(
        self,
        data: Dataset,
        task="Reconcile orders, inventory and shipments.",
        *,
        mode="evolving",
        scope="local",
        workspace="demo",
        run_id=None,
        session_id=None,
        memory=True,
        learning=True,
        routing_policy=None,
        delegated_role=None,
    ):
        if mode not in {"none", "frozen", "evolving"}:
            raise ValueError("Unknown benchmark condition")
        run_id = run_id or str(uuid.uuid4())
        cancel = self.cancellations.setdefault(run_id, Event())
        started = time.perf_counter()
        metrics = new_metrics()
        metrics["routing_policy"] = policy_name(self.model.config, routing_policy)
        folder = self.store.root / "artifacts" / run_id
        folder.mkdir(parents=True, exist_ok=True)
        input_path = folder / "input.json"
        input_path.write_text(data.model_dump_json(indent=2), encoding="utf-8")
        record = dict(
            id=run_id,
            session_id=session_id,
            status="working",
            task=task[:1500],
            mode=mode,
            live=True,
            started_at=time.time(),
            fixture_hash=digest(data.model_dump()),
            contract=data.contract(),
            metrics=metrics,
            result=[],
            verification=None,
            routing={
                "policy": metrics["routing_policy"],
                "policy_version": self.model.config.get("routing", {}).get("policy_version", POLICY_VERSION),
                "decisions": metrics["routing_decisions"],
            },
            sync_status="local" if scope == "local" else "queued",
        )
        self.store.put("run", record, scope, workspace)
        evidence, rows, chosen, parent, fresh = [], [], None, None, False

        def event(kind, message, **extra):
            if cancel.is_set():
                raise asyncio.CancelledError()
            return self.store.event(run_id, kind, message, scope, workspace, **extra)

        def persist():
            record["metrics"] = metrics
            self.store.put("run", record, scope, workspace)

        async def inspect(state):
            event("inspect", "Inspecting input schemas and business-rule versions")
            metrics["tool_calls"] += 1
            # This preflight rejects ambiguous identities before any model expenditure.
            from .domain import oracle

            oracle(data)
            return {"contract": data.contract(), "attempts": 0}

        async def retrieve(state):
            nonlocal evidence, chosen, parent
            event("retrieve", "Retrieving scoped policy evidence and eligible workflow versions")
            if memory:
                metrics["retrieval_queries"] += 1
                metrics["tool_calls"] += 1
                evidence = self.store.retrieve(task, scope, workspace, self.model.config["retrieval_limit"])
            active = next(
                (s for s in self.store.list("skill", scope, workspace) if s["state"] == "active"), None
            )
            parent = active
            key = str(data.schema_version)
            dependency_hash = digest([e["content_hash"] for e in evidence if e["record_type"] == "knowledge"])
            if not memory and active:
                # Metadata-only invalidation remains on when retrieval/reflection is disabled.
                # This permits a paired memory ablation without also disabling procedural reuse.
                current = {
                    e["key"]: e["content_hash"]
                    for e in self.store.list("knowledge", scope, workspace)
                    if e["state"] == "current"
                }
                dependency_hash = digest(
                    [current.get(e["key"]) for e in active.get("knowledge_dependencies", [])]
                )
            if mode != "none" and active and key in active["plans"]:
                if (
                    active["contracts"].get(key) == data.contract()
                    and active.get("knowledge_hash") == dependency_hash
                ):
                    chosen = active
                else:
                    event(
                        "invalidation",
                        "Saved workflow assumptions changed; reuse blocked",
                        skill_id=active["id"],
                    )
            elif active and mode != "none":
                # Diagnostic only: independently check the old artifact against the new schema.
                old = next(iter(active["plans"].values()))
                try:
                    diagnostic, duration = execute(Plan.model_validate(old), data, cancel)
                    metrics["local_execution_ms"] += duration
                    check = verify(data, diagnostic)
                    passed = check["passed"]
                except (ValueError, Exception) as exc:
                    passed = False
                    diagnostic = type(exc).__name__
                metrics["tool_calls"] += 1
                event(
                    "invalidation",
                    "Old workflow failed the independent schema/applicability check; adaptation needed",
                    skill_id=active["id"],
                    diagnostic_passed=passed,
                )
            context = routing_context(
                data.prompt_sample(),
                task,
                evidence,
                known_contracts=(parent or {}).get("contracts", {}).values(),
                delegated_role=delegated_role,
            )
            metrics["routing_context"] = context
            if chosen:
                context["reuse"] = True
                metrics["routing_decisions"].append(
                    select_route(self.model.config, context, metrics["routing_policy"])
                )
            return {
                "evidence_ids": [e["id"] for e in evidence],
                "skill_id": chosen["id"] if chosen else None,
                "plan": chosen["plans"][key] if chosen else {},
            }

        async def synthesize(state):
            nonlocal fresh
            fresh = True
            event("synthesis", "Synthesizing an executable read-only workflow from the actual task")
            failure = (
                {"error": state.get("error"), "failed_artifact": state.get("plan")}
                if state.get("error")
                else None
            )
            metrics["routing_context"] = dict(
                metrics["routing_context"],
                reuse=False,
                repair=bool(failure),
            )
            try:
                plan = await self.model.synthesize(data.prompt_sample(), task, evidence, failure, metrics)
                return {
                    "plan": plan.model_dump(),
                    "attempts": state["attempts"] + 1,
                    "error": None,
                    "skill_id": None,
                }
            finally:
                persist()

        async def run_plan(state):
            nonlocal rows
            event(
                "reuse" if not fresh else "execute",
                "Reusing a saved skill"
                if not fresh
                else "Executing the synthesized workflow inside the closed query interpreter",
            )
            metrics["tool_calls"] += 1
            try:
                rows, duration = await asyncio.to_thread(
                    execute, Plan.model_validate(state["plan"]), data, cancel
                )
                metrics["local_execution_ms"] += duration
                check = await asyncio.to_thread(verify, data, rows)
                metrics["verification_cases"] += 1
                record["verification"] = check
                if not check["passed"]:
                    # The model receives a bounded failed-check summary, never final-test expected answers.
                    return {
                        "verified": False,
                        "error": "Independent reconciliation check failed: "
                        "deduplicate before joins; aggregate distinct shipments and honor the exact output contract.",
                    }
                result_path = folder / "result.json"
                result_path.write_text(json.dumps(rows, indent=2), encoding="utf-8")
                return {"verified": True, "result_ref": str(result_path), "error": None}
            except (ValueError, RuntimeError, Exception) as exc:
                return {"verified": False, "error": f"Executable plan rejected: {str(exc)[:250]}"}

        async def evaluate(state):
            nonlocal parent
            if not fresh or mode == "none" or not learning:
                return {}
            event(
                "evaluation",
                "Testing the candidate on independent development cases and old-case regressions",
            )
            current_schema = str(data.schema_version)
            plans = dict(parent["plans"]) if parent else {}
            contracts = dict(parent["contracts"]) if parent else {}
            plans[current_schema], contracts[current_schema] = state["plan"], data.contract()
            evaluations = []
            for schema_key, plan in plans.items():
                policy = (
                    data.policy
                    if schema_key == current_schema
                    else Dataset.model_validate(
                        json.loads(
                            (self.store.root / "artifacts" / parent["source_run"] / "input.json").read_text()
                        )
                    ).policy
                )
                for case in development_cases(int(schema_key), policy, split=True):
                    if cancel.is_set():
                        raise asyncio.CancelledError()
                    try:
                        output, duration = await asyncio.to_thread(
                            execute, Plan.model_validate(plan), case, cancel
                        )
                        metrics["local_execution_ms"] += duration
                        passed = verify(case, output)["passed"]
                    except Exception:
                        passed = False
                    metrics["verification_cases"] += 1
                    metrics["tool_calls"] += 1
                    evaluations.append(
                        {
                            "fixture_hash": digest(case.model_dump()),
                            "schema": schema_key,
                            "passed": passed,
                            "partition": "development",
                        }
                    )
            admitted = all(e["passed"] for e in evaluations) and metrics["model_calls"] > 0
            version = max([s["version"] for s in self.store.list("skill", scope, workspace)], default=0) + 1
            candidate = dict(
                id=str(uuid.uuid4()),
                version=version,
                task_family="order-reconciliation",
                title=state["plan"]["title"],
                state="candidate",
                source_run=run_id,
                parent_version=parent["id"] if parent else None,
                plans=plans,
                contracts=contracts,
                artifact_hash=digest(plans),
                parameters=["as_of", "grace_days"],
                input_contract=contracts,
                output_contract=sorted(rows[0]) if rows else [],
                assumptions=[
                    "One SKU per order",
                    "Conflicting duplicates unsupported",
                    "Exact policy versions",
                ],
                dependencies=["closed SQLite SELECT interpreter v1"],
                independent_checks=evaluations,
                triggering_evidence=state.get("error") or "Verified actual task",
                knowledge_hash=digest(
                    [e["content_hash"] for e in evidence if e["record_type"] == "knowledge"]
                ),
                knowledge_dependencies=[
                    {"key": e["key"], "content_hash": e["content_hash"]}
                    for e in evidence
                    if e["record_type"] == "knowledge"
                ],
                observed_performance=dict(metrics),
                policy_versions={k: c["policy_version"] for k, c in contracts.items()},
                admission={
                    "passed": admitted,
                    "quality": "all development checks must pass",
                    "efficiency": "zero inference calls on covered reuse vs measured synthesis >0",
                    "reuse_model_calls": 0,
                    "acquisition_model_calls": metrics["model_calls"],
                },
            )
            self.store.put("skill", candidate, scope, workspace)
            if admitted:
                candidate = self.store.promote(candidate, scope, workspace)
                record["skill_id"] = candidate["id"]
                record["skill_version"] = candidate["version"]
                event("admission", f"Skill v{version} promoted after {len(evaluations)} development checks")
            else:
                self.store.put("skill", dict(candidate, state="rejected"), scope, workspace)
                event(
                    "rejection",
                    "Candidate rejected: development checks found a regression; prior version retained",
                )
            return {}

        async def complete(state):
            nonlocal rows
            event("verified", "Independent correctness checks passed")
            # Cheap bounded preference extraction: explicit future intent only, supported format only.
            if memory and "for future reports" in task.lower() and "warehouse" in task.lower():
                self.store.remember(
                    kind="preference",
                    key="report-format",
                    content=task[:1500],
                    source=f"run:{run_id}",
                    author=scope,
                    explicit=True,
                    scope=scope,
                    workspace=workspace,
                )
                evidence = self.store.retrieve("report warehouse unresolved", scope, workspace)
            else:
                evidence = (
                    self.store.retrieve("report warehouse unresolved", scope, workspace) if memory else []
                )
            grouped = any(
                e["record_type"] == "preference" and "warehouse" in e["content"].lower() for e in evidence
            )
            rows = (
                sorted(rows, key=lambda r: (r["warehouse"], -r["remaining"], r["order_id"]))
                if grouped
                else rows
            )
            report = [
                "# EvoFlow reconciliation",
                "",
                "Live execution; independently verified.",
                f"Orders: {len(rows)} | Delayed: {sum(r['delayed'] for r in rows)} | "
                f"Incomplete: {sum(r['remaining'] > 0 for r in rows)}",
                "",
                "Grouped by warehouse; unresolved first." if grouped else "Order-level results.",
                "",
                "| Order | Warehouse | Ordered | Shipped | Remaining | Delayed | Evidence-based cause |",
                "|---|---|---:|---:|---:|---|---|",
            ]
            report += [
                f"| {r['order_id']} | {r['warehouse']} | {r['ordered']} | {r['shipped']} | "
                f"{r['remaining']} | {'Yes' if r['delayed'] else 'No'} | {r['cause']} |"
                for r in rows
            ]
            report_path = folder / "report.md"
            report_path.write_text("\n".join(report), encoding="utf-8")
            record.update(
                status="complete",
                result=rows,
                report="\n".join(report),
                report_path=str(report_path),
                grouped_by_warehouse=grouped,
                evidence_ids=state.get("evidence_ids", []),
                reused=not fresh,
                plan=state["plan"],
                skill_id=record.get("skill_id") or state.get("skill_id"),
            )
            if chosen and not fresh:
                record["skill_version"] = chosen["version"]
            return {}

        builder = StateGraph(RunState)
        for name, node in (
            ("inspect", inspect),
            ("retrieve", retrieve),
            ("synthesize", synthesize),
            ("execute", run_plan),
            ("evaluate", evaluate),
            ("complete", complete),
        ):
            builder.add_node(name, node)
        builder.add_edge(START, "inspect")
        builder.add_edge("inspect", "retrieve")
        builder.add_conditional_edges("retrieve", lambda s: "execute" if s["plan"] else "synthesize")
        builder.add_edge("synthesize", "execute")

        def after_execution(state):
            if state["verified"]:
                return "evaluate"
            if (
                learning
                and state["attempts"] < self.model.config["max_model_calls_per_run"]
                and metrics["model_calls"]
                < (
                    self.model.config.get("orchestration", {}).get(
                        "max_total_calls", self.model.config["max_model_calls_per_run"]
                    )
                    if metrics.get("decision_attempts")
                    else self.model.config["max_model_calls_per_run"]
                )
            ):
                return "synthesize"
            raise ValueError(state["error"] or "Verification failed")

        builder.add_conditional_edges("execute", after_execution)
        builder.add_edge("evaluate", "complete")
        builder.add_edge("complete", END)
        try:
            async with AsyncSqliteSaver.from_conn_string(str(self.store.root / "checkpoints.db")) as saver:
                graph = builder.compile(checkpointer=saver)
                await graph.ainvoke(
                    {"run_id": run_id, "input_ref": str(input_path)},
                    {"configurable": {"thread_id": run_id}, "recursion_limit": 16},
                )
        except asyncio.CancelledError:
            record.update(status="canceled", error="Canceled; no unverified result was published")
            self.store.event(run_id, "canceled", "Run canceled", scope, workspace)
        except Exception as exc:
            record.update(status="needs_attention", error=str(exc)[:700])
            self.store.event(run_id, "error", record["error"], scope, workspace)
        finally:
            metrics["end_to_end_ms"] = (time.perf_counter() - started) * 1000
            record["finished_at"] = time.time()
            persist()
            self.cancellations.pop(run_id, None)
        return record

    def cancel(self, run_id):
        if run_id in self.cancellations:
            self.cancellations[run_id].set()
