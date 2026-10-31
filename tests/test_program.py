import pytest
from evoflow_worker.domain import Plan
from evoflow_worker.fixtures import fixture
from evoflow_worker.interpreter import execute
from evoflow_worker.program import expression, interpret


@pytest.mark.parametrize(
    "code",
    ["__import__('os')", "x.__class__", "[x for x in orders]", "open('secret')", "2**1000000", "f'{x}'"],
)
def test_expressions_cannot_access_python_execution_or_files(code):
    with pytest.raises((ValueError, SyntaxError)):
        expression(code, {"x": 1}, {})


def test_boolean_short_circuit_and_date_arithmetic():
    assert expression("x is None or x < 0", {"x": None}, {}) is True
    assert expression("days_between(as_of, due)", {"due": "2026-10-28"}, {"as_of": "2026-10-30"}) == 2


def test_generic_relational_operations_dedup_sum_join_derive():
    data = fixture(1, count=2)
    # Test-only program: exercises generic transforms, never installed as a skill.
    steps = [
        {"op": "deduplicate", "input": "orders", "as": "o"},
        {"op": "deduplicate", "input": "shipments", "as": "s"},
        {"op": "filter_in", "input": "s", "as": "f", "field": "status", "values": ["shipped"]},
        {
            "op": "group_sum",
            "input": "f",
            "as": "g",
            "keys": ["order_id"],
            "field": "quantity",
            "output": "units_total",
        },
        {
            "op": "left_join",
            "input": "o",
            "as": "j",
            "right": "g",
            "on": {"order_id": "order_id"},
            "fields": {"total": "units_total"},
        },
        {
            "op": "derive",
            "input": "j",
            "as": "d",
            "values": {
                "ordered": "quantity",
                "shipped": "coalesce(total,0)",
                "remaining": "max(0,ordered-shipped)",
                "delayed": "0",
                "cause": "'test-only'",
            },
        },
        {
            "op": "project",
            "input": "d",
            "as": "result",
            "columns": ["order_id", "warehouse", "ordered", "shipped", "remaining", "delayed", "cause"],
        },
    ]
    rows, _ = execute(Plan(title="Test program only", summary="Generic runtime test", program=steps), data)
    assert len(rows) == 2
    assert rows[0]["shipped"] == rows[0]["ordered"]
    assert rows[1]["remaining"] >= 0


def test_new_tables_cannot_mutate_input_and_unknown_operations_fail():
    with pytest.raises(ValueError):
        interpret([{"op": "deduplicate", "input": "orders", "as": "orders"}], fixture(1))
    with pytest.raises(ValueError, match="Unknown relational operation"):
        interpret([{"op": "shell", "input": "orders", "as": "output"}], fixture(1))
