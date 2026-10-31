import asyncio
import json
import os
from pathlib import Path

from evoflow_worker.cloud import Cloud
from evoflow_worker.credentials import Vault
from evoflow_worker.store import Store


async def main():
    root = Path(os.environ["LOCALAPPDATA"]) / "EvoFlow"
    cloud = Cloud(json.loads(Path("config/runtime.json").read_text()), Vault(root), Store(root))
    await cloud.connect_device()
    workspace = await cloud.ensure_workspace()
    result = {
        "automatic_connection": True,
        "device_scoped": True,
        "anonymous": cloud.session()["user"].get("is_anonymous"),
        "workspace": workspace,
    }
    Path("docs/evidence/automatic-cloud.json").write_text(json.dumps(result, indent=2))
    print(json.dumps(result))


asyncio.run(main())
