"""Read-only, scoped projection of recorded memory; never synthesizes knowledge.

Source and contract nodes represent fields actually present on a stored record.
Edges are emitted only for explicit, resolvable references, never text similarity.
The allowlist deliberately excludes sessions, plan bodies, input/result tables,
artifact paths, and all other stored record kinds.
"""

from __future__ import annotations

import re
from collections import Counter
from typing import TYPE_CHECKING
from urllib.parse import urlsplit

from .domain import digest

if TYPE_CHECKING:
    from .store import Store

RECORD_TYPES = ("knowledge", "preference", "skill", "run")
NODE_TYPES = (*RECORD_TYPES, "source", "contract")
HISTORICAL_STATES = {"superseded", "retired", "rejected"}


def _text(value, limit=1000):
    """Bound display text and keep device paths / obvious credentials off this view."""
    if not isinstance(value, (str, int, float, bool)):
        return ""
    text = str(value)
    text = re.sub(r"(?i)(?:(?<![a-z0-9])[a-z]:[\\/]|file://|\\\\)[^\s\"<>|]+", "[private path]", text)
    text = re.sub(r"(?i)\bbearer\s+\S+|\bsk-[a-z0-9_-]{12,}", "[redacted credential]", text)
    text = re.sub(r"\beyJ[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+", "[redacted session]", text)
    return text[:limit]


def _fictional(record):
    source = str(record.get("source", "")).lower()
    origin = str(record.get("data_origin", record.get("origin", ""))).lower()
    return (
        record.get("fictional") is True
        or record.get("demo") is True
        or source.startswith(("fictional-", "fictional:", "demo:"))
        or origin in {"fictional", "demo", "fixture", "fictional-demo"}
    )


def _source_label(reference):
    if reference.startswith("private-storage:"):
        return "Selected private source document", "private-storage"
    if re.search(r"(?i)(?:(?<![a-z0-9])[a-z]:[\\/]|file://|\\\\)", reference):
        return "Local source document (path private)", "local-document"
    if reference.startswith(("https://", "http://")):
        try:
            url = urlsplit(reference)
            # Source URLs may be signed; never render passwords, query or fragment.
            return _text(f"{url.scheme}://{url.hostname}{url.path}", 160), "web-source"
        except ValueError:
            return "Recorded web source", "web-source"
    return _text(reference, 160), "recorded-source"


