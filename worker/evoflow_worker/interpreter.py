"""Closed read-only SQLite DSL, not an arbitrary-code sandbox.

No filesystem, network, extension loading, eval, shell, custom Python functions,
or mutable statements are exposed. Inputs are bounded in-memory copies.
"""

from __future__ import annotations

import json
import re
import sqlite3
import time
from threading import Event

import sqlglot
from sqlglot import exp
from sqlglot.errors import SqlglotError
from sqlglot.optimizer.qualify import qualify

from .domain import OUTPUT_FIELDS, Dataset, Plan

TABLES = {
    1: {
        "orders": {
            "order_id": "TEXT",
            "sku": "TEXT",
            "warehouse": "TEXT",
            "quantity": "INTEGER",
            "promised_date": "TEXT",
        },
        "shipments": {
            "shipment_id": "TEXT",
            "order_id": "TEXT",
            "quantity": "INTEGER",
            "status": "TEXT",
            "shipped_date": "TEXT",
        },
    },
    2: {
        "orders": {
            "order_id": "TEXT",
            "sku": "TEXT",
            "warehouse": "TEXT",
            "requested_units": "INTEGER",
            "promised_date": "TEXT",
        },
        "shipments": {
            "shipment_id": "TEXT",
            "order_id": "TEXT",
            "units": "INTEGER",
            "status": "TEXT",
            "shipped_date": "TEXT",
        },
    },
}
INVENTORY = {"sku": "TEXT", "warehouse": "TEXT", "available_qty": "INTEGER"}
FUNCTIONS = {
    "sum",
    "max",
    "min",
    "coalesce",
    "ifnull",
    "count",
    "date",
    "julianday",
    "abs",
    "round",
    "lower",
    "upper",
    "length",
    "nullif",
    "iif",
    "total",
    "cast",
}


def execute(plan: Plan, data: Dataset, cancel: Event | None = None) -> tuple[list[dict], float]:
    if plan.program:
        if plan.query:
            raise ValueError("Use one executable plan language per artifact")
        from .program import interpret

        return interpret(plan.program, data, cancel)
    start = time.perf_counter()
    if not re.match(r"^\s*(SELECT|WITH)\b", plan.query, re.I):
        raise ValueError("Only a SELECT or non-recursive WITH query is admitted")
    if re.search(r"\brecursive\b", plan.query, re.I):
        raise ValueError("Recursive queries are disabled")
    # Generic SQL compilation expands expression aliases; it contains no task
    # procedure or reconciliation answers. SQLite authorization remains final.
    try:
        statements = sqlglot.parse(plan.query, dialect="sqlite")
        if len(statements) != 1 or not isinstance(statements[0], exp.Select):
            raise ValueError("Exactly one SELECT query is admitted")
        compiled = qualify(
            statements[0],
            dialect="sqlite",
            schema={**TABLES[data.schema_version], "inventory": INVENTORY},
            validate_qualify_columns=True,
            quote_identifiers=False,
            identify=False,
        ).sql(dialect="sqlite")
    except SqlglotError as exc:
        raise ValueError(f"SQL compilation rejected: {str(exc)[:250]}") from exc
    connection = sqlite3.connect(":memory:")
    try:
        connection.enable_load_extension(False)
        connection.setlimit(sqlite3.SQLITE_LIMIT_LENGTH, 2_000_000)
        connection.setlimit(sqlite3.SQLITE_LIMIT_SQL_LENGTH, 16000)
        connection.setlimit(sqlite3.SQLITE_LIMIT_COLUMN, 64)
        connection.setlimit(sqlite3.SQLITE_LIMIT_EXPR_DEPTH, 40)
        connection.execute("PRAGMA temp_store=MEMORY")
        connection.execute("PRAGMA cache_size=-4096")
        for name, columns in {**TABLES[data.schema_version], "inventory": INVENTORY}.items():
            connection.execute(f'CREATE TABLE "{name}" ({", ".join(f"{k} {v}" for k, v in columns.items())})')
            rows = getattr(data, name)
            for row in rows:
                if set(row) != set(columns):
                    raise ValueError(f"Unsupported {name} schema")
                if any(not isinstance(v, (str, int, float, type(None))) for v in row.values()):
                    raise ValueError("Only scalar input cells are allowed")
                if any(isinstance(v, str) and len(v) > 500 for v in row.values()):
                    raise ValueError("Input cell exceeds 500 characters")
            connection.executemany(
                f'INSERT INTO "{name}" VALUES ({",".join("?" for _ in columns)})',
                [tuple(row[k] for k in columns) for row in rows],
            )
        connection.execute("PRAGMA query_only=ON")
        operations = 0

        def progress():
            nonlocal operations
            operations += 1000
            return int(
                operations > 4_000_000 or time.perf_counter() - start > 3 or bool(cancel and cancel.is_set())
            )

        def authorize(action, first, second, database, trigger):
            if action == sqlite3.SQLITE_SELECT:
                return sqlite3.SQLITE_OK
            if action == sqlite3.SQLITE_READ and first in {"orders", "shipments", "inventory"}:
                return sqlite3.SQLITE_OK
            if action == sqlite3.SQLITE_FUNCTION and (second or "").lower() in FUNCTIONS:
                return sqlite3.SQLITE_OK
            return sqlite3.SQLITE_DENY

        connection.set_progress_handler(progress, 1000)
        connection.set_authorizer(authorize)
        cursor = connection.execute(
            compiled, {"as_of": data.policy.as_of, "grace_days": data.policy.grace_days}
        )
        if {c[0] for c in cursor.description} != OUTPUT_FIELDS or len(cursor.description) != 7:
            raise ValueError(f"Output requires exactly {sorted(OUTPUT_FIELDS)}")
        rows = [dict(zip([c[0] for c in cursor.description], row)) for row in cursor.fetchmany(10001)]
        if len(rows) > 10000 or len(json.dumps(rows)) > 2_000_000:
            raise ValueError("Output budget exceeded")
        return rows, (time.perf_counter() - start) * 1000
    finally:
        connection.close()
