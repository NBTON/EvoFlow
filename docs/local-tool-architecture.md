# Local actions and documents

**MCP revision:** [MCP connections and managed Cua](mcp-cua-architecture.md)
adds an outbound native connector broker alongside the existing compiled tools.
The original tool descriptions below remain historical; the new document
defines connector ownership, policy, authentication, budgets and delivery.

**Current revision:** [General Windows capabilities and packaging fix](general-capabilities.md)
supersedes the initial allowlists, basic PDF scope, deferred browser work and
failed MSIX status described below. Those paragraphs document the initial version.

Implemented October 31, 2026. The native WinUI product and authenticated loopback
worker remain the application boundaries. There is no internal MCP server.

## Request and result path

`MainWindow.RunTask` submits the actual message to the authenticated worker.
`Conversations.execute` calls the bounded `agent_turn` loop. The native bridge
registers availability and a default workspace (`Documents/EvoFlow Files`) at
startup. Only the five compiled tool contracts are advertised as provider
function schemas. With no native connection, chat cannot execute local tools.

The model returns standard function calls. Pydantic rejects unknown arguments
and validates the advertised schema. A live in-memory delivery queue associates
each action with a run, random call identity and single-use delivery token. The
native bridge polls this private API, starts the owned .NET ToolHost, and sends
the typed request on stdin. The host validates policy and emits a risk/action
preview. Application launch is handed back over the private pipe to the long-lived
native client; the same compiled launcher verifies its window again after the
helper exits. Other implementations run in the owned helper. WinUI displays the
action and relevant path/application. Calls requiring
review wait for the native Approve/Decline controls; approval is never accepted
as a model argument. The host revalidates immediately before execution.

Structured results return through the same native client, matching call/token,
then become tool messages for the next model step. Results include status,
verification, readable summary, typed data, errors and artifact paths/hashes.
The native transcript also shows execution summaries and Cancel. Provider-required
thinking continuation is held only in memory during a tool turn and is never
displayed or persisted in audits, metrics or chat records. Tool invocation
alone never counts as proof of success. Chrome success requires a matching
executable process and a visible top-level window stable across repeated checks.
Product-specific resolution prevents Chromium forks from impersonating Google
Chrome through per-user App Paths registrations. UI invoke explicitly
requires subsequent inspection to establish its effect.

Limits: six model execution steps, twelve local calls, one routing decision
(its bounded existing fallback remains), at most seven total provider requests,
five minutes per complete agent turn, 180 seconds per delivery including approval, 45 seconds per native execution,
1–45 seconds per terminal command, bounded context/input/output. Failed calls
are returned to the model; no automatic mutation retry occurs. Canceled/expired
deliveries cannot be revived by late results. Interrupted calls are never
replayed after restart. Exact duplicate mutation calls in a run return prior
outcome without another effect/approval, marked unverified for current state.
Read-only calls refresh their results and retain exact-action authorization for
that request.

## Version 1 capability and policy

| Tool | Implementation | Boundary |
|---|---|---|
| `filesystem` | .NET System.IO | list (200), inspect, UTF-8 read/write (64 KB), mkdir, file copy/move (32 MB); no delete or recursive transfers; destination must not exist for transfers |
| `application_launch` | .NET Process + product-specific installed locations and validated App Paths fallback | Chrome, Edge, Notepad, Calculator; no arbitrary executable, URL or model-supplied launch arguments; fixed native new-window switch for Chrome/Edge |
| `terminal_run` | .NET Process + ArgumentList | absolute known executable locations; hostname/whoami without args; dotnet --version/--info/--list-sdks/--list-runtimes; bounded stdout/stderr, exit code, timeout and process-tree cancellation |
| `windows_ui` | FlaUI.UIA3 5.0.0 | windows, control inspection (depth 5, 150), activate, Invoke/Value/Toggle/SelectionItem patterns; interaction in Notepad/Calculator/Paint; inaccessible, ambiguous and unsupported controls return errors |
| `document` | maintained Python handler in shipped embedded runtime | structured create/inspect/edit DOCX/PPTX/XLSX/PDF; no generated code or user templates |

Safe reads, new supported files/directories in the workspace, registered app
launch, and curated diagnostics use the established policy without another
approval prompt. Existing-file replacement/edit, move, outside-workspace access,
and UI control inspection/interactions require explicit native approval. File
transfers never overwrite. Policy-denied commands are rejected even if asked
through another tool; there is no shell-string escape hatch or approval override
for unregistered commands. Future command expansion needs a reviewed policy and
contract change. Only text/data and supported document extensions can be written
or transferred. Scripts, binaries and executable spreadsheets cannot be authored
through filesystem tools. Formula-like Excel cell inputs become literal text.