def build_memory_graph(
    store: Store,
    scope="local",
    workspace="demo",
    *,
    include_history=False,
    types=None,
    query="",
    limit=160,
):
    """Return a bounded graph for a server-selected identity and workspace.

    Current knowledge, preferences, all runs, and active/candidate skills are the
    default. History adds superseded/retired/rejected records; deleted content is
    never returned. Missing and filtered references never become phantom nodes.
    """
    if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 300:
        raise ValueError("Graph limit must be between 1 and 300")
    if isinstance(types, str):
        types = [part.strip() for part in types.split(",") if part.strip()]
    selected_types = set(types or NODE_TYPES)
    if selected_types - set(NODE_TYPES):
        raise ValueError("Unknown memory graph type")
    if not isinstance(query, str) or len(query) > 200:
        raise ValueError("Graph search must contain at most 200 characters")

    # Store.list applies BOTH ownership predicates in SQL, before display/search.
    records = {
        kind: [r for r in store.list(kind, scope, workspace) if r.get("state") != "deleted"]
        for kind in RECORD_TYPES
    }
    nodes, edges = {}, {}
    unresolved = 0
    available = {}
    for kind, values in records.items():
        for record in values:
            identifier = record.get("id")
            if not isinstance(identifier, str) or not identifier:
                continue
            available[(kind, identifier)] = record
            state = str(record.get("state", record.get("status", "recorded")))
            if not include_history and state in HISTORICAL_STATES:
                continue
            node_id = f"{kind}:{identifier}"
            label = record.get("title") if kind == "skill" else record.get("key")
            label = label or (
                f"Task · {identifier[:8]}" if kind == "run" else f"{kind.title()} · {identifier[:8]}"
            )
            properties = {}
            for key in (
                "version",
                "key",
                "task_family",
                "effective_date",
                "content_hash",
                "artifact_hash",
                "explicit",
                "authoritative",
                "reused",
                "skill_version",
                "started_at",
                "promoted_at",
                "data_origin",
                "origin",
            ):
                value = record.get(key)
                if isinstance(value, (str, int, float, bool)):
                    properties[key] = _text(value) if isinstance(value, str) else value
            if kind == "skill":
                checks = record.get("independent_checks", [])
                if isinstance(checks, list):
                    properties["development_checks"] = len(checks)
                    properties["passed_development_checks"] = sum(
                        isinstance(c, dict) and c.get("passed") is True for c in checks
                    )
            if kind == "run" and isinstance(record.get("verification"), dict):
                properties["verification_passed"] = record["verification"].get("passed") is True
            nodes[node_id] = dict(
                id=node_id,
                record_id=identifier,
                type=kind,
                label=_text(label, 120),
                state=_text(state, 40),
                summary=_text(record.get("content", record.get("task", ""))),
                provenance=f"Stored {kind} record",
                fictional=_fictional(record),
                properties=properties,
            )

    available_ids = {f"{kind}:{identifier}" for kind, identifier in available}

    def edge(source, target, relationship, provenance):
        nonlocal unresolved
        if target not in nodes:
            # A hidden historical target is filtered, rather than unresolved.
            if target not in available_ids:
                unresolved += 1
            return
        if source not in nodes:
            return
        identity = digest([source, target, relationship])[:24]
        edges[identity] = dict(
            id=identity, source=source, target=target, type=relationship, provenance=provenance
        )

    def memory_target(identifier):
        matches = [
            f"{kind}:{identifier}" for kind in ("knowledge", "preference") if (kind, identifier) in available
        ]
        return matches[0] if len(matches) == 1 else f"missing:{identifier}"

    for kind, values in records.items():
        for record in values:
            node_id = f"{kind}:{record.get('id')}"
            if node_id not in nodes:
                continue
            source = record.get("source")
            if isinstance(source, str) and source:
                if source.partition(":")[0] in RECORD_TYPES:
                    edge(node_id, source, "sourced_from", "Stored source field: record reference")
                else:
                    source_id = "source:" + digest(source)[:24]
                    label, source_kind = _source_label(source)
                    nodes[source_id] = dict(
                        id=source_id,
                        record_id=None,
                        type="source",
                        label=label,
                        state="referenced",
                        summary="Source reference recorded on memory; document contents are not inferred.",
                        provenance="Stored source field",
                        fictional=_fictional({"source": source}),
                        properties={"source_kind": source_kind},
                    )
                    edge(node_id, source_id, "sourced_from", "Stored source field")
            if isinstance(record.get("supersedes"), str) and record["supersedes"]:
                edge(node_id, f"{kind}:{record['supersedes']}", "supersedes", "Stored supersedes field")
            if isinstance(record.get("parent_version"), str) and record["parent_version"]:
                edge(node_id, f"skill:{record['parent_version']}", "revises", "Stored parent_version field")
            if isinstance(record.get("source_run"), str) and record["source_run"]:
                edge(node_id, f"run:{record['source_run']}", "learned_from", "Stored source_run field")
            if isinstance(record.get("skill_id"), str) and record["skill_id"]:
                edge(node_id, f"skill:{record['skill_id']}", "used_skill", "Stored skill_id field")
            evidence_ids = record.get("evidence_ids", [])
            if isinstance(evidence_ids, list):
                for identifier in evidence_ids:
                    if isinstance(identifier, str):
                        edge(
                            node_id,
                            memory_target(identifier),
                            "retrieved_evidence",
                            "Stored evidence_ids field",
                        )
            dependencies = record.get("knowledge_dependencies", [])
            if isinstance(dependencies, list):
                for dependency in dependencies:
                    if not isinstance(dependency, dict):
                        continue
                    matches = [
                        value
                        for value in records["knowledge"]
                        if value.get("key") == dependency.get("key")
                        and value.get("content_hash") == dependency.get("content_hash")
                        and dependency.get("content_hash")
                    ]
                    # Key alone is insufficient: link only the recorded exact version hash.
                    if len(matches) == 1:
                        edge(
                            node_id,
                            f"knowledge:{matches[0]['id']}",
                            "depends_on",
                            "Stored knowledge_dependencies key + content_hash",
                        )
                    else:
                        unresolved += 1
            contracts = []
            if isinstance(record.get("contract"), dict) and record["contract"]:
                contracts.append(("contract", record["contract"]))
            for field in ("contracts", "input_contract"):
                if isinstance(record.get(field), dict):
                    for key, contract in record[field].items():
                        if isinstance(contract, dict) and contract:
                            contracts.append((f"{field}.{key}", contract))
            for field, contract in contracts:
                contract_id = "contract:" + digest(contract)
                schema = contract.get("schema_version", "unspecified")
                policy = contract.get("policy_version", "unspecified")
                nodes[contract_id] = dict(
                    id=contract_id,
                    record_id=None,
                    type="contract",
                    label=f"Schema {_text(schema, 20)} · policy {_text(policy, 20)}",
                    state="recorded",
                    summary="Exact stored input contract; matching hashes share a contract node.",
                    provenance="Stored contract object (content hash identity)",
                    fictional=False,
                    properties={
                        "contract_hash": digest(contract),
                        "schema_version": _text(schema, 20),
                        "policy_version": _text(policy, 20),
                    },
                )
                edge(node_id, contract_id, "has_contract", f"Stored {field} object")

    query_words = query.casefold().split()
    matching = [
        n
        for n in nodes.values()
        if n["type"] in selected_types
        and all(word in " ".join((n["label"], n["summary"], n["state"])).casefold() for word in query_words)
    ]
    # Records precede derived reference nodes, preserving Store's recent-first order.
    matching.sort(key=lambda n: (n["type"] in {"source", "contract"}, NODE_TYPES.index(n["type"])))
    displayed = matching[:limit]
    displayed_ids = {n["id"] for n in displayed}
    eligible_edges = [
        e for e in edges.values() if e["source"] in displayed_ids and e["target"] in displayed_ids
    ]
    edge_limit = min(1200, limit * 8)
    return dict(
        schema_version=1,
        scope={"workspace": _text(workspace, 100), "mode": "local" if scope == "local" else "authenticated"},
        nodes=displayed,
        edges=eligible_edges[:edge_limit],
        counts={
            "nodes": len(displayed),
            "edges": len(eligible_edges[:edge_limit]),
            "by_type": dict(Counter(n["type"] for n in displayed)),
            "matching_nodes": len(matching),
            "unresolved_references": unresolved,
        },
        truncated={"nodes": len(matching) > limit, "edges": len(eligible_edges) > edge_limit},
        filters={"include_history": include_history, "types": sorted(selected_types), "query": query},
        empty_message="No recorded memory matches these filters."
        if nodes
        else "No saved memory in this workspace yet. Run a task or explicitly save a preference to begin.",
    )
