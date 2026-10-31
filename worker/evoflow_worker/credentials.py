"""Current-user Windows DPAPI storage, never a source-controlled credential file."""

from __future__ import annotations

import ctypes
import os
from ctypes import wintypes
from pathlib import Path

CREDENTIAL_SLOTS = frozenset({"openrouter", "deepseek", "mimo", "langsmith", "supabase-session"})
PROVIDER_KEY_SLOTS = {
    "DEEPSEEK_API_KEY": "deepseek",
    "MIMO_API_KEY": "mimo",
    "OPENROUTER_API_KEY": "openrouter",
    "LANGSMITH_API_KEY": "langsmith",
}
MONITORING_FIELDS = frozenset(
    {"LANGSMITH_TRACING", "LANGSMITH_PROJECT", "LANGSMITH_ENDPOINT", "LANGSMITH_WORKSPACE_ID"}
)
LANGSMITH_ENDPOINTS = frozenset({"https://api.smith.langchain.com", "https://eu.api.smith.langchain.com"})
DEFAULT_MIMO_BASE_URL = "https://token-plan-sgp.xiaomimimo.com/v1"
MIMO_BASE_URLS = frozenset({DEFAULT_MIMO_BASE_URL, "https://api.xiaomimimo.com/v1"})
MAX_PROVIDER_CONFIG_BYTES = 64 * 1024


class ProviderConfigError(ValueError):
    """A safe-to-display configuration error, never containing file values."""


def _provider_values(text: str) -> dict[str, str]:
    """Parse literal assignments only: no expansion, escapes, execution or dotenv hooks."""
    values = {}
    allowed = {*PROVIDER_KEY_SLOTS, *MONITORING_FIELDS, "MIMO_BASE_URL"}
    for number, line in enumerate(text.splitlines(), 1):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        name, separator, value = line.partition("=")
        name, value = name.strip(), value.strip()
        if not separator or name not in allowed or name in values:
            raise ProviderConfigError(f"Invalid provider configuration assignment on line {number}")
        if value.startswith(("'", '"')):
            if len(value) < 2 or value[-1] != value[0] or value[0] in value[1:-1]:
                raise ProviderConfigError(f"Invalid provider configuration quoting on line {number}")
            value = value[1:-1]
        # Keys are single-line HTTP credentials. Never import invisible controls.
        if any(ord(character) < 32 or ord(character) == 127 for character in value):
            raise ProviderConfigError(f"Invalid provider configuration characters on line {number}")
        values[name] = value.strip()
    return values


def load_provider_config(vault: Vault) -> dict[str, str]:
    """Import nonempty local keys to DPAPI; return only nonsecret configuration.

    Reads the shared per-user file, independent of checkout/delivery and vault root.
    Missing files are optional; existing files are never created, changed or deleted.
    File assignments override stored keys only when nonempty. All syntax and endpoint
    validation completes before any key is imported. No process environment is changed.
    """
    override = os.environ.get("EVOFLOW_KEYS_FILE")
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not override and not local_app_data:
        raise ProviderConfigError("Provider configuration requires LOCALAPPDATA or EVOFLOW_KEYS_FILE")
    path = Path(override) if override else Path(local_app_data) / "EvoFlow" / "provider-keys.env"
    try:
        with path.open("rb") as stream:
            raw = stream.read(MAX_PROVIDER_CONFIG_BYTES + 1)
    except FileNotFoundError:
        return {"mimo_base_url": DEFAULT_MIMO_BASE_URL}
    except (OSError, ValueError):
        raise ProviderConfigError("Unable to read local provider configuration") from None
    if len(raw) > MAX_PROVIDER_CONFIG_BYTES:
        raise ProviderConfigError("Local provider configuration exceeds size limit")
    try:
        values = _provider_values(raw.decode("utf-8-sig"))
    except UnicodeError:
        raise ProviderConfigError("Local provider configuration must be UTF-8") from None
    base_url = values.get("MIMO_BASE_URL") or DEFAULT_MIMO_BASE_URL
    if base_url not in MIMO_BASE_URLS:
        raise ProviderConfigError("MIMO_BASE_URL must be an approved HTTPS MiMo endpoint")
    monitoring = {}
    if MONITORING_FIELDS.intersection(values):
        enabled = values.get("LANGSMITH_TRACING", "").lower()
        endpoint = values.get("LANGSMITH_ENDPOINT") or "https://api.smith.langchain.com"
        if enabled not in {"", "true", "false"}:
            raise ProviderConfigError("LANGSMITH_TRACING must be true, false or empty")
        if endpoint not in LANGSMITH_ENDPOINTS:
            raise ProviderConfigError("LANGSMITH_ENDPOINT must be an approved HTTPS LangSmith endpoint")
        project = values.get("LANGSMITH_PROJECT") or "EvoFlow"
        if len(project) > 100:
            raise ProviderConfigError("LANGSMITH_PROJECT exceeds size limit")
        workspace = values.get("LANGSMITH_WORKSPACE_ID") or None
        if workspace:
            import uuid

            try:
                workspace = str(uuid.UUID(workspace))
            except ValueError:
                raise ProviderConfigError("LANGSMITH_WORKSPACE_ID must be a UUID or empty") from None
        monitoring = {
            "langsmith": {
                "enabled": enabled == "true",
                "project": project,
                "endpoint": endpoint,
                "workspace_id": workspace,
            }
        }
    for name, slot in PROVIDER_KEY_SLOTS.items():
        if values.get(name):
            try:
                vault.set(slot, values[name])
            except Exception:
                raise ProviderConfigError("Unable to protect provider credentials with DPAPI") from None
    return {"mimo_base_url": base_url, **monitoring}


class Blob(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_ubyte))]


def _crypt(data: bytes, decrypt: bool) -> bytes:
    if os.name != "nt":
        raise RuntimeError("Persistent credentials require Windows DPAPI")
    buffer = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    source, target = Blob(len(data), buffer), Blob()
    crypt = ctypes.windll.crypt32
    if decrypt:
        ok = crypt.CryptUnprotectData(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(target))
    else:
        ok = crypt.CryptProtectData(
            ctypes.byref(source), "EvoFlow", None, None, None, 1, ctypes.byref(target)
        )
    if not ok:
        raise ctypes.WinError()
    try:
        return ctypes.string_at(target.pbData, target.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(target.pbData)


class Vault:
    def __init__(self, root: Path):
        self.root = root / "credentials"
        self.root.mkdir(parents=True, exist_ok=True)

    def set(self, name: str, value: str):
        if name not in CREDENTIAL_SLOTS:
            raise ValueError("Unknown credential slot")
        (self.root / f"{name}.dpapi").write_bytes(_crypt(value.encode(), False))

    def get(self, name: str) -> str | None:
        if name not in CREDENTIAL_SLOTS:
            raise ValueError("Unknown credential slot")
        path = self.root / f"{name}.dpapi"
        if path.exists():
            return _crypt(path.read_bytes(), True).decode()
        if name == "openrouter":
            return os.environ.get("OPENROUTER_API_KEY")
        return None

    def delete(self, name: str):
        if name not in CREDENTIAL_SLOTS:
            raise ValueError("Unknown credential slot")
        (self.root / f"{name}.dpapi").unlink(missing_ok=True)
