# MCP connections and managed Cua

EvoFlow is an MCP **client**. The native `EvoFlow.Connectors` broker uses the
official ModelContextProtocol.Core 2.3.0 SDK. The existing compiled ToolHost,
FlaUI, document and Playwright capabilities retain their native implementations.
The authenticated loopback worker merges the broker's enabled tool schemas with
those contracts; it cannot register a process, change credentials or authorize
an effect. The native Connections panel is the configuration authority.

## Transports and credentials

Local connections use redirected private stdio. The native broker starts the
configured absolute executable without a console, with an OS environment
allowlist and only that connection's assigned credential. It uses the SDK's
stream transport for protocol framing, negotiation, pagination and calls.
Windows jobs terminate owned descendants when EvoFlow exits, including an
abnormal exit. Normal disconnect closes stdin and then kills a stalled owned
tree. No shared daemon is stopped. Stdio registration runs trusted software as
the current user; it is not a sandbox for malicious installed servers.

Remote connections use Streamable HTTP with redirects and automatic mutation
reconnection disabled. Remote endpoints require HTTPS; loopback HTTP is allowed
for deterministic local test servers. Endpoint URLs cannot contain credentials,
fragments or query strings. Legacy HTTP/SSE fallback is deliberately disabled.

Bearer credentials and the SDK OAuth token/client-registration cache are stored
with Windows CurrentUser DPAPI under `LocalAppData/EvoFlow/connections`.
Configuration JSON holds no secret values. Changing endpoint, executable,
authentication mode or OAuth client identity clears the previous credentials.
Stdio bearer assignment uses `MCP_SERVER_BEARER_TOKEN`; the registered server
must support that explicit convention. Arbitrary inherited environment secrets
and coding-agent credentials/configuration are not imported.

HTTP OAuth uses the SDK's standard protected-resource discovery, authorization
server discovery, dynamic registration or configured client ID, PKCE, state and
issuer verification, refresh and token cache. Sign in / Test is an explicit
native action that opens the authorization page and receives its callback on an
ephemeral loopback listener. Startup does not open an authorization page.
Servers requiring unsupported grants or unavailable registration produce a
connection failure; no password collection or alternate authentication is
invented. Real third-party OAuth account consent remains separate from the
deterministic bearer transport tests.

## Contracts, policy and budgets

Tool identity contains a validated connection ID and a digest of the exact
remote tool name. A separate SHA-256 covers the full server input/output schemas,
the narrowed host schema and host-policy version. Connection generations change
on restart. Discovery bounds are 128 tools, four pages, 48 KB per input schema,
300 KB of discovery schema/description data and 64 enabled registry entries.
External schema references and schemas deeper than 24 levels are refused.
Schema validation runs in both Python (`jsonschema` 4.26.0) and the native broker
(`JsonSchema.Net` 7.4.0). Declared structured output schemas are validated too.
Tool-list notifications invalidate the generation and remove advertised tools;
Test reconnects and discovers a fresh contract.

The native broker re-discovers before approval and after it. Approval is tied to
connection identity/generation, tool/schema hash, canonical argument hash,
independently checked live target and delivery mode. Session IDs confer no
authority. Server read-only annotations never suppress native review. Generic
connectors admit explicitly reviewed data tools; tool/argument contracts exposing
execution, code, SQL, files, credentials, clipboard, process or UI authority stay
disabled until a reviewed native adapter exists. Registering a server does not
override those restrictions.

Cua calls are serialized across its desktop and browser runtimes. The complete
MCP chat turn is bounded to 24 model steps, 40 tool calls, 25 total provider
requests including routing, ten minutes overall and 180 seconds per delivery
including review. Execution after approval is bounded to 45 seconds. Existing
native-only turns retain their six-step/twelve-call/five-minute limits.
Stdio and HTTP frames are bounded to 8 MB, outputs to 32 content blocks,
90 KB text/structured content and bounded PNGs. Only relevant connection groups
are advertised, with at most 32 function schemas per model step.

Disconnect and cancellation invalidate live capabilities, stop owned processes
and never replay a mutation. Exact duplicate mutations in a chat request return
the prior outcome with `verified=false`; observations refresh. Cancellation is
not rollback. A later request must reconnect and observe partial effects.

## Built-in Cua

The bundled Windows runtime is Cua Driver **0.38.0**, commit `85e2b1318bd7`.
`config/cua-runtime.json` records every bundled executable's SHA-256; the native
app verifies them before startup. `Sync-CuaRuntime.ps1` checks the driver pin
before copying the compatible existing installation. MIT notices accompany the
payload. No perception/model extension, agent skill or shared daemon is installed
or reconfigured. Python/TypeScript embedding APIs and macOS flags are not used.

