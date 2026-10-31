"""Export only runs linked to local MCP acceptance audits; never provider credentials."""

import argparse
import hashlib
import json
import shutil
import sqlite3
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--home", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--include-native-acceptance", action="store_true")
    parser.add_argument("--background-run")
    parser.add_argument("--witness-window", type=int)
    parser.add_argument("--app-pid", type=int)
    parser.add_argument("--independent-screenshot", type=Path)
    args = parser.parse_args()
    audit_file = args.home / "mcp-audit.jsonl"
    audit = [json.loads(line) for line in audit_file.read_text(encoding="utf-8-sig").splitlines() if line]
    identifiers = {row["runId"] for row in audit if row.get("runId")}
    runs = []
    with sqlite3.connect((args.home / "records.db").absolute().as_uri() + "?mode=ro", uri=True) as db:
        if args.include_native_acceptance:
            identifiers.update(
                row[0]
                for row in db.execute(
                    "SELECT id FROM chat_runs WHERE json_extract(data, '$.task') LIKE ?",
                    (
                        "Use the native filesystem tool to create a new text file named EvoFlow-native-MCP-check-%",
                    ),
                )
            )
        for identifier in sorted(identifiers):
            row = db.execute("SELECT data FROM chat_runs WHERE id=?", (identifier,)).fetchone()
            if row:
                source = json.loads(row[0])
                runs.append(
                    {
                        key: source.get(key)
                        for key in (
                            "id",
                            "task",
                            "status",
                            "error",
                            "assistant_response",
                            "metrics",
                            "routing",
                            "tool_evidence",
                            "started_at",
                            "finished_at",
                        )
                    }
                )
    runs.sort(key=lambda row: row.get("started_at") or 0)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "live-runs.json").write_text(json.dumps(runs, indent=2), encoding="utf-8")
    (args.output / "native-audit.json").write_text(json.dumps(audit, indent=2), encoding="utf-8")
    totals = {
        key: sum((run.get("metrics") or {}).get(key, 0) for run in runs)
        for key in ("model_calls", "tool_calls", "input_tokens", "output_tokens", "local_execution_ms")
    }
    totals.update(
        runs=len(runs),
        failures_included=True,
        cost_complete=all((run.get("metrics") or {}).get("cost_reported", False) for run in runs),
        interpretation="Acceptance-test workload, not a benchmark. Local execution time includes native review/IPC/discovery. Startup background work and protocol-message counts are not independently metered. No GPU or dollar savings claim.",
    )
    (args.output / "accounting.json").write_text(json.dumps(totals, indent=2), encoding="utf-8")
    if args.background_run:
        if not args.witness_window or not args.app_pid or args.background_run not in identifiers:
            parser.error("Background proof requires a selected run, witness HWND and app PID")
        folder = (
            args.home / "connector-evidence" / hashlib.sha256(args.background_run.encode()).hexdigest()[:20]
        )
        outcomes = [
            row
            for row in audit
            if row.get("runId") == args.background_run and row.get("kind") == "dispatch_outcome"
        ]
        denied = [row for row in outcomes if row.get("status") == "denied"]
        order = [row["callId"] for row in outcomes if row.get("status") != "denied"]
        results = [
            json.loads((folder / f"{call_id}.json").read_text(encoding="utf-8-sig")) for call_id in order
        ]
        violations = 0
        for result in results:
            delivery = result["data"]["deliveryEvidence"]
            violations += int(
                delivery["foregroundBefore"] != args.witness_window
                or delivery["foregroundAfter"] != args.witness_window
                or delivery["pointerBeforeX"] != delivery["pointerAfterX"]
                or delivery["pointerBeforeY"] != delivery["pointerAfterY"]
            )
        verified = next(
            row
            for row in results
            if row["data"]["tool"] == "verify_state"
            and row["data"]["structuredContent"].get("stable") is True
            and row["data"]["structuredContent"].get("status") == "satisfied"
            and "Display is 42" in json.dumps(row["data"]["structuredContent"])
        )
        if args.independent_screenshot:
            image_path = args.independent_screenshot
            image = {"sha256": hashlib.sha256(image_path.read_bytes()).hexdigest()}
            image_source = (
                "Independent Cua QA observation of the exact Calculator window after EvoFlow verification"
            )
        else:
            image = verified["data"]["images"][0]
            image_path = Path(image["path"])
            if hashlib.sha256(image_path.read_bytes()).hexdigest().lower() != image["sha256"].lower():
                raise ValueError("Selected evidence image hash mismatch")
            image_source = "EvoFlow MCP result"
        if image_path.resolve() != (args.output / "calculator-42.png").resolve():
            shutil.copyfile(image_path, args.output / "calculator-42.png")
        (args.output / "background-execution.json").write_text(
            json.dumps(results, indent=2), encoding="utf-8"
        )
        proof = {
            "status": "PASS" if results and not violations else "FAIL",
            "runId": args.background_run,
            "appPid": args.app_pid,
            "foregroundWitness": args.witness_window,
            "calls": len(results),
            "deniedBeforeDispatch": len(denied),
            "focusOrPointerViolations": violations,
            "stableSamples": verified["data"]["structuredContent"]["samples"],
            "verifiedDisplay": "Display is 42",
            "imageSha256": image["sha256"],
            "imageSource": image_source,
            "delivery": "background",
        }
        (args.output / "background-proof.json").write_text(json.dumps(proof, indent=2), encoding="utf-8")
        (args.output / "background-denied.json").write_text(json.dumps(denied, indent=2), encoding="utf-8")
        if violations:
            raise ValueError("Background focus or pointer invariant failed")
    print(json.dumps({"runs": len(runs), "audit_records": len(audit), "output": str(args.output)}))


if __name__ == "__main__":
    main()
