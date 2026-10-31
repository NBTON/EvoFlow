Implement MCP client support and built-in Cua Driver integration in EvoFlow. Complete the implementation, native UI, packaging, and live verification. Do not stop at a plan, mockup, or coding-agent MCP configuration.

Workspace: C:\Users\nbton\OneDrive - KFUPM\EvoFlow
Repository: https://github.com/NBTON/EvoFlow.git

Start in this existing checkout. Inspect git status and origin; preserve existing uncommitted work and skill junctions. Do not clone over it, reset it, or assume earlier verification reports describe the current build.

1. Read the project before changing architecture

Read AGENTS.md and:
- docs/brief/AI-Workflow-Hackathon-Build-Brief.md
- docs/local-tool-architecture.md
- docs/general-capabilities.md (supersedes older browser and packaging limitations)
- docs/model-provider-contracts.md
- README.md

Inspect:
- worker/evoflow_worker/local_tools.py, conversations.py, api.py, providers.py
- native/EvoFlow/LocalToolBridge.cs, BrowserBridge.cs, SettingsWindow.cs, WorkerClient.cs
- native/EvoFlow.ToolHost/
- scripts/Build-Delivery.ps1, Build-LocalLayout.ps1, Sync-BundledRuntime.ps1
- relevant tests, dependency locks, and current delivery configuration.

Read relevant installed Microsoft WinUI/WinApp CLI, LangChain/LangGraph, dependency-management, and cua-driver skills. For Cua, read SKILL.md, WINDOWS.md, RUNTIME.md, and relevant browser guidance. EMBEDDING.md contains macOS-specific material: do not copy macOS permission or launch assumptions into Windows.

Verify current official documentation and the installed binary's actual schemas:
- https://cua.ai/docs/cua-driver/quickstart
- https://cua.ai/docs/cua-driver/guides/connect-your-agent
- https://cua.ai/docs/cua-driver/concepts/platform-support
- https://cua.ai/docs/cua-driver/reference/cli
- https://github.com/trycua/cua/blob/main/libs/cua-driver/README.md
- https://docs.langchain.com/oss/python/langchain/mcp/index
- https://github.com/modelcontextprotocol/csharp-sdk

2. Implement MCP inside the product

The current worker advertises seven compiled native tools through NativeTools and rejects unregistered tool names. Extend this deliberately; do not remove validation or replace the tool loop wholesale.

Add real MCP client support for local stdio servers and remote Streamable HTTP servers. Use a maintained SDK compatible with the pinned runtime; do not handwrite JSON-RPC or assume an older LangChain adapter remains the recommended API.

Keep the UI native WinUI and the worker on authenticated loopback. Preserve native ownership of local process execution, policy, and approvals. Prefer a native connector broker using the official C# MCP SDK if suitable; another maintained SDK is acceptable when native policy and process supervision remain enforceable. Document the final ownership model.

Implement:
- Connection initialization, capability negotiation, tool discovery, tool invocation, and structured failures.
- Namespaced server/tool identities; schema validation; tool schema/version hashes; invalidation when a server changes tools.
- A registry that combines native tools with enabled MCP tools and dispatches them to the correct backend.
- Bounded discovery, schemas, inputs, outputs, timeouts, model steps, and call counts. Advertise relevant tools without flooding every model request.
- Correct handling of MCP text, structured data, images, and error content. Preserve image evidence for a supported vision model; do not pretend an image was inspected when it was not.
- Cancellation, disconnect/reconnect, readiness, and clean shutdown. Never automatically replay an uncertain mutation.
- App-owned configuration and protected per-server credentials. Support bearer authentication and the standard OAuth flow where required; report unsupported authentication explicitly.
- Native Connections settings to add/edit/remove/enable/disable/test servers, inspect available tools, and see connection errors. Persist across restarts. Never show secret values in logs or transcripts.

Treat server installation/registration as a trusted user configuration action. Models and retrieved content cannot choose new executable paths, change server configuration, select launch arguments, grant permissions, or set credentials. Do not silently import coding-agent configurations or credentials.

Required interoperability is MCP client/tool support. Clearly document the implemented protocol features and any resources/prompts limitations; do not claim universal MCP compatibility.

3. Make Cua Driver a built-in EvoFlow capability

Cua must work from EvoFlow's own chat and tool loop, with native connection status and lifecycle management. Registering it only in Codex, Claude, or another coding assistant does not satisfy this task.

Inspect the existing installation first: resolve the executable and inspect version, status, doctor, and advertised tool schemas. Reuse a compatible install without disrupting an unrelated shared runtime. If installation is needed, use the official Windows distribution/install instructions:
https://cua.ai/driver/install.ps1

Pin and record the version and artifact hashes. Inspect the installer before executing it. Do not modify other agents' configurations or shared skills as a side effect.

On Windows, prefer an EvoFlow-owned cua-driver.exe mcp child over private stdio; current documentation says this route owns its runtime. Confirm behavior against the chosen release. A separate manually started terminal or globally exposed service must not be required. Avoid macOS-only embedding flags and do not invent a Cua .NET SDK.

