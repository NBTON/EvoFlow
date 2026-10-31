"""Explicit live checks; synthetic data only, no cloud sync or credential output."""

import argparse
import asyncio
import json
import os
import uuid
from datetime import UTC, datetime
from itertools import combinations
from pathlib import Path

from evoflow_worker.credentials import Vault, load_provider_config
from evoflow_worker.decision_routing import (
    DecisionResponseError,
    routing_questions,
    validate_answers,
)
from evoflow_worker.engine import Engine
from evoflow_worker.fixtures import fixture
from evoflow_worker.models import ModelError, new_metrics
from evoflow_worker.provider_reasoning import native_reasoning
from evoflow_worker.providers import MultiProvider
from evoflow_worker.store import Store


class ObservedGateway(MultiProvider):
    """Count stream events without retaining or exporting private reasoning."""

    async def _read_stream(self, response):
        counts = self.stream_observation

        class ObservedResponse:
            async def aiter_lines(self):
                async for line in response.aiter_lines():
                    if line.startswith("data:") and line[5:].strip() != "[DONE]":
                        chunk = json.loads(line[5:].strip())
                        for choice in chunk.get("choices", []):
                            delta = choice.get("delta", {})
                            counts["visible_delta_chunks"] += bool(delta.get("content"))
                            counts["reasoning_delta_chunks"] += any(
                                delta.get(key)
                                for key in ("reasoning_content", "reasoning", "reasoning_details")
                            )
                    yield line

        return await MultiProvider._read_stream(ObservedResponse())


