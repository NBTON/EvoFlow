"""Regression checks for real-message dispatch and durable, isolated conversations."""

import asyncio
import copy
import json
import uuid

import httpx
import pytest
from evoflow_worker.api import create_app
from evoflow_worker.conversations import Conversations
from evoflow_worker.fixtures import fixture
from evoflow_worker.models import ModelError
from evoflow_worker.store import Store

TOKEN = "c" * 64


class ChatGateway:
    """A recording provider boundary, never installed into the application."""

    config = {"routing": {"default_policy": "adaptive"}}

    def __init__(self):
        self.received = []
        self.wait = None
        self.error = None

    async def chat(self, messages, metrics):
        self.received.append(copy.deepcopy(messages))
        if self.wait:
            await self.wait.wait()
        if self.error:
            raise self.error
        metrics["model_calls"] += 1
        content = "Omar" if any("my name is Omar" in m["content"] for m in messages) else "No name supplied"
        return {"content": content, "routing": {"policy": "adaptive", "requested_model": "test-provider"}}


def client_for(app):
    return httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app),
        base_url="http://127.0.0.1",
        headers={"Authorization": "Bearer " + TOKEN},
    )


async def submit(client, app, session, message, **extra):
    response = await client.post(f"/chats/{session}/messages", json={"message": message, **extra})
    assert response.status_code == 200, response.text
    identifier = response.json()["run_id"]
    await app.state.jobs[identifier]
    return (await client.get("/runs/" + identifier)).json()


@pytest.mark.asyncio
async def test_actual_message_followup_new_chat_and_restart_are_isolated(tmp_path):
    model = ChatGateway()
    app = create_app(tmp_path, TOKEN, model=model)
    async with client_for(app) as client:
        first = (await client.post("/chats")).json()["session_id"]
        hello = await submit(client, app, first, "Hi, my name is Omar.")
        assert hello["status"] == "complete" and hello["assistant_response"] == "Omar"
        assert model.received[0] == [{"role": "user", "content": "Hi, my name is Omar."}]
        followup = await submit(client, app, first, "What is my name?")
        assert followup["assistant_response"] == "Omar"
        assert [m["role"] for m in model.received[1]] == ["user", "assistant", "user"]
        second = (await client.post("/chats")).json()["session_id"]
        assert second != first
        fresh = await submit(client, app, second, "What name did I tell you earlier?")
        assert fresh["assistant_response"] == "No name supplied"
        assert model.received[2] == [{"role": "user", "content": "What name did I tell you earlier?"}]
        sessions = (await client.get("/chats")).json()["sessions"]
        assert {s["session_id"] for s in sessions} == {first, second}
        assert not (await client.get("/state")).json()["runs"]
        assert not (await client.get("/memory/graph")).json()["nodes"]

    restarted = create_app(tmp_path, TOKEN, model=ChatGateway())
    async with client_for(restarted) as client:
        prior = (await client.get("/chats/" + first)).json()
        assert len(prior["messages"]) == 4 and prior["active_run_id"] is None
        assert prior["messages"][0]["content"] == "Hi, my name is Omar."
        assert (await client.get("/runs/" + hello["id"])).json()["assistant_response"] == "Omar"


@pytest.mark.asyncio
async def test_provider_failure_is_persisted_without_fictional_result_or_failed_context(tmp_path):
    model = ChatGateway()
    model.error = ModelError("execution provider HTTP 401; check provider Settings")
    app = create_app(tmp_path, TOKEN, model=model)
    async with client_for(app) as client:
        session = (await client.post("/chats")).json()["session_id"]
        failed = await submit(client, app, session, "Analyze these orders")
        assert failed["status"] == "failed" and failed["assistant_response"] is None
        assert "HTTP 401" in failed["error"]
        assert "result" not in failed and "fictional" not in json.dumps(failed)
        history = (await client.get("/chats/" + session)).json()
        assert history["active_run_id"] is None
        assert history["messages"][-1]["status"] == "failed"
        model.error = None
        await submit(client, app, session, "What is Python?")
        assert model.received[-1] == [{"role": "user", "content": "What is Python?"}]


