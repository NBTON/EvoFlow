# MCP and managed Cua verification — 2026-10-31

Real MCP client support and managed Cua are implemented in EvoFlow. Calculator
operation through EvoFlow chat is verified, including background operation.
**The full acceptance brief is not complete:** the installed Cua 0.38.0 refuses
isolated browser preparation with `browser_endpoint_owner_mismatch`. A real Cua
browser form interaction and changed-page verification remain blocked.

## Delivered implementation

The native `EvoFlow.Connectors` library uses the official C# MCP SDK 2.3.0 for
stdio and Streamable HTTP. Native Settings owns connection configuration,
discovery, protected credentials, OAuth sign-in, status and contract inspection.
The authenticated loopback worker advertises relevant registered tools and
dispatches them to the native broker. Models cannot register executables,
change credentials, authorize actions or select arbitrary launch arguments.

Native approval binds server, tool, schema, arguments, target and delivery mode.
Contracts are revalidated after review. MCP server changes, cancellation,
disconnect and restart invalidate outstanding contracts; uncertain mutations
are never replayed automatically. Native process jobs own and clean up child
processes. Provider credentials and worker tokens are excluded from children.

Cua uses two separate private, console-free `cua-driver.exe mcp` children for
Calculator and the isolated browser. Both use the same broker as generic MCP
connections. Host-owned manifests and native policy restrict exposed routes.
No coding-assistant MCP configuration, manually started terminal or shared
daemon is required for product execution. Calculator steps were chosen by the
model during each run; no Calculator procedure was seeded as a learned skill.

See [architecture and setup](mcp-cua-architecture.md) for ownership, limits,
protocol scope, image handling and exact supported targets. This implements MCP
tools, not universal resources/prompts/sampling/elicitation compatibility.

## Delivery and integrity

Final Release delivery:
`delivery/EvoFlow-20261031-082834/Launch.ps1`.
The verified Release app was left running on the interactive desktop as PID
**41552**, executable `delivery/EvoFlow-20261031-082834/app/AppX/EvoFlow.exe`.

The unsigned MSIX SHA-256 is
`B0E4FF9BA34459B31F76DBA56D7DD9DBF175A47927FCF5D0EC1441FA810428C6`.
It includes self-contained .NET/Windows App SDK, embedded Python, the native
ToolHost, MCP SDK dependencies, Cua executables and MIT notice. Fifteen selected
payload hashes were compared between the extracted package and active WinApp
layout; all matched. See
[package integrity](evidence/mcp-cua/package-integrity.json) and
[active payload integrity](evidence/mcp-cua/active-layout-integrity.json).

An earlier package omitted ToolHost because the packaging platform and payload
path disagreed. Packaging now explicitly builds x64, fails if ToolHost is
missing, and checks the extracted payload. The native bridge fails before
advertising tools when its required executable or assembly is absent. Earlier
packages in `delivery` are preserved and are superseded by the delivery above.

Both `Build-Delivery.ps1 -SkipRuntime` and `Build-LocalLayout.ps1` completed.
The latter also produced `delivery/EvoFlow-local-tools-20261031-083024`.
The Release delivery above is the one selected for final live verification.
It requires Windows Developer Mode and WinApp CLI; it is not a trusted signed
installer. No certificate was installed. A clean-machine installer test remains
unverified.

## Runtime and dependencies

* Cua Driver **0.38.0**, upstream revision `85e2b1318bd7`, Windows x64.
* Driver SHA-256:
  `ffa58d88779ee219304b0c307f4cfa970fe822f830c6ecf9254eda5c29e22bf2`.
* UIA helper SHA-256:
  `69aa6ea0049ce59dfd14593be77c0632f12f6eda3d4a2f4bab1a00e3a72a9754`.
* Cursor helper SHA-256:
  `ffaf56a62b0f3bdaf65f2bc30612e5d6ed408362d4868d6f20e8db93be851f0c`.
* SDK `ModelContextProtocol.Core` / test server `ModelContextProtocol.AspNetCore`
  **2.3.0**; native schema validator **7.4.0**; Python jsonschema **4.26.0**.
* Build machine: .NET SDK **10.0.403**, WinApp **0.7.1**, uv **0.12.18**;
  bundled CPython **3.12.10**.

