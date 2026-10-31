"""Deterministic tests; these test-only plans are never installed as learned skills."""

import asyncio
import sqlite3

import httpx
import pytest
from evoflow_worker.api import create_app
from evoflow_worker.credentials import Vault
from evoflow_worker.domain import Plan, oracle, verify
from evoflow_worker.engine import Engine
from evoflow_worker.fixtures import fixture
from evoflow_worker.interpreter import execute
from evoflow_worker.store import Store

# Test double only: production starts with an empty library and calls OpenRouter.
TEST_SQL = """WITH o AS (SELECT DISTINCT * FROM orders), s AS (SELECT order_id,SUM(quantity) AS shipped
 FROM (SELECT DISTINCT * FROM shipments) WHERE status IN ('shipped','delivered') GROUP BY order_id),
 r AS (SELECT o.*,COALESCE(s.shipped,0) shipped,MAX(0,o.quantity-COALESCE(s.shipped,0)) remaining,
 i.available_qty FROM o LEFT JOIN s USING(order_id) LEFT JOIN
 (SELECT DISTINCT * FROM inventory) i ON i.sku=o.sku AND i.warehouse=o.warehouse)
 SELECT order_id,warehouse,quantity ordered,shipped,remaining,
 CASE WHEN remaining>0 AND julianday(:as_of)-julianday(promised_date)>:grace_days THEN 1 ELSE 0 END delayed,
 CASE WHEN remaining=0 THEN 'complete' WHEN available_qty IS NULL THEN 'inventory_unknown'
 WHEN available_qty<remaining THEN 'stock_shortage' ELSE 'fulfillment_pending' END cause FROM r"""


class TestModel:
    __test__ = False
    config = dict(retrieval_limit=4, max_model_calls_per_run=3)
    bad = False

    async def synthesize(self, sample, task, evidence, failure, metrics):
        metrics["model_calls"] += 1
        query = TEST_SQL
        if sample["contract"]["schema_version"] == 2:
            query = query.replace("SUM(quantity)", "SUM(units)").replace("o.quantity", "o.requested_units")
            query = query.replace("quantity ordered", "requested_units ordered")
        if self.bad:
            query = query.replace("SUM(quantity)", "MAX(quantity)")
        return Plan(title="TEST DOUBLE ONLY", summary="Not a live learned artifact", query=query)


@pytest.mark.parametrize("schema,split,grace", [(1, False, 0), (1, True, 0), (2, True, 0), (2, True, 2)])
async def test_correctness_on_varied_fresh_records(tmp_path, schema, split, grace):
    data = fixture(17, schema=schema, split=split, grace=grace)
    metrics = {"model_calls": 0}
    plan = await TestModel().synthesize(data.prompt_sample(), "", [], None, metrics)
    rows, _ = execute(plan, data)
    assert verify(data, rows)["passed"]


@pytest.mark.parametrize(
    "query",
    [
        "DELETE FROM orders",
        "SELECT load_extension('evil')",
        "SELECT randomblob(100000000)",
        "SELECT * FROM sqlite_master",
        "WITH RECURSIVE x(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM x) SELECT * FROM x",
        "SELECT * FROM orders; DROP TABLE orders",
        "SELECT readfile('C:/Windows/win.ini')",
    ],
)
def test_executable_boundary_rejects_unsafe_operations(query):
    with pytest.raises((ValueError, sqlite3.DatabaseError)):
        execute(Plan(title="Test", summary="Boundary test", query=query), fixture(1))


def test_conflicting_duplicates_fail_closed():
    data = fixture(1)
    data.orders.append(dict(data.orders[0], quantity=999))
    with pytest.raises(ValueError, match="Conflicting duplicate"):
        oracle(data)


