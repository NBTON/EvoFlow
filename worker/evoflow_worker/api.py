from __future__ import annotations

import asyncio
import hmac
import json
import uuid
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, ConfigDict, Field
from starlette.responses import JSONResponse

from .cloud import Cloud
from .conversations import MAX_MESSAGE_CHARS, Conversations
from .credentials import Vault, load_provider_config
from .domain import Dataset
from .engine import Engine
from .fixtures import fixture
from .local_tools import CONTRACTS, NativeTools
from .providers import MultiProvider
from .store import ChatBusyError, Store

ROOT = Path(__file__).resolve().parents[2]


class RunRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    task: str = Field(default="Reconcile orders, inventory and shipments.", max_length=1500)
    dataset: Dataset | None = None
    demo_stage: int = Field(default=1, ge=1, le=4)
    demo: bool = False
    session_id: str | None = Field(default=None, max_length=100)


class ChatMessageRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    message: str = Field(min_length=1, max_length=MAX_MESSAGE_CHARS)
    dataset: Dataset | None = None


def create_app(root: Path, token: str, *, model=None):
    config = json.loads((ROOT / "config" / "runtime.json").read_text())
    store, vault = Store(root), Vault(root)
    if model is None:
        config.update(load_provider_config(vault))
    router = model or MultiProvider(vault, config)
    engine, cloud = Engine(store, router), Cloud(config, vault, store)
    native_tools = NativeTools()
    conversations = Conversations(store, router, native_tools)

    @asynccontextmanager
    async def lifespan(application):
        try:
            await cloud.connect_device()
            await cloud.ensure_workspace()
            application.state.sync_status = "connected"
        except Exception:
            application.state.sync_status = "offline; local changes queued"

        async def flush_saved_changes():
            try:
                await cloud.sync()
                application.state.sync_status = "connected"
            except Exception:
                application.state.sync_status = "offline; local changes queued"

        sync_task = asyncio.create_task(flush_saved_changes())
        try:
            yield
        finally:
            if not sync_task.done():
                sync_task.cancel()
            await asyncio.gather(sync_task, return_exceptions=True)
            await stop_jobs()
            monitoring = getattr(router, "monitoring", None)
            if monitoring:
                await asyncio.to_thread(monitoring.flush)

    app = FastAPI(
        title="EvoFlow local worker", docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan
    )
    app.state.engine, app.state.store, app.state.cloud = engine, store, cloud
    app.state.conversations = conversations
    app.state.native_tools = native_tools
    app.state.jobs = {}
    app.state.paused = False
    app.state.shutdown_callback = None
    app.state.sync_status = "connecting"

    def context():
        owner = cloud.identity()
        return owner, (
            "demo" if owner == "local" else str(uuid.uuid5(uuid.NAMESPACE_URL, "evoflow:" + owner))
        )

    @app.middleware("http")
    async def authentication(request: Request, call_next):
        # Native client only: reject browser-origin requests and DNS rebinding.
        host = request.headers.get("host", "").split(":")[0]
        if (
            request.client.host not in {"127.0.0.1", "::1", "testclient"}
            or host not in {"127.0.0.1", "localhost", "testserver"}
            or "origin" in request.headers
        ):
            return JSONResponse({"detail": "Native loopback clients only"}, status_code=403)
        if not hmac.compare_digest(request.headers.get("authorization", ""), "Bearer " + token):
            return JSONResponse({"detail": "Unauthorized"}, status_code=401)
        length = request.headers.get("content-length", "0")
        if not length.isdigit():
            return JSONResponse({"detail": "Invalid Content-Length"}, status_code=400)
        if int(length) > 8_000_000:
            return JSONResponse({"detail": "Request budget exceeded"}, status_code=413)
        return await call_next(request)

    @app.get("/health")
    async def health():
        scope, workspace = context()
        return {
            "ready": True,
            "version": "0.1.0",
            "live_key_configured": bool(vault.get("openrouter")),
            "identity": scope,
            "workspace": workspace,
            "paused": app.state.paused,
            "supabase": app.state.sync_status,
            "native_tools": bool(native_tools.schemas()),
            "monitoring": router.monitoring.status() if hasattr(router, "monitoring") else {"enabled": False},
        }

    @app.post("/tools/register")
    async def register_tools(request: Request):
        body = await request.json()
        if (
            set(body) != {"workspace"}
            or not isinstance(body["workspace"], str)
            or len(body["workspace"]) > 1000
        ):
            raise HTTPException(400, "Invalid native tool registration")
        native_tools.workspace = body["workspace"]
        return {"registered": list(CONTRACTS), "version": 1}

    @app.get("/tools/pending")
    async def pending_tools():
        return {"calls": native_tools.pending()}

    @app.post("/tools/connectors")
    async def register_connectors(request: Request):
        body = await request.json()
        if set(body) != {"tools"}:
            raise HTTPException(400, "Invalid connector registry")
        try:
            native_tools.register_connectors(body["tools"])
        except (ValueError, TypeError, KeyError):
            raise HTTPException(400, "Invalid connector registry") from None
        return {"registered": len(native_tools.connectors)}

    @app.post("/tools/{call_id}/result")
    async def tool_result(call_id: str, request: Request):
        body = await request.json()
        if set(body) != {"deliveryToken", "result"}:
            raise HTTPException(400, "Invalid native result envelope")
        native_tools.finish(call_id, body["deliveryToken"], body["result"])
        return {"accepted": True}

    @app.get("/state")
    async def state():
        scope, workspace = context()
        return {
            "runs": store.list("run", scope, workspace),
            "skills": store.list("skill", scope, workspace),
            "knowledge": store.list("knowledge", scope, workspace),
            "preferences": store.list("preference", scope, workspace),
            "health": await health(),
        }

    @app.get("/memory/graph")
    async def memory_graph(
        query: str = Query(default="", max_length=200),
        types: str | None = Query(default=None, max_length=100),
        include_history: bool = False,
        limit: int = Query(default=160, ge=1, le=160),
    ):
        from .memory_graph import build_memory_graph

        scope, workspace = context()
        requested_types = [value.strip() for value in types.split(",") if value.strip()] if types else None
        return build_memory_graph(
            store,
            scope,
            workspace,
            include_history=include_history,
            types=requested_types,
            query=query,
            limit=limit,
        )

    @app.post("/chats")
    async def new_chat():
        return store.create_chat(*context())

    @app.get("/chats")
    async def chats(limit: int = Query(default=100, ge=1, le=1000)):
        return {"sessions": store.list_chats(*context(), limit=limit)}

    @app.get("/chats/{session_id}")
    async def chat(session_id: str):
        value = store.get_chat(session_id, *context())
        if value is None:
            raise HTTPException(404, "Conversation not found in this workspace")
        return value

    @app.post("/chats/{session_id}/messages")
    async def chat_message(session_id: str, body: ChatMessageRequest):
        if app.state.paused:
            raise HTTPException(409, "EvoFlow is paused")
        if not body.message.strip():
            raise HTTPException(400, "Enter a message")
        scope, workspace = context()
        if store.get_chat(session_id, scope, workspace) is None:
            raise HTTPException(404, "Conversation not found in this workspace")
        try:
            record = conversations.start(
                session_id,
                body.message,
                scope,
                workspace,
                body.dataset.model_dump() if body.dataset is not None else None,
            )
        except ChatBusyError as exc:
            raise HTTPException(409, str(exc)) from None
        app.state.jobs[record["id"]] = asyncio.create_task(conversations.execute(record, scope, workspace))
        return {"run_id": record["id"], "session_id": session_id}

    def saved_run(run_id):
        scope, workspace = context()
        return store.get_chat_run(run_id, scope, workspace) or store.get("run", run_id, scope, workspace)

    async def stop_jobs():
        for identifier, job in list(app.state.jobs.items()):
            if job.done():
                continue
            if saved_run(identifier) is not None:
                await cancel(identifier)
            else:
                engine.cancel(identifier)
                job.cancel()
                await asyncio.gather(job, return_exceptions=True)

    @app.post("/runs")
    async def run(body: RunRequest):
        if app.state.paused:
            raise HTTPException(409, "EvoFlow is paused")
        if body.dataset is None and not body.demo and "demo_stage" not in body.model_fields_set:
            raise HTTPException(400, "Provide an order dataset or explicitly select a fictional demo")
        if any(
            not task.done() and store.get("run", identifier, *context()) is not None
            for identifier, task in app.state.jobs.items()
        ):
            raise HTTPException(409, "A run is already active; cancel it or wait")
        scope, workspace = context()
        if body.session_id and store.get_chat(body.session_id, scope, workspace) is None:
            raise HTTPException(404, "Conversation not found in this workspace")
        data = body.dataset or fixture(
            20 + body.demo_stage,
            split=body.demo_stage >= 3,
            schema=2 if body.demo_stage >= 3 else 1,
            grace=2 if body.demo_stage == 4 else 0,
        )
        # Demo policies are explicitly fictional authoritative fixtures, never inferred from an answer.
        if body.dataset is None:
            store.remember(
                kind="knowledge",
                key="shipping-policy",
                source=f"fictional-policy:v{data.policy.version}",
                content=f"Fictional policy v{data.policy.version}: shipped and delivered quantities count; "
                f"overdue after {data.policy.grace_days} grace days. Inventory is availability evidence.",
                author="Fictional Operations Team",
                authoritative=True,
                scope=scope,
                workspace=workspace,
            )
        identifier = str(uuid.uuid4())

        async def execute_and_sync():
            result = await engine.run(
                data,
                body.task,
                scope=scope,
                workspace=workspace,
                run_id=identifier,
                session_id=body.session_id,
            )
            result["data_origin"] = "fictional-demo" if body.dataset is None else "user-selected"
            result["session_id"] = body.session_id
            store.put("run", result, scope, workspace)
            try:
                await cloud.sync()
                result["sync_status"] = "synchronized"
                app.state.sync_status = "connected"
            except Exception:
                result["sync_status"] = "queued; cloud unavailable"
                app.state.sync_status = "offline; local changes queued"
            store.put("run", result, scope, workspace, queue_cloud=False)
            return result

        task = asyncio.create_task(execute_and_sync())
        app.state.jobs[identifier] = task
        return {"run_id": identifier}

    @app.get("/runs/{run_id}")
    async def get_run(run_id: str):
        value = saved_run(run_id)
        if not value:
            raise HTTPException(404, "Run not found in this workspace")
        return value

    @app.post("/runs/{run_id}/cancel")
    async def cancel(run_id: str):
        value = saved_run(run_id)
        if not value:
            raise HTTPException(404, "Run not found")
        if value.get("run_kind") != "conversation":
            engine.cancel(run_id)
        job = app.state.jobs.get(run_id)
        if job and not job.done():
            job.cancel()
            await asyncio.gather(job, return_exceptions=True)
        # Cancellation can happen before a newly scheduled coroutine has entered its try/finally.
        value = saved_run(run_id)
        if value.get("run_kind") == "conversation" and value["status"] == "working":
            value.update(status="canceled", error="Response canceled. You can send another message.")
            value["metrics"]["usage_complete"] = value["metrics"]["cost_reported"] = False
            store.finish_chat_run(value, *context())
        return {"canceled": True}

    @app.get("/runs/{run_id}/events")
    async def events(run_id: str, after: int = 0):
        if not saved_run(run_id):
            raise HTTPException(404, "Run not found")

        async def stream():
            sequence = after
            while True:
                # First cloud authentication can adopt offline rows while this stream is open.
                for entry in store.events(run_id, sequence, *context()):
                    sequence = entry["sequence"]
                    yield "data: " + json.dumps(entry) + "\n\n"
                value = saved_run(run_id)
                if value and value["status"] != "working":
                    yield "event: done\ndata: {}\n\n"
                    break
                await asyncio.sleep(0.2)

        return StreamingResponse(stream(), media_type="text/event-stream")

    @app.post("/settings/key")
    async def key(request: Request):
        body = await request.json()
        key_value = body.get("key", "")
        if not key_value.startswith("sk-or-") or len(key_value) > 500:
            raise HTTPException(400, "Enter a valid OpenRouter key")
        vault.set("openrouter", key_value)
        return {"saved": True, "storage": "Windows current-user DPAPI"}

    @app.post("/pause")
    async def pause():
        app.state.paused = not app.state.paused
        return {"paused": app.state.paused}

    @app.post("/sync")
    async def sync():
        result = await cloud.sync()
        app.state.sync_status = "connected"
        return result

    @app.post("/skills/{identifier}/rollback")
    async def rollback(identifier: str):
        return store.rollback(identifier, *context())

    @app.post("/memory")
    async def memory(request: Request):
        body = await request.json()
        if set(body) - {"kind", "key", "content", "source", "explicit", "authoritative"}:
            raise HTTPException(400, "Unsupported memory fields")
        scope, workspace = context()
        return store.remember(**body, author=scope, scope=scope, workspace=workspace)

    @app.post("/knowledge/source")
    async def source_document(request: Request):
        body = await request.json()
        name, content = body.get("name", ""), body.get("content", "")
        if not isinstance(name, str) or not isinstance(content, str) or not 1 <= len(content) <= 4000:
            raise HTTPException(400, "Selected policy documents must contain 1–4000 characters")
        if "/" in name or "\\" in name or len(name) > 100:
            raise HTTPException(400, "Invalid source name")
        scope, workspace = context()
        path = await cloud.upload_selected(
            name + ".json", json.dumps({"name": name, "content": content}).encode()
        )
        entry = store.remember(
            kind="knowledge",
            key=name,
            content=content,
            source="private-storage:" + path,
            author=scope,
            authoritative=body.get("authoritative") is True,
            scope=scope,
            workspace=workspace,
        )
        await cloud.sync()
        return entry

    @app.delete("/memory/{kind}/{identifier}")
    async def forget(kind: str, identifier: str):
        return store.forget(kind, identifier, *context())

    @app.get("/export/{run_id}")
    async def export(run_id: str):
        scope, workspace = context()
        record = await get_run(run_id)
        if record.get("run_kind") == "conversation":
            return {
                "version": 1,
                "run": record,
                "events": store.events(run_id, 0, scope, workspace),
                "notice": "Tool verification is reported per action; no hardware-compute savings claimed",
            }
        return {
            "version": 1,
            "run": record,
            "events": store.events(run_id, 0, scope, workspace),
            "skills": store.list("skill", scope, workspace),
            "memory_snapshot": store.retrieve(record["task"], scope, workspace),
            "configuration": config,
            "notice": "No hardware-compute or dollar savings claimed",
        }

    @app.post("/shutdown")
    async def shutdown():
        await stop_jobs()
        if app.state.shutdown_callback:
            app.state.shutdown_callback()
        return {"stopping": True}

    @app.exception_handler(ValueError)
    async def invalid(request, exc):
        from starlette.responses import JSONResponse

        return JSONResponse({"detail": str(exc)[:300]}, status_code=400)

    @app.exception_handler(RuntimeError)
    async def unavailable(request, exc):
        from starlette.responses import JSONResponse

        return JSONResponse({"detail": str(exc)[:300]}, status_code=503)

    return app
