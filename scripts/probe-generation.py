"""Development probe of supported generation parameters; never captures hidden reasoning."""

import asyncio
import json
import os
from pathlib import Path

from evoflow_worker.credentials import Vault
from evoflow_worker.models import OpenRouter, new_metrics


async def main():
    config = json.loads(Path("config/runtime.json").read_text())
    model = OpenRouter(Vault(Path(os.environ["LOCALAPPDATA"]) / "EvoFlow"), config)
    for role in ["code_model", "general_model"]:
        metrics = new_metrics()
        metadata = await model.inspect(config[role])
        supported = set(p for e in metadata["endpoints"] for p in e["supported_parameters"])
        payload = {
            "model": config[role],
            "max_tokens": 2000,
            "temperature": 0,
            "messages": [
                {
                    "role": "user",
                    "content": 'Return only this exact JSON, no reasoning: {"title":"probe","summary":"probe","query":"SELECT order_id FROM orders"}',
                }
            ],
        }
        if "reasoning" in supported:
            payload["reasoning"] = {"enabled": False}
        result = await model.request("chat/completions", payload, metrics)
        message = result["choices"][0]["message"]
        print(
            json.dumps(
                {
                    "role": role,
                    "supported": list(supported),
                    "finish_reason": result["choices"][0].get("finish_reason"),
                    "content": message.get("content"),
                    "message_keys": list(message),
                    "metrics": metrics,
                }
            ),
            flush=True,
        )


asyncio.run(main())
