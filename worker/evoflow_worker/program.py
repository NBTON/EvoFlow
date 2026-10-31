"""Trusted interpreter for model-authored relational plans. No eval or generated code.

Operations are generic data transforms. None knows what an order, delay, or
inventory shortage means. Business semantics live in the synthesized artifact.
"""

import ast
import json
import time
from datetime import date

from .domain import OUTPUT_FIELDS, Dataset


def expression(text, row, parameters):
    tree = ast.parse(text, mode="eval")
    if len(list(ast.walk(tree))) > 120:
        raise ValueError("Expression complexity budget exceeded")

    def read(node):
        if isinstance(node, ast.Constant) and isinstance(node.value, (str, int, float, bool, type(None))):
            return node.value
        if isinstance(node, ast.Name) and not node.id.startswith("_"):
            if node.id in row:
                return row[node.id]
            if node.id in parameters:
                return parameters[node.id]
            raise ValueError(f"Unknown expression column: {node.id}")
        if isinstance(node, ast.IfExp):
            return read(node.body) if read(node.test) else read(node.orelse)
        if isinstance(node, ast.BoolOp):
            if isinstance(node.op, ast.And):
                return all(read(v) for v in node.values)
            if isinstance(node.op, ast.Or):
                return any(read(v) for v in node.values)
        if isinstance(node, ast.BinOp):
            left, right = read(node.left), read(node.right)
            if not isinstance(left, (int, float)) or not isinstance(right, (int, float)):
                raise ValueError("Arithmetic requires numbers")
            if isinstance(node.op, ast.Add):
                return left + right
            if isinstance(node.op, ast.Sub):
                return left - right
        if isinstance(node, ast.Compare) and len(node.ops) == 1:
            left, right = read(node.left), read(node.comparators[0])
            op = node.ops[0]
            if isinstance(op, ast.Eq):
                return left == right
            if isinstance(op, ast.NotEq):
                return left != right
            if isinstance(op, ast.Is):
                return left is right
            if isinstance(op, ast.IsNot):
                return left is not right
            if isinstance(op, ast.Gt):
                return left > right
            if isinstance(op, ast.GtE):
                return left >= right
            if isinstance(op, ast.Lt):
                return left < right
            if isinstance(op, ast.LtE):
                return left <= right
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and not node.keywords:
            args = [read(a) for a in node.args]
            if node.func.id == "max" and 1 <= len(args) <= 4:
                return max(args)
            if node.func.id == "min" and 1 <= len(args) <= 4:
                return min(args)
            if node.func.id == "coalesce" and 1 <= len(args) <= 4:
                return next((a for a in args if a is not None), None)
            if node.func.id == "days_between" and len(args) == 2:
                return (date.fromisoformat(args[0]) - date.fromisoformat(args[1])).days
        raise ValueError(f"Expression operation forbidden: {type(node).__name__}")

    return read(tree.body)


def interpret(steps: list[dict], data: Dataset, cancel=None):
    start = time.perf_counter()
    tables = {k: [dict(row) for row in getattr(data, k)] for k in ("orders", "shipments", "inventory")}
    parameters = {"as_of": data.policy.as_of, "grace_days": data.policy.grace_days}
    last = None
    work = 0

    def budget(amount=1):
        nonlocal work
        work += amount
        if work > 500000 or time.perf_counter() - start > 3 or (cancel and cancel.is_set()):
            raise ValueError("Declarative execution budget exceeded or canceled")

    for step in steps:
        budget()
        op = step.get("op")
        name = step.get("as")
        if not isinstance(name, str) or not name.isidentifier() or name in tables:
            raise ValueError("Each operation requires a new named output table")
        source = step.get("input")
        rows = tables.get(source)
        if rows is None:
            raise ValueError(f"Unknown input table: {source}")
        budget(len(rows))
        if op == "deduplicate":
            seen = set()
            output = []
            for row in rows:
                identity = json.dumps(row, sort_keys=True)
                if identity not in seen:
                    seen.add(identity)
                    output.append(dict(row))
        elif op == "filter_in":
            field = step["field"]
            values = step["values"]
            output = [dict(r) for r in rows if r[field] in values]
        elif op == "group_sum":
            keys = step["keys"]
            field = step["field"]
            column = step["output"]
            groups = {}
            for row in rows:
                identity = tuple(row[k] for k in keys)
                groups.setdefault(identity, {**{k: row[k] for k in keys}, column: 0})[column] += row[field]
            output = list(groups.values())
        elif op == "left_join":
            right = tables.get(step["right"])
            keys = step["on"]
            fields = step["fields"]
            index = {}
            if right is None:
                raise ValueError("Unknown right table")
            if not isinstance(keys, dict) or not isinstance(fields, dict):
                raise ValueError("Join on/fields must be objects")
            for row in right:
                index.setdefault(tuple(row[k] for k in keys.values()), []).append(row)
            output = []
            for row in rows:
                matches = index.get(tuple(row[k] for k in keys), [{}])
                budget(len(matches))
                for match in matches:
                    if set(fields) & set(row):
                        raise ValueError("Joined fields must not overwrite existing columns")
                    output.append({**row, **{dest: match.get(src) for dest, src in fields.items()}})
        elif op == "derive":
            values = step["values"]
            output = []
            for source_row in rows:
                row = dict(source_row)
                for column, text in values.items():
                    if not isinstance(text, str) or len(text) > 1000:
                        raise ValueError("Invalid expression")
                    budget()
                    row[column] = expression(text, row, parameters)
                output.append(row)
        elif op == "project":
            output = [{k: row[k] for k in step["columns"]} for row in rows]
        else:
            raise ValueError(f"Unknown relational operation: {op}")
        if len(output) > 20000:
            raise ValueError("Intermediate row budget exceeded")
        tables[name] = output
        last = name
    result = tables[last] if last else []
    if len(result) > 10000 or any(set(row) != OUTPUT_FIELDS for row in result):
        raise ValueError("Program must end with the exact seven-column output contract")
    if len(json.dumps(result)) > 2_000_000:
        raise ValueError("Output size budget exceeded")
    return result, (time.perf_counter() - start) * 1000
