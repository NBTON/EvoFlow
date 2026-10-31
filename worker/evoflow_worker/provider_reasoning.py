"""Provider-native reasoning controls; submitted controls are not effective-effort evidence."""

from __future__ import annotations

LOGICAL_EFFORTS = ("none", "minimal", "low", "medium", "high", "xhigh", "max")
DEEPSEEK_MAP = {
    "none": "none",
    "minimal": "low",
    "low": "low",
    "medium": "high",
    "high": "high",
    "xhigh": "high",
    "max": "max",
}
OPENROUTER_MAP = {effort: ("low" if effort == "minimal" else effort) for effort in LOGICAL_EFFORTS}


def native_reasoning(provider: str, logical: str) -> tuple[dict, str]:
    if logical not in LOGICAL_EFFORTS:
        raise ValueError("Unsupported logical reasoning effort")
    if provider == "deepseek":
        effort = DEEPSEEK_MAP[logical]
        return {
            "thinking": {"type": "disabled" if effort == "none" else "enabled"},
            "reasoning_effort": effort,
        }, effort
    if provider == "mimo":
        state = "disabled" if logical == "none" else "enabled"
        return {"thinking": {"type": state}}, state
    if provider == "openrouter":
        effort = OPENROUTER_MAP[logical]
        return {"reasoning": {"effort": effort}}, effort
    raise ValueError("Unsupported configured provider")


def output_budget(config: dict, native: str) -> int:
    budgets = config.get("output_budgets", {})
    key = "none" if native in {"none", "disabled"} else "max" if native == "max" else "thinking"
    value = budgets.get(key, {"none": 4096, "thinking": 16384, "max": 32768}[key])
    if isinstance(value, bool) or not isinstance(value, int) or not 1024 <= value <= 65536:
        raise ValueError("Output budget must be between 1024 and 65536 tokens")
    return value
