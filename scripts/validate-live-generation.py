import argparse
import asyncio
import json
import os
from pathlib import Path

from evoflow_worker.credentials import Vault
from evoflow_worker.domain import verify
from evoflow_worker.fixtures import development_cases, fixture
from evoflow_worker.interpreter import execute
from evoflow_worker.models import OpenRouter, new_metrics


async def main():
    config = json.loads(Path("config/runtime.json").read_text())
    parser = argparse.ArgumentParser()
    parser.add_argument("--model")
    parser.add_argument("--no-reasoning", action="store_true")
    args = parser.parse_args()
    if args.model:
        config["general_model"] = args.model
        config["synthesis_role"] = "general_model"
    if args.no_reasoning:
        config["generation_reasoning"] = {"enabled": False}
    router = OpenRouter(Vault(Path(os.environ["LOCALAPPDATA"]) / "EvoFlow"), config)
    data = fixture(201)
    metrics = new_metrics()
    try:
        plan = await router.synthesize(
            data.prompt_sample(), "Reconcile orders, inventory and shipments.", [], None, metrics
        )
        checks = []
        for case in development_cases(1, data.policy, True):
            rows, duration = execute(plan, case)
            checks.append(verify(case, rows)["passed"])
        result = {"passed": all(checks), "checks": checks, "plan": plan.model_dump(), "metrics": metrics}
    except Exception as exc:
        result = {"passed": False, "error": str(exc), "metrics": metrics}
    Path("artifacts/local/live-generation-validation.json").write_text(json.dumps(result, indent=2))
    print(json.dumps({k: v for k, v in result.items() if k != "plan"}), flush=True)


asyncio.run(main())