def write_evidence(path, value, vault):
    text = json.dumps(value, indent=2)
    # Fail without writing if any protected provider value somehow entered evidence.
    if any(vault.get(slot) and vault.get(slot) in text for slot in ("deepseek", "mimo", "openrouter")):
        raise RuntimeError("Credential detected in evidence; nothing written")
    path.write_text(text, encoding="utf-8")


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=("smoke", "workflow", "luna-reasoning"))
    parser.add_argument("--live", action="store_true", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    config = json.loads((root / "config/runtime.json").read_text())
    vault = Vault(Path(os.environ["LOCALAPPDATA"]) / "EvoFlow")
    config.update(load_provider_config(vault))
    gateway = MultiProvider(vault, config)
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    output = root / "docs/evidence" / f"multi-provider-{args.stage}-{stamp}.json"
    report = {
        "timestamp_utc": datetime.now(UTC).isoformat(),
        "stage": args.stage,
        "mimo_base_url": config["mimo_base_url"],
        "scope": "synthetic development verification; no sealed final fixtures or cloud sync",
        "checks": [],
    }

    if args.stage == "luna-reasoning":
        gateway = ObservedGateway(vault, config)
        gateway.stream_observation = {"visible_delta_chunks": 0, "reasoning_delta_chunks": 0}
        items = dict(
            zip(
                "ABCDEFGHIJKL",
                [
                    (4, 7),
                    (6, 11),
                    (5, 9),
                    (9, 16),
                    (3, 5),
                    (7, 14),
                    (8, 15),
                    (2, 4),
                    (10, 18),
                    (1, 2),
                    (11, 22),
                    (12, 24),
                ],
                strict=True,
            )
        )
        options = []
        for ids in combinations(items, 6):
            weight = sum(items[key][0] for key in ids)
            value = sum(items[key][1] for key in ids)
            if weight <= 37 and not {"C", "G"} <= set(ids) and {"B", "K"} & set(ids):
                options.append({"items": list(ids), "weight": weight, "value": value})
        expected = max(options, key=lambda option: option["value"])
        prompt = (
            "Solve this bounded optimization problem. Pick exactly six different items, "
            "total weight at most 37, C and G cannot both be selected, and at least one "
            "of B/K must be selected. Maximize total value. Each item is [weight,value]: "
            + json.dumps(items)
            + '. Return only JSON {"items":[sorted item letters],"weight":integer,"value":integer}.'
        )
        native, submitted = native_reasoning("openrouter", "medium")
        route = {
            "provider_adapter": "openrouter",
            "requested_model": "openai/gpt-6-luna",
            "requested_effort": "medium",
            "submitted_effort": submitted,
            "native_reasoning": native,
            "effective_effort": "unknown",
        }
        metrics = new_metrics()
        check = {
            "candidate": "gpt_luna_reasoning",
            "passed": False,
            "metrics": metrics,
            "stream_observation": gateway.stream_observation,
            "routing": route,
        }
        try:
            result = await gateway.request(
                "chat/completions",
                {"model": "openai/gpt-6-luna", "messages": [{"role": "user", "content": prompt}]},
                metrics,
                routing=route,
            )
            check["final_answer"] = json.loads(result["choices"][0]["message"]["content"])
            check["expected_final_answer"] = expected
            check["answer_correct"] = check["final_answer"] == expected
            check["reasoning_tokens_reported"] = metrics["reasoning_tokens"] > 0
            check["passed"] = check["answer_correct"] and check["reasoning_tokens_reported"]
            if not check["answer_correct"]:
                check["error"] = "Final answer failed the independent exhaustive optimization check"
            elif not check["reasoning_tokens_reported"]:
                check["error"] = "No reasoning tokens reported for the reasoning check"
        except ModelError as exc:
            check["error"] = str(exc)
        except Exception as exc:
            check["error"] = f"Unexpected {type(exc).__name__}; raw exception omitted"
        report["checks"].append(check)
        write_evidence(output, report, vault)
        print(
            json.dumps(
                {
                    "candidate": check["candidate"],
                    "passed": check["passed"],
                    "error": check.get("error"),
                    "stream_observation": gateway.stream_observation,
                    "reasoning_tokens": metrics["reasoning_tokens"],
                }
            ),
            flush=True,
        )
    elif args.stage == "smoke":
        for key in ("mimo_flash", "mimo_pro", "joey", "gpt_luna"):
            metrics = new_metrics()
            check = {"candidate": key, "passed": False, "metrics": metrics}
            try:
                if key == "joey":
                    candidates = {
                        name: json.dumps(entry)
                        for name, entry in gateway.registry.items()
                        if entry["enabled"] and vault.get(entry["provider"])
                    }
                    questions = routing_questions(candidates)
                    result = await gateway._call(
                        "openrouter",
                        config["orchestration"]["decision_primary"],
                        "decisions",
                        {
                            "model": config["orchestration"]["decision_primary"],
                            "state": json.dumps(
                                {
                                    "task": "Synthesize a bounded relational reconciliation plan",
                                    "candidates": candidates,
                                }
                            ),
                            "questions": questions,
                        },
                        metrics,
                        category="decision",
                    )
                    check["answers"] = validate_answers(result, questions)
                    check["passed"] = True
                else:
                    entry = gateway.registry[key]
                    logical = "low"
                    native, submitted = native_reasoning(entry["provider"], logical)
                    route = {
                        "provider_adapter": entry["provider"],
                        "requested_model": entry["model"],
                        "requested_effort": logical,
                        "submitted_effort": submitted,
                        "native_reasoning": native,
                        "effective_effort": "unknown",
                    }
                    result = await gateway.request(
                        "chat/completions",
                        {
                            "model": entry["model"],
                            "messages": [
                                {"role": "user", "content": "Return exactly EVOFLOW_OK as your final answer."}
                            ],
                        },
                        metrics,
                        routing=route,
                    )
                    content = result["choices"][0]["message"]["content"].strip()
                    check.update(
                        nonempty_final_answer=bool(content),
                        expected_marker=content == "EVOFLOW_OK",
                        routing=route,
                    )
                    check["passed"] = content == "EVOFLOW_OK"
            except (ModelError, DecisionResponseError) as exc:
                check["error"] = str(exc)
            except Exception as exc:
                check["error"] = f"Unexpected {type(exc).__name__}; raw exception omitted"
            report["checks"].append(check)
            write_evidence(output, report, vault)
            print(
                json.dumps(
                    {
                        "candidate": key,
                        "passed": check["passed"],
                        "error": check.get("error"),
                        "calls": metrics["model_calls"],
                    }
                ),
                flush=True,
            )
    else:
        home = root / "artifacts/local" / f"multi-provider-{uuid.uuid4().hex}"
        home.mkdir(parents=True)
        report["isolated_home"] = str(home)
        engine = Engine(Store(home), gateway)
        first = await engine.run(fixture(701), workspace="multi-provider-validation")
        report["checks"].append(
            {
                "candidate": "acquisition",
                "passed": first["status"] == "complete" and bool(first.get("skill_id")),
                "run": first,
            }
        )
        write_evidence(output, report, vault)
        if report["checks"][0]["passed"]:
            second = await engine.run(fixture(702), workspace="multi-provider-validation")
            report["checks"].append(
                {
                    "candidate": "fresh-input-reuse",
                    "passed": second["status"] == "complete"
                    and second.get("reused")
                    and second["metrics"]["model_calls"] == 0,
                    "run": second,
                }
            )
            write_evidence(output, report, vault)
        for check in report["checks"]:
            run = check["run"]
            print(
                json.dumps(
                    {
                        "candidate": check["candidate"],
                        "passed": check["passed"],
                        "status": run["status"],
                        "error": run.get("error"),
                        "calls": run["metrics"]["model_calls"],
                    }
                ),
                flush=True,
            )
    print(f"Evidence: {output}", flush=True)
    if not all(check["passed"] for check in report["checks"]):
        raise SystemExit(1)


if __name__ == "__main__":
    asyncio.run(main())