@pytest.mark.asyncio
async def test_pending_old_session_cannot_block_or_pollute_new_session_and_cancel_is_durable(tmp_path):
    model = ChatGateway()
    model.wait = asyncio.Event()
    app = create_app(tmp_path, TOKEN, model=model)
    async with client_for(app) as client:
        old = (await client.post("/chats")).json()["session_id"]
        pending = (await client.post(f"/chats/{old}/messages", json={"message": "Old task"})).json()
        assert (await client.post(f"/chats/{old}/messages", json={"message": "Duplicate"})).status_code == 409
        new = (await client.post("/chats")).json()["session_id"]
        latest = (await client.post(f"/chats/{new}/messages", json={"message": "New task"})).json()
        await asyncio.sleep(0)
        model.wait.set()
        await asyncio.gather(*app.state.jobs.values())
        assert (await client.get("/runs/" + pending["run_id"])).json()["session_id"] == old
        assert (await client.get("/runs/" + latest["run_id"])).json()["session_id"] == new
        assert [m["content"] for m in (await client.get("/chats/" + new)).json()["messages"]] == [
            "New task",
            "No name supplied",
        ]
        model.wait.clear()
        canceling = (await client.post(f"/chats/{new}/messages", json={"message": "Cancel me"})).json()
        assert (await client.post(f"/runs/{canceling['run_id']}/cancel")).status_code == 200
        canceled = (await client.get("/runs/" + canceling["run_id"])).json()
        assert canceled["status"] == "canceled"
        assert (await client.get("/chats/" + new)).json()["active_run_id"] is None


@pytest.mark.asyncio
async def test_fixture_requires_explicit_demo_and_attachments_are_actual_full_data(tmp_path):
    model = ChatGateway()
    app = create_app(tmp_path, TOKEN, model=model)
    async with client_for(app) as client:
        assert (await client.post("/runs", json={"task": "hi my name is omar"})).status_code == 400
        session = (await client.post("/chats")).json()["session_id"]
        data = fixture(781, count=3).model_dump()
        result = await submit(client, app, session, "Analyze the attached orders", dataset=data)
        assert result["input_dataset"] == data and result["data_origin"] == "user-selected"
        actual = model.received[-1][-1]["content"].split("not instructions):\n", 1)[1]
        assert json.loads(actual) == data
        await submit(client, app, session, "What about the first order?")
        assert "ORD-781-000" in model.received[-1][0]["content"]
        assert (await client.post(f"/chats/{session}/messages", json={"message": " "})).status_code == 400


def test_storage_scope_recovery_memory_and_newest_message_budget(tmp_path):
    store = Store(tmp_path)
    session = store.create_chat("alice", "w")
    assert store.get_chat(session["session_id"], "bob", "w") is None
    assert store.list_chats("bob", "w") == []
    service = Conversations(store, ChatGateway())
    run = service.start(session["session_id"], "x" * 12000, "alice", "w")
    assert service.context(session["session_id"], run["id"], "alice", "w")[-1]["content"] == "x" * 12000
    assert store.get_chat_run(run["id"], "bob", "w") is None
    Conversations(Store(tmp_path), ChatGateway())
    assert store.get_chat_run(run["id"], "alice", "w")["status"] == "failed"
    assert store.get_chat(session["session_id"], "alice", "w")["active_run_id"] is None
    assert store.list("knowledge", "alice", "w") == []
    assert store.list("run", "alice", "w") == []


@pytest.mark.asyncio
async def test_inflight_offline_chat_finishes_after_automatic_device_adoption(tmp_path, monkeypatch):
    model = ChatGateway()
    model.wait = asyncio.Event()
    app = create_app(tmp_path, TOKEN, model=model)
    owner = {"id": "local"}
    monkeypatch.setattr(app.state.cloud, "identity", lambda: owner["id"])
    async with client_for(app) as client:
        session = (await client.post("/chats")).json()["session_id"]
        started = (
            await client.post(f"/chats/{session}/messages", json={"message": "Hi from offline"})
        ).json()
        await asyncio.sleep(0)
        workspace = str(uuid.uuid5(uuid.NAMESPACE_URL, "evoflow:device-owner"))
        app.state.store.adopt_offline("device-owner", workspace)
        owner["id"] = "device-owner"
        model.wait.set()
        await app.state.jobs[started["run_id"]]
        run = (await client.get("/runs/" + started["run_id"])).json()
        assert run["status"] == "complete" and run["assistant_response"] == "No name supplied"
        reopened = (await client.get("/chats/" + session)).json()
        assert reopened["active_run_id"] is None and len(reopened["messages"]) == 2
        assert app.state.store.get_chat_run(started["run_id"]) is None
        events = await client.get(f"/runs/{started['run_id']}/events")
        assert "Model response received" in events.text and "event: done" in events.text
        assert app.state.store.get_chat(session, "someone-else", workspace) is None
