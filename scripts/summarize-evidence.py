"""Recompute comparison summaries from immutable per-run measurements; no inference."""

import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
file = root / "docs/evidence/benchmark.json"
data = json.loads(file.read_text())
totals = {k: v["totals"] for k, v in data["conditions"].items()}
a, c = totals["none"], totals["evolving"]
for name, condition in data["conditions"].items():
    for metric in ["model_calls", "input_tokens", "output_tokens", "billed_cost_usd"]:
        assert sum(r["metrics"][metric] for r in condition["runs"]) == condition["totals"][metric]
data["comparison"]["quality_maintained"] = (
    c["successes"] / c["tasks"] >= a["successes"] / a["tasks"]
    and c["final_successes"] / c["final_tasks"] >= a["final_successes"] / a["final_tasks"]
)
data["comparison"]["all_conditions_perfect"] = all(t["successes"] == t["tasks"] for t in totals.values())
data["comparison"]["model_call_reduction_percent"] = 100 * (1 - c["model_calls"] / a["model_calls"])
data["comparison"]["token_reduction_percent"] = 100 * (
    1 - (c["input_tokens"] + c["output_tokens"]) / (a["input_tokens"] + a["output_tokens"])
)
data["comparison"]["success_normalized_model_calls"] = {
    k: t["model_calls"] / t["successes"] for k, t in totals.items()
}
file.write_text(json.dumps(data, indent=2), encoding="utf-8")
lines = [
    "# EvoFlow: measured inference demand",
    "",
    "Preliminary live experiment on fictional operational data. All three conditions used the same free model, provider, "
    "temperature, generation budget, interpreter, verifier, approved knowledge corpus and explicit preference snapshot. "
    "Each condition had its own SQLite state and skill library.",
    "",
    "Two acquisition tasks plus six sealed final tasks per condition (two paired repetitions of three task variants). "
    "Candidates were frozen before final testing; final feedback did not change skills or trigger repairs.",
    "",
    "| Condition | All tasks correct | Sealed final correct | Model calls | Input tokens | Output tokens | Reported model cost |",
    "|---|---:|---:|---:|---:|---:|---:|",
]
for name, t in totals.items():
    cost = f"${t['billed_cost_usd']:.2f}" if t["cost_reported"] else "Not fully reported"
    lines.append(
        f"| {name} | {t['successes']}/{t['tasks']} | {t['final_successes']}/{t['final_tasks']} | "
        f"{t['model_calls']} | {t['input_tokens']:,} | {t['output_tokens']:,} | {cost} |"
    )
lines += [
    "",
    f"Evolving skills used {data['comparison']['model_call_reduction_percent']:.0f}% fewer model calls and "
    f"{data['comparison']['token_reduction_percent']:.1f}% fewer reported tokens than no reuse, including acquisition. "
    "It completed all eight tasks; the no-reuse condition failed one final output-contract check.",
    "",
    "Each covered reuse task required zero inference calls. Cold acquisition still requires model generation and "
    "independent development checks. On this workload, the first successful covered reuse starts amortizing acquisition; "
    "a changed schema requires another acquisition. This is an observed small-sample result, not a general break-even guarantee.",
    "",
    "| Condition | Final latency mean | Sample standard deviation | Local execution total (including checks) |",
    "|---|---:|---:|---:|",
]
for name, t in totals.items():
    lines.append(
        f"| {name} | {t['final_latency_mean_ms'] / 1000:.3f}s | {t['final_latency_stdev_ms'] / 1000:.3f}s | {t['local_execution_ms']:.2f}ms |"
    )
lines += [
    "",
    "The evolving library and its old-case regressions were tested before promotion. Skill hashes, fixture hashes, "
    "all events, provider request IDs, individual failures, retries, timing, and usage are in benchmark.json.",
    "",
    "Memory ablation is separate: paired executions with retrieval/reflection disabled and enabled both remained "
    "correct and used the frozen procedure. The explicit warehouse-grouping preference applied only with memory enabled. "
    "These two runs are excluded from the procedural comparison above.",
    "",
    "No dollar savings are inferred from zero versus zero free-model cost. Tokens are inference-demand measurements, "
    "not GPU, FLOP, energy, or hardware-compute measurements. Local worker timing excludes native startup and cloud "
    "synchronization. Supabase service cost was not individually metered. Development model-selection trials are "
    "separate from this frozen benchmark and include rejected/failed generations.",
    "",
]
failures = [
    f"- {name}: {r['id']} — {r.get('error')}"
    for name, v in data["conditions"].items()
    for r in v["runs"]
    if r["status"] != "complete"
]
lines += ["Recorded failures:", ""] + failures
(root / "docs/evidence/benchmark.md").write_text("\n".join(lines), encoding="utf-8")
print(json.dumps(data["comparison"]))
