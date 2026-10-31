"""Offline provider config contracts; only temporary files and fake DPAPI are used."""

import traceback

import pytest
from evoflow_worker import credentials
from evoflow_worker.credentials import (
    DEFAULT_MIMO_BASE_URL,
    ProviderConfigError,
    Vault,
    load_provider_config,
)


@pytest.fixture(autouse=True)
def isolated_config(monkeypatch, tmp_path):
    # XOR is deliberately a test double, not production protection.
    monkeypatch.setattr(credentials, "_crypt", lambda data, decrypt: bytes(byte ^ 0x5A for byte in data))
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "local"))
    monkeypatch.setenv("EVOFLOW_KEYS_FILE", str(tmp_path / "keys.env"))
    monkeypatch.delenv("OPENROUTER_API_KEY", raising=False)


def write_config(tmp_path, text):
    path = tmp_path / "keys.env"
    path.write_text(text, encoding="utf-8")
    return path


def test_missing_file_does_not_create_configuration_or_credentials(tmp_path):
    vault = Vault(tmp_path / "state")
    assert load_provider_config(vault) == {"mimo_base_url": DEFAULT_MIMO_BASE_URL}
    assert not (tmp_path / "keys.env").exists()
    assert list(vault.root.iterdir()) == []


def test_imports_all_keys_overwrites_nonempty_and_preserves_source(tmp_path):
    vault = Vault(tmp_path / "state")
    vault.set("openrouter", "previous-key")
    vault.set("supabase-session", "session-preserved")
    config = write_config(
        tmp_path,
        "DEEPSEEK_API_KEY=deepseek-test\nMIMO_API_KEY=mimo-test\n"
        "MIMO_BASE_URL=https://api.xiaomimimo.com/v1\nOPENROUTER_API_KEY=openrouter-test\n",
    )
    original, modified = config.read_bytes(), config.stat().st_mtime_ns
    assert load_provider_config(vault) == {"mimo_base_url": "https://api.xiaomimimo.com/v1"}
    for slot in ("deepseek", "mimo", "openrouter"):
        assert vault.get(slot) == f"{slot}-test"
        assert f"{slot}-test".encode() not in (vault.root / f"{slot}.dpapi").read_bytes()
    assert vault.get("supabase-session") == "session-preserved"
    assert config.read_bytes() == original and config.stat().st_mtime_ns == modified


def test_blank_keys_do_not_erase_existing_keys(tmp_path):
    vault = Vault(tmp_path / "state")
    for slot in ("deepseek", "mimo", "openrouter"):
        vault.set(slot, f"stored-{slot}")
    write_config(
        tmp_path,
        'DEEPSEEK_API_KEY=\nMIMO_API_KEY=   \nOPENROUTER_API_KEY=""\nMIMO_BASE_URL=\n',
    )
    assert load_provider_config(vault) == {"mimo_base_url": DEFAULT_MIMO_BASE_URL}
    for slot in ("deepseek", "mimo", "openrouter"):
        assert vault.get(slot) == f"stored-{slot}"


def test_bom_comments_quotes_and_literals_never_expand(monkeypatch, tmp_path):
    monkeypatch.setenv("FAKE_SECRET", "must-not-expand")
    path = write_config(
        tmp_path,
        '\ufeff# comment\n\n DEEPSEEK_API_KEY = "${FAKE_SECRET}"\n'
        "MIMO_API_KEY='$(nothing);`literal`\\n'\nOPENROUTER_API_KEY=literal#hash=equals\n",
    )
    vault = Vault(tmp_path / "state")
    load_provider_config(vault)
    assert vault.get("deepseek") == "${FAKE_SECRET}"
    assert vault.get("mimo") == "$(nothing);`literal`\\n"
    assert vault.get("openrouter") == "literal#hash=equals"
    assert path.exists()


@pytest.mark.parametrize(
    "invalid",
    [
        "UNKNOWN_API_KEY=secret-marker",
        "supabase-session=secret-marker",
        "DEEPSEEK_API_KEY",
        "=secret-marker",
        "export DEEPSEEK_API_KEY=secret-marker",
        "DEEPSEEK_API_KEY=a\nDEEPSEEK_API_KEY=secret-marker",
        'DEEPSEEK_API_KEY="secret-marker',
        'DEEPSEEK_API_KEY="secret-marker" extra',
        "DEEPSEEK_API_KEY=secret-marker\x00hidden",
        "DEEPSEEK_API_KEY=secret-marker\tinside",
    ],
)
def test_invalid_syntax_rejected_before_any_import(tmp_path, invalid):
    vault = Vault(tmp_path / "state")
    write_config(tmp_path, "MIMO_API_KEY=must-not-import\n" + invalid)
    with pytest.raises(ProviderConfigError) as error:
        load_provider_config(vault)
    assert "secret-marker" not in str(error.value)
    assert list(vault.root.iterdir()) == []


