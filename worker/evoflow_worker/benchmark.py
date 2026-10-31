from __future__ import annotations

import json
import os
import statistics
from pathlib import Path

from .credentials import Vault
from .domain import digest
from .engine import Engine
from .fixtures import fixture
from .models import OpenRouter, new_metrics
from .store import Store

REPO = Path(__file__).resolve().parents[2]


async def main(command, root, repeats):
    root.mkdir(parents=True, exist_ok=True)
    config = json.loads((REPO / "config" / "runtime.json").read_text())
    # All runs use the current user's protected app credential, while experimental
    # records/checkpoints/libraries remain isolated under the experiment directory.
    router = OpenRouter(Vault(Path(os.environ["LOCALAPPDATA"]) / "EvoFlow"), config)
    if command == "probe":
        evidence = {"live": True, "models": {}}
        for role in ("general_model", "code_model", "decision_model"):
            model = config[role]
            metrics = new_metrics()
            try:
                metadata = await router.inspect(model)
                if role == "decision_model":
                    result = await router.score(
                        "Attempt 1 failed: missing field. Attempt 2 repeated the same "
                        "query without any changes or new evidence.",
                        metrics,
                    )
                    response_summary = {k: v for k, v in result.items() if k not in {"usage"}}
                else:
                    result = await router.request(
                        "chat/completions",
                        {
                            "model": model,
                            "max_tokens": 32,
                            "temperature": 0,
                            "reasoning": {"enabled": False},
                            "messages": [{"role": "user", "content": "Reply exactly: EvoFlow ready"}],
                        },
                        metrics,
                    )
                    response_summary = {"text": result["choices"][0]["message"].get("content", "")}
                evidence["models"][role] = {
                    "available": True,
                    "metadata": metadata,
                    "response": response_summary,
                    "metrics": metrics,
                }
            except Exception as exc:
                evidence["models"][role] = {"available": False, "error": str(exc), "metrics": metrics}
            print(json.dumps({"role": role, "available": evidence["models"][role]["available"]}), flush=True)
        (root / "model-capabilities.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
        return
    if command == "demo":
        store = Store(root)
        engine = Engine(store, router)
        runs = []
        for stage, data in enumerate(
            [
                fixture(21),
                fixture(22),
                fixture(23, schema=2, split=True),
                fixture(24, schema=2, split=True, grace=2),
            ],
            1,
        ):
            store.remember(
                kind="knowledge",
                key="shipping-policy",
                content=f"Fictional rule v{data.policy.version}: "
                f"count shipped/delivered units; {data.policy.grace_days} grace days; distinguish inventory evidence.",
                source=f"fictional-policy-v{data.policy.version}",
                author="Fictional Operations Team",
                authoritative=True,
            )
            task = "Reconcile orders, inventory and shipments."
            if stage == 1:
                task += " For future reports, group delays by warehouse and show unresolved orders first."
            run = await engine.run(data, task)
            runs.append(run)
            print(
                json.dumps(
                    {
                        "stage": stage,
                        "status": run["status"],
                        "calls": run["metrics"]["model_calls"],
                        "reused": run.get("reused"),
                        "skill": run.get("skill_version"),
                        "error": run.get("error"),
                    }
                ),
                flush=True,
            )
        evidence = {
            "live": True,
            "runs": runs,
            "skills": store.list("skill"),
            "events": {r["id"]: store.events(r["id"]) for r in runs},
        }
        (root / "demo-evidence.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
        if any(r["status"] != "complete" for r in runs):
            raise SystemExit("Live demo incomplete; see evidence")
        return
    if not 1 <= repeats <= 3:
        raise ValueError("Benchmark repeats must be between 1 and 3")
    evidence = {
        "live": True,
        "preliminary": True,
        "configuration": config,
        "routing_policy": "fixed",
        "routing_comparison": "All conditions use the same configured model and reasoning profile",
        "objective": "Maintain exact oracle correctness; reduce measured inference per successful task",
        "partitions": {
            "learning": [201, 202],
            "development": [101, 102, 103, 104],
            "sealed_final": [9001 + i for i in range(repeats * 3)],
        },
        "conditions": {},
        "notice": "Free vs free is not dollar savings; tokens are not hardware compute.",
    }
    for mode in ("none", "frozen", "evolving"):
        store = Store(root / mode)
        if store.list("run"):
            raise ValueError("Benchmark requires a fresh root for isolated acquisition accounting")
        engine = Engine(store, router)
        store.remember(
            kind="knowledge",
            key="shipping-policy",
            content="Fictional shipping policy v1: count shipped "
            "and delivered units, sum split shipments, exclude canceled units. Inventory is availability evidence.",
            source="fictional-policy-v1",
            author="Fictional Operations Team",
            authoritative=True,
        )
        store.remember(
            kind="preference",
            key="report-format",
            content="For future reports group delays by warehouse and show unresolved orders first.",
            source="explicit benchmark preference",
            author="benchmark",
            explicit=True,
        )
        runs = []
        # B freezes after first learned workflow; C may learn the changed schema.
        for index, data in enumerate([fixture(201), fixture(202, schema=2, split=True)]):
            run = await engine.run(
                data, mode=mode, learning=(mode != "frozen" or index == 0), routing_policy="fixed"
            )
            run["partition"] = "learning"
            runs.append(run)
            print(
                json.dumps(
                    {
                        "condition": mode,
                        "partition": "learning",
                        "status": run["status"],
                        "calls": run["metrics"]["model_calls"],
                        "error": run.get("error"),
                    }
                ),
                flush=True,
            )
        freeze_hash = digest(store.list("skill"))
        # Candidates are now frozen. Final fixture answers never enter generation prompts,
        # repair, development selection, or memory retention. Verification is reporting only.
        for index in range(repeats * 3):
            data = fixture(9001 + index, schema=2 if index % 3 == 2 else 1, split=index % 3 == 2)
            run = await engine.run(data, mode=mode, learning=False, routing_policy="fixed")
            run["partition"] = "sealed_final"
            runs.append(run)
            print(
                json.dumps(
                    {
                        "condition": mode,
                        "partition": "sealed_final",
                        "status": run["status"],
                        "calls": run["metrics"]["model_calls"],
                        "error": run.get("error"),
                    }
                ),
                flush=True,
            )
        if digest(store.list("skill")) != freeze_hash:
            raise AssertionError("Final test altered the frozen skill library")
        totals = {
            field: sum(r["metrics"].get(field, 0) for r in runs)
            for field in (
                "model_calls",
                "input_tokens",
                "output_tokens",
                "reasoning_tokens",
                "retries",
                "tool_calls",
                "local_execution_ms",
                "end_to_end_ms",
                "billed_cost_usd",
                "verification_cases",
                "retrieval_queries",
            )
        }
        final = [r for r in runs if r["partition"] == "sealed_final"]
        totals.update(
            tasks=len(runs),
            successes=sum(r["status"] == "complete" for r in runs),
            final_tasks=len(final),
            final_successes=sum(r["status"] == "complete" for r in final),
            final_latency_mean_ms=statistics.mean(r["metrics"]["end_to_end_ms"] for r in final),
            final_latency_stdev_ms=statistics.stdev(r["metrics"]["end_to_end_ms"] for r in final)
            if len(final) > 1
            else 0,
            usage_complete=all(r["metrics"]["usage_complete"] for r in runs),
            cost_reported=all(r["metrics"]["cost_reported"] for r in runs),
        )
        evidence["conditions"][mode] = {
            "runs": runs,
            "totals": totals,
            "freeze_hash": freeze_hash,
            "skills": store.list("skill"),
            "events": {r["id"]: store.events(r["id"]) for r in runs},
        }
    # Memory is measured separately with a paired same-dataset execution. No model calls
    # are attributed to it when an already-frozen executable covers the case.
    store = Store(root / "evolving")
    engine = Engine(store, router)
    paired = []
    for enabled in (False, True):
        result = await engine.run(fixture(9200), memory=enabled, learning=False, routing_policy="fixed")
        paired.append(
            {
                "enabled": enabled,
                "correct": result["status"] == "complete",
                "preference_retained": result.get("grouped_by_warehouse"),
                "metrics": result["metrics"],
            }
        )
    evidence["memory_ablation"] = paired
    baseline = evidence["conditions"]["none"]["totals"]
    evolved = evidence["conditions"]["evolving"]["totals"]
    evidence["comparison"] = {
        "model_calls_reduced": baseline["model_calls"] - evolved["model_calls"],
        "tokens_reduced": baseline["input_tokens"]
        + baseline["output_tokens"]
        - evolved["input_tokens"]
        - evolved["output_tokens"],
        "quality_maintained": baseline["successes"] == evolved["successes"] == baseline["tasks"],
        "acquisition_included": True,
        "billed_dollar_savings_claimed": False,
        "hardware_compute_savings_claimed": False,
    }
    (root / "benchmark.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
    lines = [
        "# EvoFlow live benchmark",
        "",
        "Preliminary paired experiment; small sample. Acquisition included.",
        "",
        "| Condition | Success / tasks | Calls | Input tokens | Output tokens | Billed USD |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for mode, result in evidence["conditions"].items():
        t = result["totals"]
        lines.append(
            f"| {mode} | {t['successes']}/{t['tasks']} | {t['model_calls']} | {t['input_tokens']} | "
            f"{t['output_tokens']} | {t['billed_cost_usd']} |"
        )
    lines += [
        "",
        "No dollar savings are inferred from zero versus zero billed cost. Tokens measure inference demand,",
        "not GPU time, FLOPs or energy. All conditions use the same generation model and settings.",
        "Final candidates were frozen before the sealed test; final checks did not update skills.",
        "See benchmark.json for individual failures, latency variation, learning and local verification cost.",
    ]
    (root / "benchmark.md").write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps(evidence["comparison"]), flush=True)
