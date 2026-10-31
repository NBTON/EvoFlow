import asyncio
import copy

import pytest
from evoflow_worker.local_tools import NativeTools, agent_turn
from evoflow_worker.models import ModelError, OpenRouter, new_metrics


def descriptor(group="generic", name="mcp_fixture_echo", schema_hash="a" * 64):
    return {
        "name": name,
        "description": "Reviewed data connector",
        "parameters": {
            "type": "object",
            "properties": {"value": {"type": "string"}},
            "required": ["value"],
            "additionalProperties": False,
        },
        "schemaHash": schema_hash,
        "serverId": "fixture",
        "generation": "generation-1",
        "group": group,
        "remoteName": "echo",
    }


async def test_native_connector_registry_validates_names_schemas_and_binding():
    tools = NativeTools()
    tools.workspace = "C:/workspace"
    tools.register_connectors([descriptor()])
    invalid = await tools.execute("run", "mcp_fixture_echo", {"value": 42})
    assert invalid["status"] == "failed" and not tools.pending()
    call = asyncio.create_task(tools.execute("run", "mcp_fixture_echo", {"value": "test"}))
    await asyncio.sleep(0)
    delivery = tools.pending()[0]
    assert delivery["binding"] == {
        "schemaHash": "a" * 64,
        "serverId": "fixture",
        "generation": "generation-1",
    }
    tools.register_connectors([descriptor(schema_hash="b" * 64)])
    assert delivery["binding"]["schemaHash"] == "a" * 64
    tools.finish(
        delivery["callId"],
        delivery["deliveryToken"],
        {"status": "denied", "verified": False, "summary": "Contract changed"},
    )
    assert (await call)["status"] == "denied"
    for entries in ([descriptor(), descriptor()], [descriptor(name="filesystem")], [{}]):
        with pytest.raises(ValueError):
            tools.register_connectors(entries)


def test_cua_selection_keeps_backends_explicit_and_native_tools_work_elsewhere():
    tools = NativeTools()
    tools.workspace = "C:/workspace"
    tools.register_connectors(
        [descriptor("cua_desktop", "mcp_cua_desktop_read"), descriptor("cua_browser", "mcp_cua_browser_read")]
    )
    calculator = {s["function"]["name"] for s in tools.relevant_schemas("Open Calculator")}
    assert "mcp_cua_desktop_read" in calculator
    assert not {"application_launch", "windows_ui", "terminal_run"} & calculator
    browser = {s["function"]["name"] for s in tools.relevant_schemas("Use Cua browser")}
    assert "mcp_cua_browser_read" in browser and "browser" not in browser
    ordinary = {s["function"]["name"] for s in tools.relevant_schemas("Open Word")}
    assert {"application_launch", "document", "windows_ui"} <= ordinary


async def test_agent_rejects_tools_not_advertised_for_this_turn():
    class Model:
        async def chat_step(self, *args, **kwargs):
            return {
                "routing": {},
                "tool_calls": [{"id": "1", "function": {"name": "unregistered", "arguments": "{}"}}],
            }

    tools = NativeTools()
    tools.workspace = "C:/workspace"
    with pytest.raises(ModelError, match="advertised"):
        await agent_turn(
            Model(), [{"role": "user", "content": "test"}], new_metrics(), tools, "r", lambda *a: None
        )
    assert not tools.pending()


@pytest.mark.parametrize("vision", [False, True])
async def test_image_evidence_enters_only_confirmed_vision_model_context(vision):
    class Tools:
        workspace = "C:/workspace"

        def schemas(self):
            return [{"type": "function", "function": {"name": "mcp_fixture_echo"}}]

        async def execute(self, *args):
            return {
                "status": "succeeded",
                "verified": False,
                "summary": "Image received",
                "data": {
                    "visionImages": [
                        {"type": "image_url", "image_url": {"url": "data:image/png;base64,test"}}
                    ]
                },
            }

    class Model:
        calls = 0

        def tool_images_supported(self, route):
            return vision

        async def chat_step(self, messages, *args, **kwargs):
            self.calls += 1
            if self.calls == 1:
                return {
                    "routing": {},
                    "tool_calls": [{"id": "1", "function": {"name": "mcp_fixture_echo", "arguments": "{}"}}],
                }
            observed = copy.deepcopy(messages)
            assert any(isinstance(m["content"], list) for m in observed) == vision
            return {"routing": {}, "content": "Observed image evidence"}

    metrics = new_metrics()
    response, evidence = await agent_turn(
        Model(), [{"role": "user", "content": "test"}], metrics, Tools(), "r", lambda *a, **k: None
    )
    assert response["content"] and not evidence[0]["verified"]


def test_image_support_uses_current_provider_metadata():
    model = OpenRouter(None, {})
    route = {"requested_model": "vision", "provider_adapter": "openrouter"}
    assert not model.tool_images_supported(route)
    model.metadata["vision"] = ({"architecture": {"input_modalities": ["text", "image"]}}, {})
    assert model.tool_images_supported(route)
    assert not model.tool_images_supported({**route, "provider_adapter": "deepseek"})
