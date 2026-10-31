from __future__ import annotations

import asyncio
import json
import time
import uuid

import httpx

from .credentials import Vault
from .store import Store


class Cloud:
    def __init__(self, config: dict, vault: Vault, store: Store):
        self.config, self.vault, self.store = config, vault, store
        self.auth_lock = asyncio.Lock()
        self.sync_lock = asyncio.Lock()

    async def connect_device(self):
        """One persistent authenticated identity per device; no email/password UI."""
        async with self.auth_lock:
            if self.session():
                return self.identity()
            response = await self.request(
                "POST", "/auth/v1/signup", authenticated=False, json={"data": {"application": "EvoFlow"}}
            )
            session = response.json()
            if not session.get("access_token") or not session.get("refresh_token"):
                raise RuntimeError(
                    "Automatic Supabase device session is unavailable; local work is preserved"
                )
            self.vault.set("supabase-session", json.dumps(session))
            return self.identity()

    def session(self):
        raw = self.vault.get("supabase-session")
        return json.loads(raw) if raw else None

    def identity(self):
        session = self.session()
        return session["user"]["id"] if session else "local"

    async def request(self, method, route, *, authenticated=True, **kwargs):
        session = self.session() if authenticated else None
        if authenticated and not session:
            raise ValueError("Automatic cloud connection is offline; local work is preserved")
        if session and session.get("expires_at", 0) < time.time() + 60:
            async with httpx.AsyncClient(timeout=20) as client:
                response = await client.post(
                    self.config["supabase_url"] + "/auth/v1/token?grant_type=refresh_token",
                    headers={"apikey": self.config["supabase_publishable_key"]},
                    json={"refresh_token": session["refresh_token"]},
                )
                response.raise_for_status()
                session = response.json()
                self.vault.set("supabase-session", json.dumps(session))
        headers = {"apikey": self.config["supabase_publishable_key"]}
        if session:
            headers["Authorization"] = "Bearer " + session["access_token"]
        headers.update(kwargs.pop("headers", {}))
        async with httpx.AsyncClient(timeout=20) as client:
            response = await client.request(
                method, self.config["supabase_url"] + route, headers=headers, **kwargs
            )
        if response.is_error:
            raise RuntimeError(f"Supabase request failed ({response.status_code}); queued data retained")
        return response

    async def sign_in(self, email, password):
        response = await self.request(
            "POST",
            "/auth/v1/token?grant_type=password",
            authenticated=False,
            json={"email": email, "password": password},
        )
        session = response.json()
        self.vault.set("supabase-session", json.dumps(session))
        return {"user_id": session["user"]["id"], "email": session["user"].get("email")}

    async def sign_out(self):
        try:
            if self.session():
                await self.request("POST", "/auth/v1/logout")
        finally:
            self.vault.delete("supabase-session")

    async def ensure_workspace(self):
        if not self.session():
            await self.connect_device()
        owner = self.identity()
        if owner == "local":
            raise ValueError("Cloud connection unavailable; local work is saved")
        workspace = str(uuid.uuid5(uuid.NAMESPACE_URL, "evoflow:" + owner))
        await self.request(
            "POST",
            "/rest/v1/workspaces?on_conflict=id",
            headers={"Prefer": "resolution=merge-duplicates"},
            json={"id": workspace, "owner_id": owner, "name": "My EvoFlow workspace"},
        )
        self.store.adopt_offline(owner, workspace)
        return workspace

    async def sync(self):
        async with self.sync_lock:
            return await self._sync()

    async def _sync(self):
        workspace = await self.ensure_workspace()
        scope = self.identity()
        with self.store.connect() as db:
            entries = db.execute(
                "SELECT id,kind,data FROM outbox WHERE scope=? AND workspace=?", (scope, workspace)
            ).fetchall()
        synced = 0
        for identifier, kind, raw in entries:
            try:
                # Only compact metadata and selected results; raw fixture inputs remain local.
                value = json.loads(raw)
                if kind == "run":
                    value = {k: v for k, v in value.items() if k not in {"report_path", "report", "plan"}}
                await self.request(
                    "POST",
                    "/rest/v1/records?on_conflict=id,workspace_id,kind",
                    headers={"Prefer": "resolution=merge-duplicates"},
                    json={
                        "id": value["id"],
                        "kind": kind,
                        "owner_id": scope,
                        "workspace_id": workspace,
                        "payload": value,
                    },
                )
                with self.store.connect() as db:
                    # A concurrent local edit must survive an in-flight upload of
                    # an earlier version of the same idempotent record.
                    db.execute("DELETE FROM outbox WHERE id=? AND data=?", (identifier, raw))
                synced += 1
            except Exception as exc:
                with self.store.connect() as db:
                    db.execute("UPDATE outbox SET last_error=? WHERE id=?", (str(exc)[:200], identifier))
                raise
        return {"synced": synced, "workspace_id": workspace, "status": "up_to_date"}

    async def upload_selected(self, name: str, content: bytes):
        if len(content) > 2_000_000 or "/" in name or "\\" in name or name in {".", ".."}:
            raise ValueError("Invalid selected artifact")
        workspace = await self.ensure_workspace()
        path = f"{workspace}/{uuid.uuid4()}/{name}"
        await self.request(
            "POST",
            "/storage/v1/object/evoflow-private/" + path,
            content=content,
            headers={"Content-Type": "application/json"},
        )
        return path
