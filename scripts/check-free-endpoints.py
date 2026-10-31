import asyncio
import json
import os
from pathlib import Path

import httpx
from evoflow_worker.credentials import Vault


async def main():
    key = Vault(Path(os.environ["LOCALAPPDATA"]) / "EvoFlow").get("openrouter")
    async with httpx.AsyncClient(timeout=20) as client:
        for model in [
            "google/gemma-4-31b-it:free",
            "google/gemma-4-26b-a4b-it:free",
            "nvidia/nemotron-3-ultra-550b-a55b:free",
            "thinkingmachines/inkling-small:free",
        ]:
            r = await client.post(
                "https://openrouter.ai/api/v1/chat/completions",
                headers={"Authorization": "Bearer " + key},
                json={
                    "model": model,
                    "max_tokens": 100,
                    "reasoning": {"enabled": False},
                    "messages": [{"role": "user", "content": "Reply exactly READY"}],
                },
            )
            j = r.json()
            error = j.get("error", {})
            msg = j.get("choices", [{}])[0].get("message", {}).get("content")
            print(
                json.dumps(
                    {
                        "model": model,
                        "status": r.status_code,
                        "error": str(error).replace(key, "[redacted]")[:400],
                        "content": msg,
                    }
                ),
                flush=True,
            )


asyncio.run(main())