[Cua integrity record](../config/cua-runtime.json) and lockfiles pin the payload
and dependencies. The compatible existing official installation was reused.
Optional perception/model extensions were not installed. Shared Cua runtime,
other agents' configurations and junction-backed skills were not changed.

## Automated verification

Run from the repository root in PowerShell:

```powershell
uv sync --frozen
.venv/Scripts/python.exe -m pytest -q
.venv/Scripts/python.exe -m ruff check worker tests scripts
.venv/Scripts/python.exe -m ruff format --check worker tests scripts
dotnet run --project native/EvoFlow.ToolHost.Tests/EvoFlow.ToolHost.Tests.csproj
dotnet run --project native/EvoFlow.Connectors.Tests/EvoFlow.Connectors.Tests.csproj
dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64
./scripts/Build-Delivery.ps1 -SkipRuntime
./scripts/Build-LocalLayout.ps1
```

Results: **160 Python tests passed**, lint passed, **43 files** formatted;
ToolHost process/path/cancellation checks passed; **30 connector checks passed**;
native x64 build passed with **0 warnings and 0 errors**.

[Connector results](evidence/mcp-cua/connector-tests.json) cover actual stdio
and HTTP negotiation/discovery/calls, namespace collisions, schema changes
including during review, malformed/error/multimodal responses, output schemas,
bearer authentication failure/success, DPAPI persistence, environment isolation,
credential redaction, approval denial, protected targets, cancellation,
timeouts, disconnect/restart and process cleanup. The public OAuth PKCE flow is
implemented using the SDK; real third-party consent was not exercised.

The existing FlaUI smoke test inspected and invoked a control in a test app and
independently observed the changed state. The existing Playwright browser smoke
test failed because Chrome closed during startup (`TargetClosedException`).
That failure is retained in
[native regression results](evidence/mcp-cua-packaged/regression-native-result.json);
it is separate from Cua's browser ownership refusal and was not substituted for
Cua acceptance.

## Live acceptance and evidence

Only synthetic test instructions, Calculator and the public Selenium test page
were used. The native UI was inspected/operated with WinApp CLI. QA controllers
entered chat instructions and clicked native approval controls; product tool
execution used EvoFlow's own private MCP connections. The controllers are test
code, not a product dependency or an MCP replacement.

On the interactive desktop, build the connector test project, then run its
executable with `--native-qa <case> <EvoFlow-PID> <evidence-directory>`.
Cases include `background`, `browser`, `native`, `deny`, `cancel`, `stale`,
`reconnect`, `memory`, `history` and `shutdown`. The helper invokes WinApp CLI
and checks the native results; `background` creates its own synthetic foreground
witness. `scripts/Test-McpCuaViaDriver.ps1` supplies the alternative UIA
controller and the live `disconnect` case, which accepts an exact owned Cua PID
and independently checks its executable name and parent before termination.

The resumed shell ran on an isolated `exebox-*` desktop where WinApp could not
see the interactive app. A native QA helper launched on the interactive Default
desktop ran WinApp there. Launching the WinUI app on the isolated desktop failed
with the platform's `SetShownInSwitchers` / `E_NOTIMPL`; the delivered app was
launched and tested on the normal interactive desktop. This limitation is not a
successful isolated-desktop or clean-machine test.

| Criterion | Result | Evidence / boundary |
| --- | --- | --- |
| A: Calculator 6 × 7 | Passed | Fresh actual application observation and stable verification report 42; screenshot and execution sidecars retained. |
| B: Cua browser interaction | Blocked | `browser_prepare` refuses with `browser_endpoint_owner_mismatch`; no form typing/submission or changed-page success claimed. |
| C: Background operation | Passed | Another test window remained foreground; before/after foreground HWND and pointer coordinates recorded for every call. |
| D: Denial, cancel, stale target | Passed | Native decline, canceled owned runtime and stale PID/HWND refusal retained; no hidden backend fallback. |
| D: Disconnect, restart, shutdown | Passed with evidence below | Automated transport lifecycle tests, live shutdown and restored built-in settings; live disconnect test recorded separately. |
| E: Existing product | Partial live, automated pass | Native filesystem readback, FlaUI and native memory graph; conversation isolation, executable-plan interpreter, independent oracle and promotion/reuse regression tests pass. A new live learned workflow acquisition was not repeated. Existing Playwright smoke failed as described above. |