Integrate it as a first-party connection using the same production MCP discovery/dispatch path as other servers. Native supervision must launch it without a console window, monitor readiness, keep work off the UI thread, recover safely after failure, and clean up only EvoFlow-owned processes. A daemon route is acceptable only if ownership and private access are demonstrated.

Provide the real supported desktop and browser operations through reviewed tool contracts/policy. Use advertised schemas and bundled guide resources/get_guide where available, rather than invented arguments or loading the entire manual into every prompt.

Desktop interaction must select an exact current PID/window, observe first, use fresh snapshot-bound element tokens, act, then read fresh state to verify. Invalidate handles after navigation, window changes, and runtime restarts. Pixels require a matching fresh screenshot and correct coordinate mapping.

Prefer background delivery. A background_unavailable result must remain visible and must not trigger hidden foreground escalation. Foreground/desktop control requires native user authorization for the scope and delivery mode. Serialize desktop control so concurrent requests cannot race for focus or shared state.

For browser tests, use a dedicated test window/profile unless existing-profile attachment is explicitly authorized. Respect Cua's exact window/page binding and EvoFlow's existing browser/network policy. Keep the existing native/FlaUI/Playwright/document tools working; make the chosen backend visible and avoid silent fallback that masks a failed Cua call.

4. Preserve EvoFlow's policy and learning boundaries

MCP/Cua must pass through native approval and policy before effects. Do not trust a server's read-only annotations as sufficient authorization. Treat unfamiliar tools conservatively. Bind approval to the server identity, tool, validated arguments, schema hash, target, and delivery mode; revalidate before dispatch. Approvals must never be model-supplied arguments.

Prevent Cua or generic connectors from bypassing protections through shells, terminals, browser developer tools, credential/security dialogs, arbitrary generated code, unrestricted SQL, protected paths, or EvoFlow's own approval UI. Enforce these restrictions across semantic, pixel, desktop, browser, clipboard, file, and process routes that are exposed. If a route cannot be constrained, leave that route disabled and explain the specific limitation.

Use a host-owned Cua capability manifest or supported authorization mechanism where available. Do not enable unrestricted mode or approval-bypass flags. Keep provider keys and worker tokens out of child environments; pass only credentials explicitly assigned to that server.

Keep connection/session IDs separate from authorization. Audit identities, schema/argument hashes, approval decisions, outcome, timing, and verification without persisting secrets or raw screenshots/UI content indiscriminately. Store selected evidence locally.

Build-time .agents/skills, company knowledge, preferences, learned executable workflows, and independent verification remain distinct. Do not seed the Calculator sequence as a learned skill. Keep the learned-plan interpreter and independent oracle intact. Any future MCP-based learned plan needs versioned contracts, independent checks, and auditable promotion. Include connector/model/tool work honestly in accounting; claim no GPU or dollar savings.

5. Verify through EvoFlow

Add meaningful automated coverage for stdio and HTTP discovery/calls; namespace collisions; schema changes; malformed/error/multimodal responses; authentication failures; approval denial; protected targets; cancellation/timeouts; and process cleanup. Use deterministic test servers without real external mutations. Exercise a generic connector as well as Cua.

Run live acceptance through the built native EvoFlow application:
A. Ask EvoFlow: "Open Calculator, compute 6 × 7, and verify that Calculator displays 42." Its model must choose Cua tools dynamically. Read 42 from a fresh app observation and retain a screenshot plus execution evidence. A text answer of 42 or a successful tool exit is insufficient.
B. Drive a harmless public browser test page through Cua, perform a real interaction, and verify changed page state using fresh evidence.
C. Demonstrate background Calculator operation while another test window stays foreground. Check focus and pointer behavior. If the platform refuses a route, preserve that refusal and test the explicit approval path without claiming background success.
D. Verify denial, cancellation, a stale target, runtime disconnect, app restart with restored configuration, and shutdown without orphaned owned processes.
E. Recheck existing native tools, conversations, memory, and workflow learning. Identify unverified cases honestly.

6. Build and deliver

When dependencies change, update and pin lockfiles intentionally, then verify the frozen environment:

uv sync --frozen
.venv/Scripts/python.exe -m pytest -q
.venv/Scripts/python.exe -m ruff check worker tests scripts
.venv/Scripts/python.exe -m ruff format --check worker tests scripts
dotnet run --project native/EvoFlow.ToolHost.Tests/EvoFlow.ToolHost.Tests.csproj
dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64

Add and run appropriate connector/native tests. Use WinApp CLI and the existing delivery scripts to verify the native UI and launch outside the development environment.

Include the pinned Windows Cua executable and required payload in delivery when redistribution permits, with notices and integrity checks; otherwise provide a tested native guided setup with an honest prerequisite. Do not silently install optional perception/model extensions. Do not claim a self-contained delivery if Cua is missing from it.

Update architecture/setup docs and add a concise MCP/Cua verification report with reproducible commands, tool/runtime versions, sanitized evidence paths, and measured limitations. Launch the verified build and report exactly where it is.

This request authorizes local implementation, necessary dependency/Cua setup, reversible live tests, and local build/package verification. Proceed autonomously. Ask only for genuinely required credentials, OS permissions, or unresolved external requirements. No push, public deployment, purchases, or hackathon submission. Finish with working behavior, verification evidence, and remaining gaps.

