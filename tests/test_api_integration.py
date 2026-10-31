"""API boundaries for graph scope, upload limits and durable automatic synchronization."""

import asyncio
import json
import uuid

import httpx
import pytest
from evoflow_worker.api import create_app
from evoflow_worker.cloud import Cloud
from evoflow_worker.credentials import Vault
from evoflow_worker.store import Store


@pytest.mark.asyncio
async def test_graph_endpoint_filters_scope_and_requires_launch_token(tmp_path):
    app = create_app(tmp_path, "t" * 64)
    app.state.store.remember(
        kind="knowledge",
        key="fictional-policy",
        content="Fictional warehouse rule",
        source="fictional-policy:v1",
        author="Operations",
        authoritative=True,
    )
    app.state.store.remember(
        kind="knowledge",
        key="private-other-user",
        content="Other user's policy",
        source="official",
        author="Other",
        authoritative=True,
        scope="other",
    )
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app), base_url="http://127.0.0.1"
    ) as client:
        assert (await client.get("/memory/graph")).status_code == 401
        client.headers["Authorization"] = "Bearer " + "t" * 64
        response = await client.get("/memory/graph?query=warehouse&types=knowledge&limit=10")
        assert response.status_code == 200
        graph = response.json()
        assert graph["nodes"] and all(n["type"] == "knowledge" for n in graph["nodes"])
        assert "Other user's policy" not in json.dumps(graph)
        assert (await client.get("/memory/graph?limit=1000")).status_code == 422
        assert (
            await client.get("/memory/graph", headers={"Origin": "https://evil.example"})
        ).status_code == 403


@pytest.mark.asyncio
async def test_invalid_and_excessive_content_lengths_return_bounded_errors(tmp_path):
    app = create_app(tmp_path, "t" * 64)
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app),
        base_url="http://127.0.0.1",
        headers={"Authorization": "Bearer " + "t" * 64},
    ) as client:
        assert (
            await client.post("/memory", content=b"{}", headers={"Content-Length": "bad"})
        ).status_code == 400
        assert (
            await client.post("/memory", content=b"{}", headers={"Content-Length": "9000000"})
        ).status_code == 413


@pytest.mark.asyncio
async def test_late_automatic_connection_syncs_adopted_offline_records(tmp_path, monkeypatch):
    store = Store(tmp_path)
    owner = str(uuid.uuid4())
    workspace = str(uuid.uuid5(uuid.NAMESPACE_URL, "evoflow:" + owner))
    entry = store.put("knowledge", {"id": str(uuid.uuid4()), "content": "offline fictional note"})
    cloud = Cloud(
        {"supabase_url": "https://example.invalid", "supabase_publishable_key": "test"},
        Vault(tmp_path),
        store,
    )
    identity = {"value": "local"}
    sent = []
    monkeypatch.setattr(cloud, "identity", lambda: identity["value"])
    monkeypatch.setattr(
        cloud, "session", lambda: None if identity["value"] == "local" else {"user": {"id": owner}}
    )

    async def connect():
        identity["value"] = owner
        return owner

    async def request(method, route, **kwargs):
        sent.append((method, route, kwargs.get("json")))
        return httpx.Response(201, json={})

    monkeypatch.setattr(cloud, "connect_device", connect)
    monkeypatch.setattr(cloud, "request", request)
    result = await cloud.sync()
    assert result["synced"] == 1
    assert store.get("knowledge", entry["id"], owner, workspace)
    assert sent[-1][2]["owner_id"] == owner
    assert store.get("knowledge", entry["id"]) is None


@pytest.mark.asyncio
async def test_generated_demo_origin_is_saved_before_cloud_sync(tmp_path, monkeypatch):
    app = create_app(tmp_path, "t" * 64)
    observed = []

    async def run(data, task, **kwargs):
        return {"id": kwargs["run_id"], "task": task, "status": "complete"}

    async def sync():
        observed.extend(app.state.store.list("run"))
        return {"synced": 1}

    monkeypatch.setattr(app.state.engine, "run", run)
    monkeypatch.setattr(app.state.cloud, "sync", sync)
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app),
        base_url="http://127.0.0.1",
        headers={"Authorization": "Bearer " + "t" * 64},
    ) as client:
        response = await client.post("/runs", json={"demo_stage": 1})
        assert response.status_code == 200
        await asyncio.gather(*app.state.jobs.values())
        result = await client.get("/runs/" + response.json()["run_id"])
        assert result.json()["data_origin"] == "fictional-demo"
        assert observed[0]["data_origin"] == "fictional-demo"