@pytest.mark.parametrize(
    "endpoint",
    [
        "http://api.xiaomimimo.com/v1",
        "https://evil.example/v1",
        "https://api.xiaomimimo.com.evil.example/v1",
        "https://api.xiaomimimo.com@evil.example/v1",
        "https://evil.example@api.xiaomimimo.com/v1",
        "https://api.xiaomimimo.com:443/v1",
        "https://api.xiaomimimo.com/v1/",
        "https://api.xiaomimimo.com/v1?secret-marker",
        "https://api.xiaomimimo.com/v1#secret-marker",
        "https://API.XIAOMIMIMO.COM/v1",
    ],
)
def test_endpoint_must_be_exact_allowlisted_https_url(tmp_path, endpoint):
    vault = Vault(tmp_path / "state")
    write_config(tmp_path, "DEEPSEEK_API_KEY=secret-marker\nMIMO_BASE_URL=" + endpoint)
    with pytest.raises(ProviderConfigError, match="approved HTTPS MiMo endpoint") as error:
        load_provider_config(vault)
    assert endpoint not in str(error.value) and "secret-marker" not in str(error.value)
    assert list(vault.root.iterdir()) == []


@pytest.mark.parametrize("endpoint", sorted(credentials.MIMO_BASE_URLS))
def test_both_approved_endpoints_are_accepted(tmp_path, endpoint):
    write_config(tmp_path, "MIMO_BASE_URL=" + endpoint)
    assert load_provider_config(Vault(tmp_path / "state")) == {"mimo_base_url": endpoint}


def test_size_and_encoding_fail_closed(tmp_path):
    vault = Vault(tmp_path / "state")
    path = tmp_path / "keys.env"
    for raw in (b"x" * (credentials.MAX_PROVIDER_CONFIG_BYTES + 1), b"\xffsecret-marker"):
        path.write_bytes(raw)
        with pytest.raises(ProviderConfigError) as error:
            load_provider_config(vault)
        assert "secret-marker" not in str(error.value)
        assert list(vault.root.iterdir()) == []


def test_read_error_is_redacted(monkeypatch, tmp_path):
    vault = Vault(tmp_path / "state")

    def fail(*args, **kwargs):
        raise PermissionError("secret-marker")

    monkeypatch.setattr(credentials.Path, "open", fail)
    with pytest.raises(ProviderConfigError, match="Unable to read") as error:
        load_provider_config(vault)
    assert "secret-marker" not in "".join(traceback.format_exception(error.value))


def test_dpapi_error_is_redacted_and_source_retained(monkeypatch, tmp_path):
    path = write_config(tmp_path, "DEEPSEEK_API_KEY=secret-marker")
    vault = Vault(tmp_path / "state")

    def fail(data, decrypt):
        raise RuntimeError(data.decode())

    monkeypatch.setattr(credentials, "_crypt", fail)
    with pytest.raises(ProviderConfigError, match="Unable to protect") as error:
        load_provider_config(vault)
    assert "secret-marker" not in "".join(traceback.format_exception(error.value))
    assert path.read_text() == "DEEPSEEK_API_KEY=secret-marker"
    assert list(vault.root.iterdir()) == []


def test_canonical_path_is_independent_of_vault_or_delivery(monkeypatch, tmp_path):
    local = tmp_path / "local" / "EvoFlow"
    local.mkdir(parents=True)
    path = local / "provider-keys.env"
    path.write_text("MIMO_API_KEY=canonical-test", encoding="utf-8")
    monkeypatch.delenv("EVOFLOW_KEYS_FILE")
    for delivery in ("delivery-one", "delivery-two"):
        vault = Vault(tmp_path / delivery)
        assert load_provider_config(vault) == {"mimo_base_url": DEFAULT_MIMO_BASE_URL}
        assert vault.get("mimo") == "canonical-test"


def test_override_wins_and_missing_localappdata_has_safe_error(monkeypatch, tmp_path):
    monkeypatch.delenv("LOCALAPPDATA")
    write_config(tmp_path, "MIMO_API_KEY=override-test")
    vault = Vault(tmp_path / "state")
    load_provider_config(vault)
    assert vault.get("mimo") == "override-test"
    monkeypatch.delenv("EVOFLOW_KEYS_FILE")
    with pytest.raises(ProviderConfigError, match="requires LOCALAPPDATA"):
        load_provider_config(vault)


@pytest.mark.parametrize("slot", ["../escape", "..\\escape", "C:\\escape", "unknown", ""])
def test_vault_rejects_unknown_slots_for_every_operation(tmp_path, slot):
    vault = Vault(tmp_path / "state")
    for operation in (lambda: vault.get(slot), lambda: vault.set(slot, "x"), lambda: vault.delete(slot)):
        with pytest.raises(ValueError, match="Unknown credential slot"):
            operation()
    assert list(vault.root.iterdir()) == []


def test_vault_get_preserves_dpapi_precedence_and_openrouter_env_fallback(monkeypatch, tmp_path):
    vault = Vault(tmp_path / "state")
    monkeypatch.setenv("OPENROUTER_API_KEY", "env-openrouter")
    monkeypatch.setenv("DEEPSEEK_API_KEY", "not-imported")
    monkeypatch.setenv("MIMO_API_KEY", "not-imported")
    assert vault.get("openrouter") == "env-openrouter"
    assert vault.get("deepseek") is None and vault.get("mimo") is None
    for slot in credentials.CREDENTIAL_SLOTS:
        vault.set(slot, "protected-test")
        assert vault.get(slot) == "protected-test"
        vault.delete(slot)
    assert vault.get("openrouter") == "env-openrouter"
    assert vault.get("supabase-session") is None
