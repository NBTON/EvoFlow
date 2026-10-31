from __future__ import annotations

import hashlib
import json
from datetime import date
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator


def digest(value: Any) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


class Policy(BaseModel):
    model_config = ConfigDict(extra="forbid")
    version: int = Field(default=1, ge=1, le=10000)
    as_of: str = "2026-10-30"
    grace_days: int = Field(default=0, ge=0, le=365)
    counted_statuses: list[str] = ["shipped", "delivered"]


class Dataset(BaseModel):
    model_config = ConfigDict(extra="forbid")
    schema_version: Literal[1, 2] = 1
    orders: list[dict] = Field(max_length=10000)
    shipments: list[dict] = Field(max_length=20000)
    inventory: list[dict] = Field(max_length=10000)
    policy: Policy = Field(default_factory=Policy)

    @model_validator(mode="after")
    def bounded_cells(self):
        from .interpreter import INVENTORY, TABLES

        for table, columns in {**TABLES[self.schema_version], "inventory": INVENTORY}.items():
            for row in getattr(self, table):
                if set(row) != set(columns):
                    raise ValueError(f"Unsupported {table} schema")
                for value in row.values():
                    if not isinstance(value, (str, int, float, type(None))):
                        raise ValueError("Input cells must be scalar values")
                    if isinstance(value, str) and len(value) > 500:
                        raise ValueError("Input cell size budget exceeded")
        return self

    def contract(self) -> dict:
        return {
            "schema_version": self.schema_version,
            "tables": {
                name: sorted({k for row in getattr(self, name) for k in row})
                for name in ("orders", "shipments", "inventory")
            },
            "policy_version": self.policy.version,
            "policy_hash": digest(self.policy.model_dump()),
        }

    def prompt_sample(self) -> dict:
        return {
            "contract": self.contract(),
            "policy": self.policy.model_dump(),
            "samples": {name: getattr(self, name)[:4] for name in ("orders", "shipments", "inventory")},
        }


class Plan(BaseModel):
    model_config = ConfigDict(extra="forbid")
    title: str = Field(min_length=1, max_length=120)
    query: str = Field(default="", max_length=16000)
    program: list[dict] = Field(default_factory=list, max_length=16)
    summary: str = Field(max_length=1000)


OUTPUT_FIELDS = {"order_id", "warehouse", "ordered", "shipped", "remaining", "delayed", "cause"}


def oracle(data: Dataset) -> list[dict]:
    """Independent Python oracle: not imported by the plan interpreter or sent to models.

    Exact duplicate IDs collapse; conflicting duplicate IDs are unsupported.
    Orders have one SKU each. Inventory is evidence of availability, not an
    allocation promise. Canceled shipment rows never count unless policy says so.
    """

    def unique(rows: list[dict], keys: tuple) -> list[dict]:
        found = {}
        for row in rows:
            key = tuple(row.get(k) for k in keys)
            if any(k is None for k in key):
                raise ValueError(f"Missing identity: {keys}")
            if key in found and found[key] != row:
                raise ValueError(f"Conflicting duplicate identity: {key}")
            found[key] = row
        return list(found.values())

    orders = unique(data.orders, ("order_id",))
    inventory = unique(data.inventory, ("sku", "warehouse"))
    shipments = unique(data.shipments, ("shipment_id",))
    quantity = "quantity" if data.schema_version == 1 else "requested_units"
    shipped_quantity = "quantity" if data.schema_version == 1 else "units"
    result = []
    for order in orders:
        ordered = int(order[quantity])
        if ordered < 0:
            raise ValueError("Negative order quantity")
        related = [
            s
            for s in shipments
            if s["order_id"] == order["order_id"] and s["status"] in data.policy.counted_statuses
        ]
        if any(int(s[shipped_quantity]) < 0 for s in related):
            raise ValueError("Negative shipment quantity")
        shipped = sum(int(s[shipped_quantity]) for s in related)
        remaining = max(0, ordered - shipped)
        available = next(
            (
                int(i["available_qty"])
                for i in inventory
                if i["sku"] == order["sku"] and i["warehouse"] == order["warehouse"]
            ),
            None,
        )
        delayed = (
            remaining > 0
            and (date.fromisoformat(data.policy.as_of) - date.fromisoformat(order["promised_date"])).days
            > data.policy.grace_days
        )
        cause = (
            "complete"
            if not remaining
            else (
                "inventory_unknown"
                if available is None
                else "stock_shortage"
                if available < remaining
                else "fulfillment_pending"
            )
        )
        result.append(
            dict(
                order_id=order["order_id"],
                warehouse=order["warehouse"],
                ordered=ordered,
                shipped=shipped,
                remaining=remaining,
                delayed=int(delayed),
                cause=cause,
            )
        )
    return sorted(result, key=lambda r: r["order_id"])


def verify(data: Dataset, rows: list[dict]) -> dict:
    expected = oracle(data)
    actual = sorted(rows, key=lambda r: str(r.get("order_id", "")))
    ok = expected == actual
    return {
        "passed": ok,
        "orders_checked": len(expected),
        "checks": [
            "identity deduplication",
            "shipment totals",
            "remaining quantity",
            "policy delay threshold",
            "supported inventory cause",
        ],
        "mismatches": []
        if ok
        else [
            {
                "order_id": e["order_id"],
                "expected": e,
                "actual": next((a for a in actual if a.get("order_id") == e["order_id"]), None),
            }
            for e in expected
            if e not in actual
        ][:6],
        "unexpected_rows": len(actual) - len(expected),
    }