Paths are normalized before review and execution. UNC/device/alternate-stream,
reserved-device, wildcard and ambiguous Windows paths are denied. Existing
ancestor components are checked for reparse points; junctions/symbolic links
are rejected rather than followed. Protected areas include application state,
credentials, browser profiles, shared skill/instruction folders and Windows
system writes. No recursively destructive operation is offered. These checks
are application policy, not isolation against a malicious process already
running as the same Windows user and racing filesystem changes.

FlaUI omits password controls. EvoFlow approval/settings, shells, developer
tools, credential prompts and browsers are protected from UI inspection/actions;
browser control remains Phase 4. UI cannot be used as an alternate terminal or
to click the application's own approval buttons. Other accessible applications
support inspection/activation; only the initial interaction allowlist supports
mutation. No UIAccess/elevation or inaccessible-control fallback is attempted.

The helper and subprocesses do not inherit provider keys or worker tokens.
Cancellation kills the owned host/process tree, including document processes
and stalled COM UI calls. Canceling a launch does not close an already existing
user browser; partial file/application effects must be inspected before retry.
Completed artifacts remain usable. Cancellation is not rollback.

The local JSONL audit stores call/run identity, contract version, argument hash,
risk, approval requirement, timestamps, status and verification. It does not
store arguments, document content, UI values/titles, stdout or exception text.
Worker events retain outcome metadata and a result hash. Tool content is passed
to the configured model only for the requested task; the normal final chat
answer is persisted locally. Local tool audit/data is not added to cloud sync.

## Documents and delivery

Pinned `python-docx`, `python-pptx`, `openpyxl`, `ReportLab`, `pdfplumber` and
`pypdf` are in `pyproject.toml`, `uv.lock`, and the hashed `requirements.lock`.
`scripts/Sync-BundledRuntime.ps1` installs the locked runtime dependencies;
`Build-Delivery.ps1` acquires embedded Python 3.12.10 and verifies their imports.
The native build includes the self-contained .NET ToolHost and FlaUI dependencies.
No machine-wide Python, Office installation or internal MCP service is required.
Document handler execution uses isolated Python startup (`-I`), JSON stdin and
a fixed application-owned handler path. Outputs use a temporary file in the
destination directory, format reopen validation and atomic replacement.

`Build-LocalLayout.ps1` prepares a self-contained WinApp development layout.
The final local layout passed live UI verification. The attempted unsigned MSIX
build failed with MakeAppx 0x8007007b; installer verification remains outstanding.
See [the current verification report](LOCAL-TOOLS-VERIFICATION-2026-10-31.md).

Word supports title/paragraph creation, paragraph append and text replacement
(replacing a paragraph can simplify its run formatting). PowerPoint supports
title/body slides, append and indexed slide edit. Excel supports sheet/row
creation and cell edits; formulas/macros and external connections are not
accepted as generated operations. PDF supports text creation, page append and
title metadata edit; arbitrary existing-page layout/text edits and OCR are not
implemented. PDF creation uses ReportLab's default fonts; complex-script layout
and exact Office fidelity need additional work. Office input rejects external
relationships/macros and has ZIP expansion bounds; inputs/outputs have a 16 MB
limit. Deeper Office fidelity using OpenXml/ClosedXML remains a later option.

## Product learning and future interfaces

These are trusted application tools, not learned product skills. `.agents/skills`
remains build guidance. The existing model-authored relational plans, interpreter,
oracle, skill hashes, promotion rules, fixture partitions, acquisition accounting
and sealed final feedback stay separate. New plan families may reference tool
identity + version + argument/output contracts; admitting such a family still
requires independent checks and auditable promotion. Local action traces are not
automatically promoted as learned procedures. No handwritten tool sequence is
seeded into the learned library.

`ILocalTool` and `IBrowserTool` prepare a direct internal interface for Playwright
for .NET in Phase 4. No browser automation engine, DOM actions or browser MCP is
implemented now. MCP is reserved for optional outbound third-party connectors.

## Verification commands

```powershell
uv sync --frozen
.venv/Scripts/python.exe -m pytest -q
.venv/Scripts/python.exe -m ruff check worker tests scripts
.venv/Scripts/python.exe -m ruff format --check worker tests scripts
dotnet run --project native/EvoFlow.ToolHost.Tests/EvoFlow.ToolHost.Tests.csproj
dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64
./scripts/Sync-BundledRuntime.ps1
winapp run native/EvoFlow/EvoFlow.csproj --arch x64 --detach --json
```

Run the native live UI test script and inspect its screenshots and independent
document readback. Mock-provider tests establish protocol behavior; they do not
establish live execution. No GPU or dollar savings claim follows from this work.
