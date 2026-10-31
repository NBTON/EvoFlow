"""Graph evidence is actual metadata, never inferred associations or learned skills."""

import json

import pytest
from evoflow_worker.memory_graph import build_memory_graph
from evoflow_worker.store import Store


def put(store, kind, identifier, **fields):
    return store.put(kind, {"id": identifier, **fields})


def test_empty_graph_does_not_seed_demo_nodes(tmp_path):
    store = Store(tmp_path)
    graph = build_memory_graph(store)
    assert graph["nodes"] == graph["edges"] == []
    assert graph["counts"]["nodes"] == 0
    assert "No saved memory" in graph["empty_message"]
    assert store.list("skill") == []


def test_explicit_relationships_provenance_and_exact_contracts(tmp_path):
    store = Store(tmp_path)
    contract = {"schema_version": 1, "policy_version": 2, "policy_hash": "abc"}
    put(store, "knowledge", "old", key="policy", state="superseded", content_hash="oldhash")
    put(
        store,
        "knowledge",
        "policy",
        key="policy",
        state="current",
        content_hash="newhash",
        supersedes="old",
        source="fictional-policy:v2",
        content="Fictional policy",
    )
    put(store, "preference", "preference", key="format", state="current", source="run:run", explicit=True)
    put(store, "skill", "parent", state="retired", version=1)
    put(
        store,
        "skill",
        "skill",
        state="active",
        version=2,
        parent_version="parent",
        source_run="run",
        contracts={"1": contract},
        knowledge_dependencies=[{"key": "policy", "content_hash": "newhash"}],
    )
    put(
        store,
        "run",
        "run",
        status="complete",
        evidence_ids=["policy", "preference"],
        skill_id="skill",
        contract=contract,
    )
    graph = build_memory_graph(store, include_history=True)
    relationships = {(e["source"], e["target"], e["type"]) for e in graph["edges"]}
    assert ("knowledge:policy", "knowledge:old", "supersedes") in relationships
    assert ("preference:preference", "run:run", "sourced_from") in relationships
    assert ("skill:skill", "skill:parent", "revises") in relationships
    assert ("skill:skill", "run:run", "learned_from") in relationships
    assert ("skill:skill", "knowledge:policy", "depends_on") in relationships
    assert ("run:run", "knowledge:policy", "retrieved_evidence") in relationships
    assert ("run:run", "skill:skill", "used_skill") in relationships
    contracts = [n for n in graph["nodes"] if n["type"] == "contract"]
    assert len(contracts) == 1
    assert sum(e["target"] == contracts[0]["id"] for e in graph["edges"]) == 2
    assert all(e["provenance"].startswith("Stored ") for e in graph["edges"])
    assert graph["counts"]["unresolved_references"] == 0
    assert next(n for n in graph["nodes"] if n["id"] == "knowledge:policy")["fictional"]
    assert not next(n for n in graph["nodes"] if n["id"] == "run:run")["fictional"]


def test_scope_filter_precedes_reference_resolution_and_search(tmp_path):
    store = Store(tmp_path)
    store.put(
        "knowledge", {"id": "private", "key": "other-owner-secret", "state": "current"}, "other", "team"
    )
    store.put(
        "knowledge", {"id": "private", "key": "other-workspace-secret", "state": "current"}, "me", "elsewhere"
    )
    store.put("run", {"id": "mine", "status": "complete", "evidence_ids": ["private"]}, "me", "team")
    graph = build_memory_graph(store, "me", "team")
    assert [n["id"] for n in graph["nodes"]] == ["run:mine"]
    assert graph["edges"] == []
    assert "secret" not in json.dumps(graph)
    assert graph["counts"]["unresolved_references"] == 1
    assert build_memory_graph(store, "me", "team", query="secret")["nodes"] == []


def test_history_deleted_candidates_and_missing_references(tmp_path):
    store = Store(tmp_path)
    for state in ("current", "candidate", "superseded", "deleted"):
        put(
            store,
            "knowledge",
            state,
            key=state,
            state=state,
            content="Hidden deleted text" if state == "deleted" else state,
        )
    for state in ("active", "candidate", "retired", "rejected"):
        put(store, "skill", state, title=state, state=state)
    put(store, "run", "run", evidence_ids=["superseded", "deleted", "absent"], skill_id="missing")
    current = build_memory_graph(store)
    current_ids = {n["id"] for n in current["nodes"]}
    assert current_ids == {
        "knowledge:current",
        "knowledge:candidate",
        "skill:active",
        "skill:candidate",
        "run:run",
    }
    assert current["edges"] == []
    history = build_memory_graph(store, include_history=True)
    assert "knowledge:deleted" not in {n["id"] for n in history["nodes"]}
    assert "Hidden deleted text" not in json.dumps(history)
    assert len(history["nodes"]) == 8
    assert [(e["source"], e["target"]) for e in history["edges"]] == [("run:run", "knowledge:superseded")]


