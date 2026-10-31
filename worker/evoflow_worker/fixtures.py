"""Fictional data generation. No learned procedure or expected answer is stored here."""

from __future__ import annotations

import random

from .domain import Dataset, Policy


def fixture(seed: int, *, split=False, schema=1, grace=0, count=12) -> Dataset:
    rng = random.Random(seed)
    orders, shipments, inventory = [], [], []
    for index in range(count):
        oid = f"ORD-{seed}-{index:03d}"
        sku, warehouse = f"SKU-{index:03d}", ["Riyadh", "Jeddah", "Dammam"][index % 3]
        quantity = rng.randint(3, 18)
        orders.append(
            {
                "order_id": oid,
                "sku": sku,
                "warehouse": warehouse,
                "quantity" if schema == 1 else "requested_units": quantity,
                "promised_date": ["2026-10-24", "2026-10-29", "2026-11-02"][index % 3],
            }
        )
        shipped = quantity if index % 4 == 0 else rng.randint(0, quantity - 1)
        parts = [shipped] if not split or shipped < 2 else [shipped // 2, shipped - shipped // 2]
        for part, units in enumerate(parts):
            if units:
                shipments.append(
                    {
                        "shipment_id": f"S-{oid}-{part}",
                        "order_id": oid,
                        "quantity" if schema == 1 else "units": units,
                        "status": "shipped",
                        "shipped_date": "2026-10-28",
                    }
                )
        # Negative evidence: canceled shipments must not count.
        if index == 3:
            shipments.append(
                {
                    "shipment_id": f"C-{oid}",
                    "order_id": oid,
                    "quantity" if schema == 1 else "units": 50,
                    "status": "canceled",
                    "shipped_date": "2026-10-27",
                }
            )
        if index % 5 != 0:
            inventory.append({"sku": sku, "warehouse": warehouse, "available_qty": rng.randint(0, 20)})
    if orders:
        orders.append(dict(orders[0]))
    if shipments:
        shipments.append(dict(shipments[0]))
    return Dataset(
        schema_version=schema,
        orders=orders,
        shipments=shipments,
        inventory=inventory,
        policy=Policy(version=1 + int(bool(grace)), grace_days=grace),
    )


def development_cases(schema: int, policy: Policy, split: bool) -> list[Dataset]:
    cases = [fixture(101, schema=schema), fixture(102, schema=schema, count=6)]
    if split:
        cases += [fixture(103, schema=schema, split=True), fixture(104, schema=schema, split=True, count=7)]
    for case in cases:
        case.policy = policy.model_copy()
    return cases
