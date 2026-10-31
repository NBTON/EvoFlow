"""Explicit live, synthetic conversation + LangSmith readback; no cloud data sync."""

import argparse
import asyncio
import json
import os
from datetime import UTC, datetime
from pathlib import Path

from evoflow_worker.conversations import Conversations
from evoflow_worker.credentials import Vault, load_provider_config
from evoflow_worker.providers import MultiProvider
from evoflow_worker.store import Store


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true", required=True)
    parser.add_argument(
        "--resume", action="store_true", help="Read back the saved check without new inference"
    )
    parser.add_argument("--output", type=Path, default=Path("docs/evidence/langsmith-live.json"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    vault = Vault(Path(os.environ["LOCALAPPDATA"]) / "EvoFlow")
    config = json.loads((root / "config/runtime.json").read_text())
    config.update(load_provider_config(vault))
    gateway = MultiProvider(vault, config)
    monitoring = gateway.monitoring
    if monitoring.client is None:
        raise SystemExit("LangSmith is disabled or has no protected key. See docs/monitoring.md.")
    if args.resume:
        previous = json.loads(args.output.read_text())
        session = previous["session_id"]
        record = {"id": previous["run_id"]}
        result = {
            "status": "complete" if previous["conversation_complete"] else "failed",
            "assistant_response": "EVOFLOW_MONITORING_OK" if previous["synthetic_marker_correct"] else "",
            "metrics": {"model_calls": previous["model_calls"]},
        }
        started = datetime.fromisoformat(previous.get("started_at_utc", "2026-10-31T00:00:00+00:00"))
    else:
        started = datetime.now(UTC)
        home = root / "artifacts" / "local" / ("langsmith-" + started.strftime("%Y%m%dT%H%M%SZ"))
        store = Store(home)
        session = store.create_chat("local", "demo")["session_id"]
        conversations = Conversations(store, gateway)
        record = conversations.start(
            session, "Reply exactly EVOFLOW_MONITORING_OK. Do not use tools.", "local", "demo"
        )
        result = await conversations.execute(record, "local", "demo")
    await asyncio.to_thread(monitoring.flush)
    project = await monitoring.client.aread_project(project_name=monitoring.project)
    fields = ["ID", "RUN_TYPE", "METADATA", "INPUTS", "OUTPUTS", "END_TIME", "TRACE_ID"]
    # Retry only readback indexing; never repeat a paid inference to get a trace.
    roots, children, llms = [], [], []
    for _ in range(12):
        roots = [
            r
            async for r in monitoring.client.runs.query(
                project_ids=[str(project.id)],
                is_root=True,
                min_start_time=started,
                filter=f'and(eq(metadata_key, "evoflow_run_id"), eq(metadata_value, "{record["id"]}"))',
                selects=fields,
                page_size=30,
            )
        ]
        if len(roots) == 1:
            children = [
                r
                async for r in monitoring.client.runs.query(
                    project_ids=[str(project.id)],
                    trace_id=str(roots[0].id),
                    selects=fields,
                    min_start_time=started,
                    page_size=30,
                )
            ]
            llms = [r for r in children if (r.run_type or "").lower() == "llm"]
            if len(llms) == result["metrics"]["model_calls"] and all(r.end_time for r in children):
                break
        await asyncio.sleep(2)
    if len(roots) != 1:
        raise SystemExit("Live request completed, but the LangSmith root trace was not confirmed.")
    trace = roots[0]
    receipt = {
        "verified_at_utc": datetime.now(UTC).isoformat(),
        "started_at_utc": started.isoformat(),
        "project": monitoring.project,
        "live": True,
        "run_id": record["id"],
        "session_id": session,
        "trace_id": str(trace.id),
        "conversation_complete": result["status"] == "complete",
        "synthetic_marker_correct": (result.get("assistant_response") or "").strip()
        == "EVOFLOW_MONITORING_OK",
        "model_calls": result["metrics"]["model_calls"],
        "llm_spans": len(llms),
        "session_correlation_verified": all(
            (r.metadata or {}).get("session_id") == session for r in children
        ),
        "all_inputs_empty": all(not r.inputs for r in children),
        "provider_spans": [
            {
                "model": (r.metadata or {}).get("ls_model_name"),
                "provider": (r.metadata or {}).get("ls_provider"),
                "usage": (r.outputs or {}).get("usage_metadata", {}),
            }
            for r in llms
        ],
        "cost_note": "Only provider-reported costs included; missing costs remain unknown.",
    }
    serialized = json.dumps(receipt, indent=2)
    if any(
        vault.get(slot) and vault.get(slot) in serialized
        for slot in ("openrouter", "deepseek", "mimo", "langsmith")
    ):
        raise SystemExit("Credential detected; no evidence written.")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(serialized + "\n", encoding="utf-8")
    print(serialized)
    if not all(
        (
            receipt["conversation_complete"],
            receipt["synthetic_marker_correct"],
            receipt["session_correlation_verified"],
            receipt["all_inputs_empty"],
            receipt["llm_spans"] == receipt["model_calls"],
        )
    ):
        raise SystemExit("Live monitoring acceptance failed; see retained receipt.")


if __name__ == "__main__":
    asyncio.run(main())