Two first-party connections use the same MCP client/discovery/dispatch path:

* **Cua Calculator:** a host-owned capability manifest scopes launch and native
  observation/input to Calculator. On UWP Windows, the trusted System32
  ApplicationFrameHost executable is also in the manifest; native policy checks
  the exact HWND's child process against the installed Microsoft Calculator
  package on each dispatch. Other hosted windows are filtered/refused. Input
  requires a current snapshot-bound element token for that exact PID/window,
  expires after 90 seconds and is invalidated by input or runtime restart.
* **Cua isolated browser:** a separate manifest permits typed browser tools,
  isolated profiles and `https://www.selenium.dev` only. Existing profiles,
  browser chrome, developer tools, passwords, uploads/downloads, clipboard, raw
  desktop/pixel input and generated scripts are disabled. The origin restriction
  cannot be combined with generic desktop tools in one Cua runtime.

This is deliberately bounded Cua support, not unrestricted computer control.
Other applications/origins need an additional reviewed native policy and manifest.
Foreground delivery needs exact native review. A `background_unavailable` or
other refusal remains an error and never triggers a hidden backend or delivery
fallback. Cua-selected turns exclude competing native UI/browser backends.

## Results, evidence and learning

Text, structured data, PNG images and error/refusal content retain their meaning.
A tool's successful exit or returned mutation is not verification. Fresh state
observations establish observed state; stable `verify_state` predicates establish
only their stated postcondition. Cua structured `status:refused` is treated as
failure even when the server omits MCP `isError`.

Images are stored locally with hashes. A bounded current image is supplied to
the model only when current OpenRouter capability metadata confirms image input;
other providers get structured/text evidence and an explicit indication that
pixels were not supplied. Sending an image is not proof it was inspected. Raw
images do not enter metrics, chat history, cloud sync or default audit records.
The optional native **Keep local execution evidence** checkbox retains selected
bounded result sidecars, including observed application/page content, for review.
Default audits contain identities, hashes, authorization/outcome, timing and
verification rather than raw arguments, screenshots or UI content. Selected
evidence also measures foreground HWND and pointer coordinates before/after Cua.

No local automation trace is promoted to a learned skill. `.agents/skills`,
knowledge, preferences, the learned-plan interpreter, independent oracle,
sealed fixtures and acquisition accounting retain their separate boundaries.
Provider calls and tool time remain in the existing run metrics. No GPU,
energy or dollar savings are inferred from this integration.

## Protocol scope and setup

Implemented: initialization/capability negotiation, paginated tool discovery,
tools/list_changed invalidation, schema validation, calls and cancellation,
stdio lifecycle, Streamable HTTP, bearer and SDK OAuth. Resources/prompts,
sampling, elicitation, arbitrary embedded resources, audio, task extensions and
universal MCP compatibility are not claimed. Unsupported content fails explicitly.
Cua's reviewed `get_guide` tool supplies bounded guidance on demand.

Rebuild the locked runtime with `uv sync --frozen`,
`./scripts/Sync-BundledRuntime.ps1`, `./scripts/Sync-CuaRuntime.ps1`, then the native
x64 build. Use native Settings > MCP and Cua Driver to add/edit/remove/enable,
test/sign in, disconnect, inspect tool contracts and select evidence retention.
Save followed by Test activates edited connections; enabled configurations
restore on app restart. The development MSIX/layout includes the native SDK
dependencies, Cua executables/notices and embedded Python. It remains unsigned
and requires the documented WinApp/Developer Mode delivery route.

Packaging explicitly builds the native ToolHost with `Platform=x64` and checks
the extracted delivery for its executable/assembly, connector library, Python
worker and Cua payload. This avoids advertising compiled native tools from a
package that accidentally omitted them because of an AnyCPU output path. The
native bridge also refuses startup when that required payload is missing.

## Primary references checked

* [Official C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)
* [SDK transports and OAuth](https://csharp.sdk.modelcontextprotocol.io/)
* [Current LangChain MCP interface](https://docs.langchain.com/oss/python/langchain/mcp/index)
  uses MCPAdapter in LangChain 1.4+, rather than the older adapter package. This
  product keeps transport/process authority native instead of adding that layer.
* [Cua quickstart](https://cua.ai/docs/cua-driver/quickstart),
  [connect your agent](https://cua.ai/docs/cua-driver/guides/connect-your-agent),
  [platform support](https://cua.ai/docs/cua-driver/concepts/platform-support),
  [CLI](https://cua.ai/docs/cua-driver/reference/cli),
  [permission manifests](https://cua.ai/docs/cua-driver/guides/permissions),
  [pinned upstream README](https://github.com/trycua/cua/blob/85e2b1318bd7/libs/cua-driver/README.md).