def test_dependency_hash_must_resolve_a_unique_actual_version(tmp_path):
    store = Store(tmp_path)
    put(store, "knowledge", "v1", state="superseded", key="policy", content_hash="v1")
    put(store, "knowledge", "v2", state="current", key="policy", content_hash="v2")
    put(
        store,
        "skill",
        "skill",
        state="active",
        knowledge_dependencies=[{"key": "policy", "content_hash": "v1"}],
    )
    assert build_memory_graph(store)["edges"] == []
    history = build_memory_graph(store, include_history=True)
    assert [e["target"] for e in history["edges"]] == ["knowledge:v1"]
    put(store, "knowledge", "collision", state="superseded", key="policy", content_hash="v1")
    assert build_memory_graph(store, include_history=True)["edges"] == []


def test_type_search_size_limits_and_no_dangling_edges(tmp_path):
    store = Store(tmp_path)
    put(store, "knowledge", "k", key="warehouse", state="current", source="recorded-policy")
    put(store, "preference", "p", key="format", state="candidate")
    put(store, "run", "r", evidence_ids=["k"])
    graph = build_memory_graph(store, types="knowledge", query="WAREHOUSE")
    assert [n["id"] for n in graph["nodes"]] == ["knowledge:k"]
    assert graph["edges"] == []
    bounded = build_memory_graph(store, limit=2)
    assert bounded["counts"]["matching_nodes"] == 4
    assert bounded["truncated"]["nodes"]
    ids = {n["id"] for n in bounded["nodes"]}
    assert all(e["source"] in ids and e["target"] in ids for e in bounded["edges"])
    with pytest.raises(ValueError):
        build_memory_graph(store, types=["session"])
    with pytest.raises(ValueError):
        build_memory_graph(store, limit=301)
    with pytest.raises(ValueError):
        build_memory_graph(store, query="x" * 201)


def test_projection_omits_secrets_paths_tables_and_plan_bodies(tmp_path):
    store = Store(tmp_path)
    put(store, "session", "auth", refresh_token="NEVER-RETURN")
    put(
        store,
        "knowledge",
        "k",
        key="policy",
        state="current",
        source="private-storage:user/workspace/policy",
        content="Policy memo at C:\\private\\sensitive.txt with Bearer very-secret-token",
        refresh_token="NEVER-RETURN",
    )
    put(
        store,
        "run",
        "run",
        report_path="C:\\private\\report.md",
        plan={"query": "SELECT secret"},
        result=[{"secret": "NEVER-RETURN"}],
    )
    raw = json.dumps(build_memory_graph(store))
    for private in (
        "NEVER-RETURN",
        "C:\\\\private",
        "sensitive.txt",
        "user/workspace",
        "very-secret-token",
        "SELECT secret",
    ):
        assert private not in raw
    assert "Selected private source document" in raw
    assert "[private path]" in raw


def test_matching_content_does_not_create_unrecorded_relationships(tmp_path):
    store = Store(tmp_path)
    put(store, "knowledge", "k", key="same", state="current", content="warehouse same")
    put(store, "preference", "p", key="same", state="current", content="warehouse same")
    put(store, "skill", "s", title="warehouse same", state="active", version=1)
    graph = build_memory_graph(store)
    assert len(graph["nodes"]) == 3
    assert graph["edges"] == []


def test_web_sources_hide_signed_url_credentials_and_origins_are_explicit(tmp_path):
    store = Store(tmp_path)
    put(
        store,
        "knowledge",
        "web",
        state="current",
        source="https://user:password@example.com/policy?token=secret#private",
    )
    put(store, "run", "demo", data_origin="fictional-demo")
    put(store, "run", "selected", data_origin="user-selected", fixture_hash="also-has-a-fixture-hash")
    graph = build_memory_graph(store)
    source = next(n for n in graph["nodes"] if n["type"] == "source")
    assert source["label"] == "https://example.com/policy"
    assert source["properties"]["source_kind"] == "web-source"
    assert "password" not in json.dumps(graph)
    assert "token=secret" not in json.dumps(graph)
    assert next(n for n in graph["nodes"] if n["id"] == "run:demo")["fictional"]
    selected = next(n for n in graph["nodes"] if n["id"] == "run:selected")
    assert not selected["fictional"]
    assert selected["properties"]["data_origin"] == "user-selected"
