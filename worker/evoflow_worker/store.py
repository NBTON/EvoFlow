from __future__ import annotations

import json
import sqlite3
import threading
import time
import uuid
from pathlib import Path

from .domain import digest


class ChatBusyError(RuntimeError):
    """Only one active request may append to a conversation at a time."""


class Store:
    def __init__(self, root: Path):
        self.root = root
        root.mkdir(parents=True, exist_ok=True)
        self.path = root / "records.db"
        self.lock = threading.RLock()
        with self.connect() as db:
            db.executescript("""
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS records(
                  kind TEXT NOT NULL,id TEXT NOT NULL,scope TEXT NOT NULL,workspace TEXT NOT NULL,
                  data TEXT NOT NULL,updated REAL NOT NULL,PRIMARY KEY(kind,id,scope,workspace));
                CREATE TABLE IF NOT EXISTS events(
                  id INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL,scope TEXT NOT NULL,
                  workspace TEXT NOT NULL,data TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS outbox(
                  id TEXT PRIMARY KEY,scope TEXT NOT NULL,workspace TEXT NOT NULL,
                  kind TEXT NOT NULL,data TEXT NOT NULL,last_error TEXT);
                CREATE TABLE IF NOT EXISTS chat_sessions(
                  id TEXT NOT NULL,scope TEXT NOT NULL,workspace TEXT NOT NULL,
                  title TEXT NOT NULL,created REAL NOT NULL,updated REAL NOT NULL,
                  active_run_id TEXT,PRIMARY KEY(id,scope,workspace));
                CREATE TABLE IF NOT EXISTS chat_messages(
                  sequence INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL,
                  session_id TEXT NOT NULL,scope TEXT NOT NULL,workspace TEXT NOT NULL,
                  run_id TEXT NOT NULL,role TEXT NOT NULL,content TEXT NOT NULL,
                  created REAL NOT NULL,status TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS chat_message_session
                  ON chat_messages(session_id,scope,workspace,sequence);
                CREATE TABLE IF NOT EXISTS chat_runs(
                  id TEXT NOT NULL,session_id TEXT NOT NULL,scope TEXT NOT NULL,
                  workspace TEXT NOT NULL,data TEXT NOT NULL,PRIMARY KEY(id,scope,workspace));
            """)

    def connect(self):
        return sqlite3.connect(self.path, timeout=15)

    def put(self, kind, value, scope="local", workspace="demo", *, queue_cloud=True):
        value = dict(value)
        value.setdefault("id", str(uuid.uuid4()))
        with self.lock, self.connect() as db:
            serialized = json.dumps(value)
            db.execute(
                "INSERT INTO records VALUES(?,?,?,?,?,?) ON CONFLICT(kind,id,scope,workspace) "
                "DO UPDATE SET data=excluded.data,updated=excluded.updated",
                (kind, value["id"], scope, workspace, serialized, time.time()),
            )
            if queue_cloud and scope != "local":
                ident = digest([kind, value["id"], scope, workspace])
                db.execute(
                    "INSERT OR REPLACE INTO outbox VALUES(?,?,?,?,?,NULL)",
                    (ident, scope, workspace, kind, serialized),
                )
        return value

    def list(self, kind, scope="local", workspace="demo"):
        with self.connect() as db:
            return [
                json.loads(row[0])
                for row in db.execute(
                    "SELECT data FROM records WHERE kind=? AND scope=? AND workspace=? ORDER BY updated DESC",
                    (kind, scope, workspace),
                )
            ]

    def get(self, kind, identifier, scope="local", workspace="demo"):
        return next((v for v in self.list(kind, scope, workspace) if v["id"] == identifier), None)

    def event(self, run_id, kind, message, scope="local", workspace="demo", **extra):
        value = dict(kind=kind, message=message, timestamp=time.time(), **extra)
        with self.lock, self.connect() as db:
            cur = db.execute(
                "INSERT INTO events(run_id,scope,workspace,data) VALUES(?,?,?,?)",
                (run_id, scope, workspace, json.dumps(value)),
            )
            value["sequence"] = cur.lastrowid
        return value

    def events(self, run_id, after=0, scope="local", workspace="demo"):
        with self.connect() as db:
            return [
                dict(json.loads(row[1]), sequence=row[0])
                for row in db.execute(
                    "SELECT id,data FROM events WHERE run_id=? AND id>? AND scope=? AND workspace=? ORDER BY id",
                    (run_id, after, scope, workspace),
                )
            ]

    def create_chat(self, scope="local", workspace="demo"):
        identifier, now = str(uuid.uuid4()), time.time()
        with self.lock, self.connect() as db:
            db.execute(
                "INSERT INTO chat_sessions VALUES(?,?,?,?,?,?,NULL)",
                (identifier, scope, workspace, "New Chat", now, now),
            )
        return self.get_chat(identifier, scope, workspace)

    def list_chats(self, scope="local", workspace="demo", *, limit=100):
        # Conversation tables deliberately never enter persistent Memory or the cloud outbox.
        with self.connect() as db:
            rows = db.execute(
                "SELECT s.id,s.title,s.created,s.updated,s.active_run_id,"
                "(SELECT COUNT(*) FROM chat_messages m WHERE m.session_id=s.id "
                "AND m.scope=s.scope AND m.workspace=s.workspace) "
                "FROM chat_sessions s WHERE s.scope=? AND s.workspace=? "
                "ORDER BY s.updated DESC LIMIT ?",
                (scope, workspace, limit),
            ).fetchall()
        return [
            dict(
                session_id=row[0],
                title=row[1],
                created_at=row[2],
                updated_at=row[3],
                active_run_id=row[4],
                message_count=row[5],
            )
            for row in rows
        ]

    def get_chat(self, identifier, scope="local", workspace="demo"):
        with self.connect() as db:
            row = db.execute(
                "SELECT title,created,updated,active_run_id FROM chat_sessions "
                "WHERE id=? AND scope=? AND workspace=?",
                (identifier, scope, workspace),
            ).fetchone()
            if row is None:
                return None
            messages = db.execute(
                "SELECT id,run_id,role,content,created,status FROM chat_messages "
                "WHERE session_id=? AND scope=? AND workspace=? ORDER BY sequence",
                (identifier, scope, workspace),
            ).fetchall()
        return dict(
            session_id=identifier,
            title=row[0],
            created_at=row[1],
            updated_at=row[2],
            active_run_id=row[3],
            messages=[
                dict(id=m[0], run_id=m[1], role=m[2], content=m[3], created_at=m[4], status=m[5])
                for m in messages
            ],
        )

    def start_chat_run(self, session_id, record, scope="local", workspace="demo"):
        now = time.time()
        with self.lock, self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            session = db.execute(
                "SELECT active_run_id,title FROM chat_sessions WHERE id=? AND scope=? AND workspace=?",
                (session_id, scope, workspace),
            ).fetchone()
            if session is None:
                raise ValueError("Conversation not found in this workspace")
            if session[0]:
                raise ChatBusyError("This chat has an active request; cancel it or wait")
            title = " ".join(record["task"].split())[:70] if session[1] == "New Chat" else session[1]
            db.execute(
                "UPDATE chat_sessions SET active_run_id=?,title=?,updated=? "
                "WHERE id=? AND scope=? AND workspace=?",
                (record["id"], title, now, session_id, scope, workspace),
            )
            db.execute(
                "INSERT INTO chat_messages(id,session_id,scope,workspace,run_id,role,content,created,status) "
                "VALUES(?,?,?,?,?,'user',?,?,'working')",
                (str(uuid.uuid4()), session_id, scope, workspace, record["id"], record["task"], now),
            )
            db.execute(
                "INSERT INTO chat_runs VALUES(?,?,?,?,?)",
                (record["id"], session_id, scope, workspace, json.dumps(record)),
            )

    def get_chat_run(self, identifier, scope="local", workspace="demo"):
        with self.connect() as db:
            row = db.execute(
                "SELECT data FROM chat_runs WHERE id=? AND scope=? AND workspace=?",
                (identifier, scope, workspace),
            ).fetchone()
        return json.loads(row[0]) if row else None

    def resolve_chat_run_scope(self, identifier, scope, workspace):
        """Internal job continuation after first device authentication adopts offline records.

        Public reads always require the current authenticated scope. Only an already
        authorized offline job may follow its unique run ID to the adopted location.
        """
        if scope != "local" or workspace != "demo":
            return scope, workspace
        with self.connect() as db:
            rows = db.execute("SELECT scope,workspace FROM chat_runs WHERE id=?", (identifier,)).fetchall()
        return tuple(rows[0]) if len(rows) == 1 else (scope, workspace)

    def finish_chat_run(self, record, scope="local", workspace="demo"):
        now = time.time()
        status = record["status"]
        if status not in {"complete", "failed", "canceled"}:
            raise ValueError("Invalid terminal conversation status")
        with self.lock, self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            previous = db.execute(
                "SELECT data FROM chat_runs WHERE id=? AND scope=? AND workspace=?",
                (record["id"], scope, workspace),
            ).fetchone()
            if previous is None or json.loads(previous[0])["status"] != "working":
                return
            db.execute(
                "UPDATE chat_runs SET data=? WHERE id=? AND scope=? AND workspace=?",
                (json.dumps(record), record["id"], scope, workspace),
            )
            db.execute(
                "UPDATE chat_messages SET status=? WHERE run_id=? AND scope=? AND workspace=?",
                (status, record["id"], scope, workspace),
            )
            content = record.get("assistant_response") if status == "complete" else record.get("error")
            db.execute(
                "INSERT INTO chat_messages(id,session_id,scope,workspace,run_id,role,content,created,status) "
                "VALUES(?,?,?,?,?,'assistant',?,?,?)",
                (
                    str(uuid.uuid4()),
                    record["session_id"],
                    scope,
                    workspace,
                    record["id"],
                    content or "",
                    now,
                    status,
                ),
            )
            db.execute(
                "UPDATE chat_sessions SET active_run_id=NULL,updated=? "
                "WHERE id=? AND scope=? AND workspace=? AND active_run_id=?",
                (now, record["session_id"], scope, workspace, record["id"]),
            )

    def recover_interrupted_chats(self):
        with self.connect() as db:
            rows = db.execute("SELECT data,scope,workspace FROM chat_runs").fetchall()
        for raw, scope, workspace in rows:
            record = json.loads(raw)
            if record["status"] == "working":
                record.update(
                    status="failed",
                    error="The app stopped before this response finished. Please retry.",
                    finished_at=time.time(),
                )
                record["metrics"]["usage_complete"] = record["metrics"]["cost_reported"] = False
                self.finish_chat_run(record, scope, workspace)
                self.event(
                    record["id"], "error", record["error"], scope, workspace, session_id=record["session_id"]
                )

    def promote(self, candidate, scope="local", workspace="demo"):
        """Atomic lifecycle switch and audit; active version is unique per family."""
        retired = []
        with self.lock, self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            for (raw,) in db.execute(
                "SELECT data FROM records WHERE kind='skill' AND scope=? AND workspace=?", (scope, workspace)
            ).fetchall():
                previous = json.loads(raw)
                if previous["state"] == "active":
                    previous["state"] = "retired"
                    retired.append(previous)
                    db.execute(
                        "UPDATE records SET data=? WHERE kind='skill' AND id=? AND scope=? AND workspace=?",
                        (json.dumps(previous), previous["id"], scope, workspace),
                    )
            candidate = dict(candidate, state="active", promoted_at=time.time())
            db.execute(
                "INSERT OR REPLACE INTO records VALUES('skill',?,?,?,?,?)",
                (candidate["id"], scope, workspace, json.dumps(candidate), time.time()),
            )
            db.execute(
                "INSERT INTO events(run_id,scope,workspace,data) VALUES(?,?,?,?)",
                (
                    candidate["source_run"],
                    scope,
                    workspace,
                    json.dumps(
                        {
                            "kind": "promotion",
                            "message": f"Promoted skill v{candidate['version']}",
                            "skill_id": candidate["id"],
                            "parent_version": candidate.get("parent_version"),
                            "timestamp": time.time(),
                        }
                    ),
                ),
            )
        for previous in retired:
            if previous["id"] != candidate["id"]:
                self.put("skill", previous, scope, workspace)
        self.put("skill", candidate, scope, workspace)
        return candidate

    def adopt_offline(self, scope, workspace):
        """Claim this device's never-authenticated offline records on first connection."""
        if scope == "local":
            return
        with self.lock, self.connect() as db:
            records = db.execute(
                "SELECT kind,data FROM records WHERE scope='local' AND workspace='demo'"
            ).fetchall()
            db.execute(
                "UPDATE events SET scope=?,workspace=? WHERE scope='local' AND workspace='demo'",
                (scope, workspace),
            )
            db.execute("DELETE FROM records WHERE scope='local' AND workspace='demo'")
            for table in ("chat_sessions", "chat_messages", "chat_runs"):
                db.execute(
                    f"UPDATE {table} SET scope=?,workspace=? WHERE scope='local' AND workspace='demo'",
                    (scope, workspace),
                )
            for kind, raw in records:
                db.execute(
                    "INSERT OR IGNORE INTO records VALUES(?,?,?,?,?,?)",
                    (kind, json.loads(raw)["id"], scope, workspace, raw, time.time()),
                )
        for kind, raw in records:
            self.put(kind, json.loads(raw), scope, workspace)

    def rollback(self, identifier, scope="local", workspace="demo"):
        candidate = self.get("skill", identifier, scope, workspace)
        if not candidate or candidate["state"] not in {"retired", "active"}:
            raise ValueError("Only a previously verified version can be restored")
        return self.promote(dict(candidate, rollback=True), scope, workspace)

    def remember(
        self,
        *,
        kind,
        key,
        content,
        source,
        author,
        explicit=False,
        authoritative=False,
        scope="local",
        workspace="demo",
        effective_date="2026-10-30",
    ):
        if kind not in {"knowledge", "preference"}:
            raise ValueError("Invalid memory type")
        if len(content) > 4000 or len(source) > 500 or not key or len(key) > 100:
            raise ValueError("Memory budget exceeded")
        entries = [e for e in self.list(kind, scope, workspace) if e["key"] == key]
        current = next((e for e in entries if e["state"] == "current"), None)
        content_hash = digest(content)
        if current and current["content_hash"] == content_hash:
            return dict(current, duplicate_suppressed=True)
        accepted = bool(
            (kind == "knowledge" and authoritative and source)
            or (kind == "preference" and explicit and author)
        )
        value = dict(
            id=str(uuid.uuid4()),
            key=key,
            content=content,
            content_hash=content_hash,
            source=source,
            author=author,
            explicit=explicit,
            authoritative=authoritative,
            version=max([e["version"] for e in entries], default=0) + 1,
            effective_date=effective_date,
            state="current" if accepted else "candidate",
            supersedes=current["id"] if current and accepted else None,
        )
        if current and accepted:
            self.put(kind, dict(current, state="superseded"), scope, workspace)
        return self.put(kind, value, scope, workspace)

    def retrieve(self, query, scope="local", workspace="demo", limit=4):
        words = set(query.lower().split())
        entries = [
            dict(e, record_type=kind)
            for kind in ("knowledge", "preference")
            for e in self.list(kind, scope, workspace)
            if e["state"] == "current" and e["effective_date"] <= "2026-10-30"
        ]
        return sorted(entries, key=lambda e: len(words & set(e["content"].lower().split())), reverse=True)[
            :limit
        ]

    def forget(self, kind, identifier, scope="local", workspace="demo"):
        entry = self.get(kind, identifier, scope, workspace)
        if not entry or kind not in {"knowledge", "preference"}:
            raise ValueError("Memory entry not found")
        return self.put(
            kind, dict(entry, state="deleted", content="", content_hash=digest("")), scope, workspace
        )
