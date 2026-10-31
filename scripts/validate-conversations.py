"""Opt-in live chat checks through the API, without cloud startup or real history edits."""

import argparse
import asyncio
import json
import os
from datetime import UTC, datetime
from pathlib import Path

import httpx
from evoflow_worker.api import create_app
from evoflow_worker.credentials import Vault, load_provider_config
from evoflow_worker.providers import MultiProvider


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true", required=True)
    args = parser.parse_args()
    assert args.live
    project = Path(__file__).resolve().parents[1]
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    home = project / "artifacts/local" / f"conversation-check-{stamp}"
    config = json.loads((project / "config/runtime.json").read_text())
    vault = Vault(Path(os.environ["LOCALAPPDATA"]) / "EvoFlow")
    config.update(load_provider_config(vault))
    gateway = MultiProvider(vault, config)
    token = os.urandom(32).hex()
    app = create_app(home, token, model=gateway)
    report = {
        "timestamp_utc": datetime.now(UTC).isoformat(),
        "scope": "Actual model requests through isolated local API/SQLite; no cloud sync or Memory retrieval",
        "checks": [],
    }

    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app),
        base_url="http://127.0.0.1",
        headers={"Authorization": "Bearer " + token},
    ) as client:

        async def session():
            response = await client.post("/chats")
            response.raise_for_status()
            return response.json()["session_id"]

        async def send(name, session_id, message):
            print(f"{name}: submitting actual message", flush=True)
            response = await client.post(f"/chats/{session_id}/messages", json={"message": message})
            response.raise_for_status()
            run_id = response.json()["run_id"]
            for _ in range(1800):
                value = (await client.get(f"/runs/{run_id}")).json()
                if value["status"] != "working":
                    break
                await asyncio.sleep(0.1)
            else:
                await client.post(f"/runs/{run_id}/cancel")
                raise RuntimeError("Live conversation deadline reached")
            report["checks"].append({"test": name, "session_id": session_id, "result": value})
            print(
                f"{name}: {value['status']} — {value.get('assistant_response') or value.get('error')}",
                flush=True,
            )
            return value

        old = await session()
        a = await send("A_greeting", old, "Hi, my name is Omar.")
        b = await send("B_follow_up", old, "What is my name?")
        new = await session()
        c = await send("C_new_chat", new, "What name did I tell you earlier?")
        d_simple = await send("D_simple", new, "What is Python? Please answer in two sentences.")
        d_complex = await send(
            "D_complex",
            new,
            "Solve this programming problem and prove the algorithm correct. Given a directed acyclic "
            "graph with weighted edges (possibly negative), find a longest path from s to t in O(V+E). "
            "Give Python code, handle unreachable t, and explain why Dijkstra's algorithm is unsuitable.",
        )
        missing_data = await send("orders_without_data", new, "Analyze these orders.")

        class EmptyVault:
            def get(self, name):
                return None

        app.state.conversations.model = MultiProvider(EmptyVault(), config)
        error = await send("E_missing_credentials", new, "Please say hello.")
        preserved = (await client.get(f"/chats/{old}")).json()
        report["assertions"] = {
            "A_relevant": a["status"] == "complete" and "omar" in a["assistant_response"].lower(),
            "B_context": b["status"] == "complete" and "omar" in b["assistant_response"].lower(),
            "C_isolated": c["status"] == "complete" and "omar" not in c["assistant_response"].lower(),
            "D_complete": d_simple["status"] == d_complex["status"] == "complete",
            "E_explicit_failure": error["status"] == "failed" and error["assistant_response"] is None,
            "previous_chat_preserved": len(preserved["messages"]) == 4,
            "all_no_workflow_tools": all(
                check["result"]["metrics"]["tool_calls"] == 0 for check in report["checks"]
            ),
            "no_order_demo": all(
                "orders checked" not in (value.get("assistant_response") or "").lower()
                for value in (a, b, c, d_simple, d_complex, missing_data)
            ),
        }
        routes = [v["routing"]["decisions"] for v in (d_simple, d_complex)]
        report["routing_comparison"] = routes
        # Provider receipts do not confirm effective effort. Record submitted settings honestly.
        report["passed"] = all(report["assertions"].values())
    serialized = json.dumps(report, indent=2)
    if any(vault.get(slot) and vault.get(slot) in serialized for slot in ("openrouter", "deepseek", "mimo")):
        raise RuntimeError("Credential detected in evidence; nothing written")
    output = project / "docs/evidence" / f"conversation-live-{stamp}.json"
    output.write_text(serialized, encoding="utf-8")
    print(
        json.dumps({"passed": report["passed"], "evidence": str(output), "assertions": report["assertions"]}),
        flush=True,
    )
    if not report["passed"]:
        raise SystemExit(1)


if __name__ == "__main__":
    asyncio.run(main())
