"""Verify coding-agent skill discovery without starting an AI/model turn.

Uses the supplied Codex executable's versioned app-server protocol. Emits only
the relevant skill metadata and source hashes, never auth configuration.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import queue
import subprocess
import threading
from datetime import datetime, timezone
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--codex", required=True, type=Path)
    parser.add_argument("--langchain-source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    lock = json.loads((root / "skills-lock.json").read_text(encoding="utf-8"))
    process = subprocess.Popen(
        [str(args.codex), "app-server", "--stdio"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        encoding="utf-8",
        cwd=root,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    messages: queue.Queue = queue.Queue()

    def read_messages():
        for line in process.stdout:
            try:
                messages.put(json.loads(line))
            except json.JSONDecodeError:
                continue

    threading.Thread(target=read_messages, daemon=True).start()

    def send(message):
        process.stdin.write(json.dumps(message) + "\n")
        process.stdin.flush()

    def request(identifier, method, params):
        send({"id": identifier, "method": method, "params": params})
        while True:
            message = messages.get(timeout=45)
            if message.get("id") == identifier:
                if "error" in message:
                    raise RuntimeError(message["error"])
                return message["result"]

    try:
        request(
            1,
            "initialize",
            {
                "clientInfo": {"name": "evoflow_setup_verifier", "version": "1.0"},
                "capabilities": {"experimentalApi": True},
            },
        )
        send({"method": "initialized", "params": {}})
        result = request(2, "skills/list", {"cwds": [str(root)], "forceReload": True})
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=10)

    entries = [skill for entry in result["data"] for skill in entry["skills"]]
    wanted = set(lock["skills"])
    relevant = [
        s
        for s in entries
        if s["name"] in wanted
        or s.get("pluginId", "")
        in {"winui@win-dev-skills", "winappcli@win-dev-skills", "windbg@win-dev-skills"}
    ]
    evidence = []
    for name, entry in lock["skills"].items():
        source = args.langchain_source / entry["skillPath"]
        installed = root / ".agents" / "skills" / name / "SKILL.md"

        def digest(path):
            return hashlib.sha256(path.read_bytes()).hexdigest()

        evidence.append(
            {
                "skill": name,
                "sha256": digest(installed),
                "matches_source": digest(installed) == digest(source),
            }
        )
    required = wanted | {
        "winui-setup",
        "winui-dev-workflow",
        "winui-design",
        "winui-ui-testing",
        "winui-packaging",
        "winapp-setup",
        "winapp-package",
        "windbg-diagnostic-method",
    }
    # Plugin skills use a qualified discovery name such as winui:winui-setup.
    missing = sorted(required - {s["name"].split(":", 1)[-1] for s in relevant if s["enabled"]})
    output = {
        "verified_at_utc": datetime.now(timezone.utc).isoformat(),
        "client": str(args.codex),
        "client_version": subprocess.check_output([str(args.codex), "--version"], text=True).strip(),
        "method": "fresh app-server client; skills/list forceReload=true; no model turn",
        "current_chat_reloaded": False,
        "langchain_source_commit": subprocess.check_output(
            ["git", "-C", str(args.langchain_source), "rev-parse", "HEAD"], text=True
        ).strip(),
        "skills": [{k: s.get(k) for k in ("name", "scope", "enabled", "path", "pluginId")} for s in relevant],
        "source_hash_checks": evidence,
        "missing_required_skills": missing,
        "discovery_errors": [error for entry in result["data"] for error in entry["errors"]],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(
        json.dumps(
            {
                "discovered_relevant_skills": len(relevant),
                "hash_matches": sum(x["matches_source"] for x in evidence),
                "missing": missing,
                "output": str(args.output),
            }
        )
    )
    if missing or not all(x["matches_source"] for x in evidence):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
