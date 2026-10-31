"""Small development-only calibration for the optional Span behavior diagnostic."""

import asyncio
import json
import os
from pathlib import Path

from evoflow_worker.credentials import Vault
from evoflow_worker.models import OpenRouter, new_metrics


async def main():
    config = json.loads(Path("config/runtime.json").read_text())
    router = OpenRouter(Vault(Path(os.environ["LOCALAPPDATA"]) / "EvoFlow"), config)
    cases = {
        "repeat_missing_field": {
            "expected": True,
            "trace": "Step 1 failed because sku was missing. Step 2 ran the identical program with the identical input. Step 3 repeated that program again. No new evidence was obtained.",
        },
        "verified_revision": {
            "expected": False,
            "trace": "The first query failed because sku was missing. The program was revised to preserve sku and then passed the independent development checks.",
        },
        "successful_reuse": {
            "expected": False,
            "trace": "The input schema and policy version matched the saved skill. The workflow executed once, independent checks passed and the result was saved.",
        },
        "unchanged_wrong_total": {
            "expected": True,
            "trace": "Verification found duplicate shipment counting. The agent executed the identical joins twice more without deduplicating, changing the plan, or obtaining new evidence.",
        },
    }
    questions = {
        key: {
            "type": "noul",
            "instructions": f"Considering only example {key}, does this execution repeat a failed assumption without new evidence?",
            "criteria": {
                "true": "A failed step repeats unchanged without new evidence.",
                "false": "The task succeeds or the plan changes in response to evidence.",
            },
        }
        for key in cases
    }
    metrics = new_metrics()
    # Respan accepts a string or its input/output message structure, not an arbitrary state object.
    # JSON text keeps the independent example labels visible without using the rejected object shape.
    response = await router.request(
        "decisions",
        {
            "model": config["decision_model"],
            "state": json.dumps({k: v["trace"] for k, v in cases.items()}),
            "questions": questions,
        },
        metrics,
    )
    threshold = 0.8
    scores = {key: float(response["answers"][key]["noul"]) for key in cases}
    checks = {key: (score >= threshold) == cases[key]["expected"] for key, score in scores.items()}
    result = {
        "model": config["decision_model"],
        "interface": "/api/alpha/decisions",
        "supported_question_types_tested": ["noul"],
        "threshold": threshold,
        "scores": scores,
        "development_checks": checks,
        "passed": all(checks.values()),
        "sample_count": len(cases),
        "preliminary": True,
        "automatic_scoring_enabled": False,
        "state_format": "JSON-serialized string; Respan rejects arbitrary JSON state objects",
        "threshold_note": "Predeclared development threshold on four examples; not a general calibration claim",
        "authority": "Advisory only; never overrides deterministic checks or budgets",
        "metrics": metrics,
    }
    Path("docs/evidence/decision-calibration.json").write_text(json.dumps(result, indent=2))
    print(json.dumps({"passed": result["passed"], "scores": scores}), flush=True)


asyncio.run(main())