async def test_skill_promotion_persistence_reuse_and_schema_revision(tmp_path):
    store = Store(tmp_path)
    model = TestModel()
    engine = Engine(store, model)
    first = await engine.run(fixture(1))
    assert first["status"] == "complete"
    assert len(store.list("skill")) == 1
    second = await Engine(Store(tmp_path), model).run(fixture(2))
    assert second["reused"] and second["metrics"]["model_calls"] == 0
    revised = await engine.run(fixture(3, schema=2, split=True))
    assert revised["status"] == "complete" and revised["skill_version"] == 2
    assert any(e["kind"] == "invalidation" for e in store.events(revised["id"]))
    old = next(s for s in store.list("skill") if s["version"] == 1)
    store.rollback(old["id"])
    assert next(s for s in store.list("skill") if s["state"] == "active")["version"] == 1
    with sqlite3.connect(tmp_path / "checkpoints.db") as db:
        assert db.execute("SELECT count(*) FROM checkpoints").fetchone()[0] > 0


async def test_failed_development_candidate_rejected_without_quality_claim(tmp_path):
    model = TestModel()
    model.bad = True
    # First task has one counted shipment per order. Independent split-shipment
    # development cases expose the MAX-versus-SUM regression before promotion.
    store = Store(tmp_path)
    run = await Engine(store, model).run(fixture(1))
    assert run["status"] == "complete"
    assert store.list("skill")[0]["state"] == "rejected"


def test_memory_scope_duplicates_authority_versions_and_deletion(tmp_path):
    store = Store(tmp_path)
    kwargs = dict(
        kind="knowledge",
        key="rule",
        content="Count split shipment units",
        source="policy-1",
        author="ops",
        authoritative=True,
    )
    original = store.remember(**kwargs, scope="alice")
    duplicate = store.remember(**kwargs, scope="alice")
    assert duplicate["duplicate_suppressed"] and duplicate["id"] == original["id"]
    candidate = store.remember(
        **dict(kwargs, content="Unverified change", authoritative=False), scope="alice"
    )
    assert candidate["state"] == "candidate"
    assert store.retrieve("shipment", scope="bob") == []
    revision = store.remember(**dict(kwargs, content="New authoritative rule"), scope="alice")
    assert revision["supersedes"] == original["id"]
    assert [e["content"] for e in store.retrieve("rule", scope="alice")] == ["New authoritative rule"]
    store.forget("knowledge", revision["id"], scope="alice")
    assert store.retrieve("rule", scope="alice") == []


async def test_knowledge_change_invalidates_dependency(tmp_path):
    store = Store(tmp_path)
    engine = Engine(store, TestModel())
    kwargs = dict(
        kind="knowledge",
        key="rule",
        content="Original policy",
        source="official fictional rule",
        author="ops",
        authoritative=True,
    )
    store.remember(**kwargs)
    await engine.run(fixture(1))
    store.remember(**dict(kwargs, content="Revised policy"))
    run = await engine.run(fixture(2))
    assert not run["reused"] and run["metrics"]["model_calls"] == 1


async def test_auth_origin_and_unknown_run_access(tmp_path):
    app = create_app(tmp_path, "s" * 64, model=TestModel())
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app), base_url="http://127.0.0.1"
    ) as client:
        assert (await client.get("/health")).status_code == 401
        client.headers["Authorization"] = "Bearer " + "s" * 64
        assert (await client.get("/health")).status_code == 200
        assert (await client.get("/health", headers={"Origin": "https://evil.example"})).status_code == 403
        assert (await client.get("/health", headers={"Host": "evil.example"})).status_code == 403
        assert (await client.get("/runs/another-users-run")).status_code == 404


async def test_cancellation_and_failed_run_are_durable(tmp_path):
    class Slow(TestModel):
        async def synthesize(self, *args):
            await asyncio.sleep(5)

    engine = Engine(Store(tmp_path), Slow())
    task = asyncio.create_task(engine.run(fixture(1), run_id="cancel-test"))
    await asyncio.sleep(0.1)
    engine.cancel("cancel-test")
    task.cancel()
    result = await task
    assert result["status"] == "canceled"
    assert Store(tmp_path).get("run", "cancel-test")["status"] == "canceled"


def test_dpapi_round_trip_and_no_plaintext(tmp_path):
    vault = Vault(tmp_path)
    vault.set("openrouter", "sk-or-test-not-a-real-key")
    assert vault.get("openrouter") == "sk-or-test-not-a-real-key"
    assert b"sk-or-test" not in (tmp_path / "credentials" / "openrouter.dpapi").read_bytes()