Earlier verified background run `128ad8c9-4c54-41b6-9f8f-4862035ca34b` recorded
14 calls, two stable verification samples and zero focus/pointer violations.
The final delivery repeat `f078c595-290b-443a-98cb-5dbc7cdb00c3` (EvoFlow PID
30624) recorded **13 actual MCP calls, zero focus/pointer violations, and two
stable samples of Display is 42**. A stale-token request was denied before
dispatch. Its exact process/run identifiers are recorded in
[background proof](evidence/mcp-cua/background-proof.json),
[execution results](evidence/mcp-cua/background-execution.json) and
[Calculator screenshot](evidence/mcp-cua/calculator-42.png).
That run requested structured verification without an image. An independent
Cua QA observation immediately afterward captured the same exact Calculator
PID 2664 / HWND 34475372, read Display is 42 again and retained the screenshot
(SHA-256 `8b58f051f98b02f9458e8521684b2e7628ba229f7f625e57c4d526e5d8ccb888`).
It visibly shows the expression `6 × 7 =` and result 42. Its source is explicitly
identified in the proof; it is not represented as an image the model inspected.
See [independent observation](evidence/mcp-cua/calculator-independent-observation.json).

The corrected package also created and independently read back a 28-byte native
filesystem test file, SHA-256
`07A2DFFD43A22E667BE2CE86835CE349D5D3180301BEAAC74A2842659D1A4FAE`.
After the live disconnect in run `519e3b81-102f-4aee-8203-fed95f997aef`, the
native Connections panel's **Test / Sign in** action restored Calculator to
`ready`, server 0.38.0, owned PID 39048. The shared daemon and browser connection
were not killed by that test. See
[native reconnection](evidence/mcp-cua-packaged/reconnect-native-result.json).
The final shutdown test tracked six owned worker/Cua/helper descendants; all
exited, with none remaining. Restart then restored both managed connections.
See [shutdown result](evidence/mcp-cua-packaged/shutdown-final-native-result.json).

Further native UI evidence is under [packaged acceptance evidence](evidence/mcp-cua-packaged/).
Generic connector add/test/discovery and a real echo through EvoFlow chat passed
in run `c174162b-855d-4942-bfda-dbbc396c4e35`; the temporary fixture connection
was removed afterward. Native memory opened with 17 recorded nodes and its
filter available. No existing memory contents or credentials were exported.
After the last restart, native chat history restored the successful Calculator
conversation in isolation, without the subsequent browser-failure transcript.
The verified app remains on that conversation. See
[history result](evidence/mcp-cua-packaged/history-native-result.json) and
[native chat screenshot](evidence/mcp-cua-packaged/history-native-evoflow.png).

Cua's browser refusal reproduced through the product's broker and a direct
official-SDK client, using isolated profiles with installed Chrome and Edge.
The final package reproduced the same refusal in run
`8b24b9dc-e5b2-47c9-bc0c-18b076c5a4a5`.
Ownership checking was preserved; no personal profile, unrestricted mode,
approval bypass, hidden foreground escalation or alternate browser backend was
used to manufacture a passing result. Browser acceptance needs a supported Cua
runtime/environment resolution and a fresh end-to-end test.

## Accounting and limitations

[Sanitized runs](evidence/mcp-cua/live-runs.json),
[native audit](evidence/mcp-cua/native-audit.json), and
[accounting](evidence/mcp-cua/accounting.json) retain failed attempts as well as
successes. Accounting is for acceptance testing, not an inference benchmark.
The selected 18 synthetic acceptance runs include 125 model calls, 93 tool
attempts, 1,393,884 input tokens and 21,371 output tokens, including failures.
Tool time includes native review/IPC/discovery; background startup work and
protocol-message counts are not independently metered. Provider cost reporting
is incomplete. There is no GPU, energy, dollar or inference-savings claim.

Images are local evidence and are supplied to a model only when current
capability metadata confirms vision input; no claim of pixel inspection is
inferred merely from sending an image. Local evidence retention was enabled for
these synthetic test connections. General audit records contain identities,
hashes, decisions and timings rather than indiscriminate screenshots/UI data.

Unverified: successful Cua browser interaction, third-party OAuth consent,
signed/clean-machine installation and a new live workflow-learning acquisition.
Other apps/origins and unconstrainable desktop/pixel/shell/code/SQL/clipboard/
file routes remain disabled by the reviewed Cua policy. No push, public
deployment, service purchase or submission was performed.
