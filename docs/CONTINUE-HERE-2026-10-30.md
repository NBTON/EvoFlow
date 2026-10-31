# EvoFlow continuation packet — implementation handoff

> Latest continuation completed 2026-10-31: see
> [multi-provider final QA](FINAL-VERIFICATION-2026-10-31.md) and
> [current provider checkpoint](MULTI-PROVIDER-CHECKPOINT.md). Live provider and
> isolated learning/reuse checks passed, and the updated external delivery passed
> native UI regressions and restart. Remaining-work lists below are historical.

> Continuation verification completed later on 2026-10-30. Read
> [FINAL-VERIFICATION-2026-10-30.md](FINAL-VERIFICATION-2026-10-30.md) for current
> outcomes, delivery hashes, restarted process locations, screenshots and limits.
> The remaining-work checklist and PIDs below are historical handoff state.

Prepared 2026-10-30, approximately 20:00 Asia/Riyadh. The user stopped the
implementation turn to preserve their remaining Codex quota and requested a
handoff. **Continue from existing progress; do not restart or scaffold over it.**
This packet supersedes pending-status statements in older handoffs.

Claude Code handoff addition: root `CLAUDE.md` and
`docs/CLAUDE-CONTINUATION-PROMPT.md` now provide Claude-specific entry points.
Claude's private project-local Supabase MCP was configured with the same scoped
URL/features as Codex. `claude mcp get supabase` verified configuration and reports
**Needs authentication**; run `claude mcp login supabase` or use `/mcp` to authorize
Claude separately. Codex OAuth was not copied. Product DPAPI credentials stay
unchanged. Other Codex connectors are not required for remaining local work.

## Authorization and objective

Workspace: `C:\Users\nbton\OneDrive - KFUPM\EvoFlow`

Origin: `https://github.com/NBTON/EvoFlow.git`

The upstream was initially empty. **There are still no local commits, and the
whole implementation is untracked.** Preserve it. Do not run `git clean`, reset,
overwrite the checkout, or assume untracked means disposable. Runtime, builds,
delivery folders and transient `artifacts/` are ignored. No push has occurred.

The user authorized end-to-end implementation, installation of the required
.NET 10 SDK, and parallel subagents. Do not ask for these approvals again.
**Do not push, deploy, purchase services, or submit the hackathon entry.**
Do not send messages to other people. Keep secrets out of chat/source/evidence.

The latest original request is to finish EvoFlow and these refinements:

1. Joint model + reasoning/thinking-effort routing, including supported delegated
   roles. Respect real capabilities; inspect requested/submitted/effective
   settings and account for all attempts without paid fallback.
2. Native WinUI compact Dynamic Island-style dark pill, smoothly expanding on
   hover with stable pointer traversal, delayed collapse, click/pin/keyboard use.
   Clicky is an interaction reference, never a framework replacement.
3. Focused everyday task/chat/progress surface, separate Settings window, labeled
   Settings button, and real notification-area icon/menu. Windows controls icon
   placement; do not force it into or out of overflow.
4. Remove bright rectangular window outlines/exposed white corners; retain real
   keyboard focus indicators. Verify actual rendered windows.
5. Interactive native knowledge/memory graph from actual stored records and
   explicit provenance: search/filter, pan/zoom, connected hover, node details and
   relationship navigation. Label fictional data; invent no learned memories or
   edges. Existing SQLite/Supabase architecture stays.

Finish required checks/native build, WinApp CLI UI verification, four screenshots
(compact, hover, settings, graph), delivery validation and concise limitations.
The supported product is a scoped order-reconciliation workflow learner, not a
general desktop automation agent or a full Prime Agent reproduction.

## Read these first

- Root `AGENTS.md`.
- **Complete** `docs/brief/AI-Workflow-Hackathon-Build-Brief.md`.
- `config/OpenRouter-Model-Roles.snapshot.json` and current `config/runtime.json`.
- This packet, `README.md`, `docs/IMPLEMENTATION-HANDOFF.md` (older pending-status
  paragraphs in those documents need the final update).

Original supplied brief and model-role JSON are in:
`C:\Users\nbton\Documents\Codex\2026-10-30\wh\outputs\`.
Deadline in brief: **2026-10-31 20:00 Asia/Riyadh**.

Reference URLs already inspected:
- `https://github.com/farzaa/clicky.git`, especially
  `leanring-buddy/CompanionPanelView.swift` and `CompanionManager.swift`.
- `https://github.com/neo4j-labs/agent-memory/blob/a4b47e50912b8e25cf1f40e05ac79f8f9a574000/examples/lennys-memory/README.md#1`.
- Hermes procedural skills and Prime Agent references in the complete brief;
  README now credits them and correctly limits the implementation claims.

## Skills and tools

The relevant Microsoft skills have already been installed and used; do not
reinstall working prerequisites. Read the applicable entries in a fresh agent:

`C:\Users\nbton\.codex\plugins\cache\win-dev-skills\winui\0.7.1\agent-plugin\skills\`
- `winui-dev-workflow/SKILL.md`
- `winui-design/SKILL.md`
- `winui-ui-testing/SKILL.md`
- `winui-packaging/SKILL.md`

WinDbg diagnostic guidance:
`C:\Users\nbton\.codex\plugins\cache\win-dev-skills\windbg\1.0.0\skills\windbg-diagnostic-method\SKILL.md`.
The native exception skill explicitly excludes ordinary managed/XAML errors;
WinApp's stowed-exception capture was used for the actual startup crash.

LangChain skills are project-local under `.agents/skills`: ecosystem-primer first,
then fundamentals/persistence/dependencies and applicable routing guidance. The
Supabase plugin skill must be used for Supabase work. The job-autofill junctions
are unrelated to this project and must remain untouched.

Environment: Windows Home/Core 26H2 build 26300, x64; no Windows Sandbox available.
UI testing is explicitly **local**, not a guest. Do not enable features/reboot.
.NET SDK **10.0.403**, WinApp CLI **0.7.1**, .NET 10 + Windows App SDK **2.6.4**.
Python dev environment 3.12.13; delivery embeds signed Python **3.12.10**.
`global.json`, NuGet lock, `uv.lock`, and hashed `requirements.lock` exist.

OneDrive interrupted an editable Python installation earlier. Recovery succeeded
with **`uv sync --frozen --link-mode=copy`**. Use copy mode here, avoid concurrent
environment modifications. Do not change global ACLs.

WinApp is preferred for all native UI verification. Discover installed command
help. Use `WINAPP_UI_WORKFLOW_ID=evoflow-native-final` consistently for cooperating
calls, known current PID/HWND with `--on local`, check EVERY exit code, and
`winapp ui yield --on local` in finally. Re-discover volatile PIDs/HWNDs. Do not
blindly replay old selectors. Real input requires the correct foreground window.
`send-keys --via send-input` is needed for WinUI keyboard behavior.

## Implementation map

- `worker/evoflow_worker/domain.py`: input contract, independent oracle/verifier.
- `program.py`: bounded generic relational DSL, safe expression AST; no eval or
  arbitrary generated Python/shell/filesystem/network. Dedup/filter/group/join/
  derive/project compose model-authored workflows.
- `interpreter.py`: DSL execution plus closed read-only legacy SQLite path with
  authorizer/SQLGlot compilation and time/output/work bounds.
- `engine.py`: LangGraph inspect/retrieve/apply-or-synthesize/execute/verify/
  development-regression evaluation/promote-or-reject, durable SQLite checkpoints,
  cancellation, skill contract/hash/lineage evidence and rollback.
- `models.py`, **`routing.py`**: free OpenRouter chat adapter, separate alpha
  Decisions adapter, capability checks, joint routing and usage accounting.
- `store.py`: scoped records/events, versioned knowledge/preferences, authority,
  supersession/deletion, atomic skill admission/rollback, idempotent outbox.
- `cloud.py`: automatic Supabase anonymous device auth protected with current-user
  DPAPI, private workspace RLS, bounded selected-source Storage, sync mutex and
  offline adoption. Product has no email/password login requirement.
- `api.py`: random per-launch stdin token, loopback only, Host/Origin/client
  checks, bounded bodies, runs/events/cancel/settings/memory/sync/export/shutdown.
  `/memory/graph` is scoped server-side. New demo run records get explicit
  `data_origin=fictional-demo`; attached datasets get `user-selected`. Origin is
  persisted before cloud sync. Do not backfill old records by guessing.
- **`memory_graph.py`**: bounded actual-record projection, at most160 nodes, real
  source/dependency/evidence/contract/lineage edges, safe metadata/redaction,
  search/type/history filters; no fabricated associations.
- `native/EvoFlow/WorkerClient.cs`: embedded runtime preferred, random token stdin,
  bounded startup, HTTP/SSE, failed-start cleanup, idempotent owned-worker shutdown.
- `MainWindow.xaml/.cs`, `NativePresence.cs`, `CompanionSettings.cs`: pill, smooth
 180ms hover dwell,180ms animation,700ms exit delay/tolerance, click/pin/keyboard
  hold, task-progress hold, monitor/topmost/hotkey controls, stable GUID tray icon,
  Explorer restart recovery, actual frameless rounded region.
- **`SettingsWindow.cs`**: separate protected API-key/device-sync and presentation
  settings. Main collapses before Settings/Memory activation to avoid topmost
  overlap. UI preferences save separately under app data.
- **`MemoryWindow.cs`**: native Canvas/ScrollViewer force layout, pan/zoom/fit,
  hover/focus links, inspector/relationship navigation, accessible node list,
  search/type/history; separate Manage memory dialogs for preference, policy,
  explicit private-source upload, retired verified-skill restore. Mutations read
  back `/state` before success. No WebView/Neo4j.
- `DetailWindow.cs`: results/report/JSON evidence, added readable **Model & effort**
  tab, historical benchmark label.
- `scripts/Setup.ps1`, `Build-Delivery.ps1`, `Launch-Delivery.ps1`,
  **`Test-NativeUI.ps1`**; current self-contained csproj includes runtime/worker/
  config as None assets to avoid PRI warnings.

## Authentication and cloud state

Authorized Supabase project: **csvfsltznjdaalnzvgbz**;
URL `https://csvfsltznjdaalnzvgbz.supabase.co`.
Only public publishable configuration ships. Never put MCP OAuth/service-role
credentials into the app. Anonymous Auth/device sessions were enabled and tested.
Current user's OpenRouter key was entered privately in EvoFlow Settings and is
stored protected; do not ask them to paste it or replace it blindly.

Credentials/state: `%LOCALAPPDATA%\EvoFlow` (DPAPI credentials subfolder, SQLite,
artifacts/checkpoints, companion-settings.json). Preserve all user data. Each
device has its own identity; cross-device account linking is not implemented.
Cloud outage queues local work. New sync mutex/late adoption bug fixes have tests.

Migration: `supabase/migrations/20261030125626_evoflow_initial.sql`.
RLS-enabled workspaces, memberships, records and security-invoker typed views;
private bucket `evoflow-private`. Earlier real two-user CRUD/denial/Storage round
trip passed9 checks; temporary test users/artifacts were removed. Evidence:
`docs/evidence/cloud-verification.json`, `automatic-cloud.json`.
Read-only MCP connection reverified during this session. No new schema needed.

## Routing/accounting specifics

- Product default `adaptive`, benchmark explicitly `fixed` across all conditions
  and memory ablation. Never infer routing from final answers.
- One workflow-validated active generator:
  `nvidia/nemotron-3-ultra-550b-a55b:free`.
  routine effort none, novel medium, complex/repair high. Joint profiles select
  a configured role/model AND effort. Other delegated generation roles remain
  explicitly disabled pending reliability validation (North is a proposal).
- Covered skill reuse records `no_inference`, effective effort `not_applicable`.
- Current `/models` + endpoint metadata checks are bounded10s, cache<=5minutes;
  supported reasoning only, matching provider tags, require_parameters and
  max_price prompt/completion0; no paid fallback/substitution.
- `run.routing.decisions` and `metrics.calls` expose requested model/effort,
  submitted effort, actual returned model/provider/request ID, usage/reason.
  **Effective effort stays unknown if provider does not report it.** Reasoning
  token counts are not proof a setting was honored.
- Attempts/retries/cancel/timeout/transport errors preserve incomplete usage.
  Root additionally fixed successful-retry accounting: an error attempt without
  usage cannot become complete just because a later attempt has a receipt.
- Respan uses `/api/alpha/decisions`; arbitrary object state produced400. Script
  now JSON-serializes state; four predeclared development checks passed .8
  threshold. Advisory only; automatic scoring remains disabled.

## Verified evidence — do not mislabel

**Current Python:** latest full suite **60 passed**, scoped Ruff check passed;
all28 Python files formatted. Commands below. Root added API scope/body/sync/
origin tests; routing tests and graph9 tests also pass.

**Current native:** `dotnet build ... -p:Platform=x64` succeeded with **0 warnings,
0 errors**, latest log `artifacts/native-final-build.log` (transient ignored).

**Current adaptive live development check:** `docs/evidence/adaptive-workflow.json`.
Fresh isolated seeds17→18, no sealed tests. Acquisition used1 call, medium
requested/submitted, effective unknown, Nvidia,1454 input +3023 output tokens
(2451 reasoning included in output), reported cost0, four independent checks and
promotion.87s acquisition; new-record reuse correct,0 calls,~110ms worker time.
No failed attempts/retries discarded. Separate from benchmark, not broad quality
or savings evidence. Artifacts retained in ignored
`artifacts/local/adaptive-validation-af25484351a54b4b99b21f53ef121bf1`.
Capability/calibration: `routing-capability.json`, `decision-calibration.json`.

**Historical PRE-adaptive benchmark:** `docs/evidence/benchmark.json`, `.md`.
Fixed model/settings (reasoning disabled),2 acquisition +6 sealed final tasks per
condition, candidates frozen before final, no final-feedback repairs:
- No reuse7/8 overall,5/6 final,8 calls,15165 input+5226 output.
- Frozen8/8 overall,6/6 final,4 calls,7581+2512.
- Evolving8/8 overall,6/6 final,2 calls,3772+1273.
Model billed0.75% fewer calls/75.3% fewer tokens observed including acquisition;
not dollars/GPU/FLOPs/energy. Keep baseline failure visible. Memory ablation
separate: both correct, preference only applied with retrieval enabled.
Historical `live-demo.json`: four stages correct, calls1,0,1,1; skill versions
1,1,2,3. These files must not be relabeled as adaptive routing measurements.

## Native bugs already diagnosed and fixed

Do not regress these:
- WinRT `DisplayArea.FindAll()` enumeration caused `ClassFactory cannot supply
  requested class` → C000027B. WinApp dump proved IReadOnlyListImpl.GetEnumerator
  → LINQ → TargetBounds. Copy via Count/index into managed array first.
- Style/DWM changes alone left3px bright NC frame. NativePresence now returns0
  for WM_NCCALCSIZE, removes legacy edge styles, zeroes DWM margins and uses a
  rounded window region. Actual compact/hover captures show no bright frame.
- Initial Show(false) prevented foreground input tests. Activate once at initial
  launch; hover/idle restoration still Show(false), no hover focus stealing.
- Separate Settings was obscured by topmost Main. Opening Settings/Memory now
  collapses Main before child activation, preserving saved topmost preference.
- Memory constructor contrast subscription threw COM0x80070490. Narrow unsupported
  event catch falls back to a window-lifetime2s DispatcherQueueTimer using desktop
  SPI_GETHIGHCONTRAST; cleanup only unsubscribes successful subscriptions.

Sanitized startup diagnostic hook remains, never suppresses exceptions. Local
`native-startup-error.txt` and ignored debug logs may contain OLD failures; check
timestamps. WinApp dump/log under `%TEMP%\winapp-dumps` for old PID45712 explains
the original monitor bug; do not upload dumps.

## Latest UI and delivery state at interruption

**A real outside-checkout release-layout app is currently running** (verify live,
these identifiers are volatile):
- Native PID **30484**, main HWND **395970**.
- Memory HWND **1050784** is open/foreground after the scripted tests.
- Owned worker PID **32172**.
- Native executable:
  `C:\Users\nbton\Documents\EvoFlow-Delivery-20261030-195427\app\AppX\EvoFlow.exe`
- Worker executable: same prefix `app\AppX\runtime\python.exe`.
- That layout has hostfxr.dll and bundled Python; NO worker-location.json.
  It is running outside checkout and `.venv`, reusing the protected user session.

Delivery built by `./scripts/Build-Delivery.ps1 -SkipRuntime`:
- Unsigned MSIX: repo root `EvoFlow_1.0.0.0_x64.msix`.
- Reproducible extracted delivery folder:
  `delivery\EvoFlow-20261030-195427\` containing app/, Launch.ps1, README.txt,
  delivery.json and MSIX.
- Copied outside checkout to the Documents folder above. `Launch.ps1` uses
  `winapp run <app> --manifest <app/AppxManifest.xml> --detach --json`, which
  stages/debug-registers layout under app/AppX. This launch **succeeded**.
- An earlier external copy under `%LOCALAPPDATA%\EvoFlow-Delivery-20261030-195427`
  exists too; WinApp reported that existing folder nonexistent, possibly packaged
  CLI AppData redirection. Do not claim that route passed. Documents route passed.
- No certificate trusted, no signed installer tested. This is a native development
  delivery route requiring WinApp CLI0.7.1 + Developer Mode, not a normal installer
  or a clean-machine validation. No Windows Sandbox available.

**Latest WinApp scripted UI batch on outside-checkout PID30484:8 passed,0 failed.**
`docs/evidence/ui-final/test-results.json` and `scripts/Test-NativeUI.ps1`.
Checks: compact280x44; hover expansion +1s pointer traversal; click/pin holds;
no Settings form in chat; separate Settings + cloud connected; stored memory
graph; search/no-results/clear recovery; zoom/history; cancellable management.
Earlier failed cycle report is retained separately; do not mistake it for latest.

Current screenshots under `docs/evidence/ui-final/`:
- `compact.png`
- `hover.png`
- `settings.png`
- `memory.png`
- `memory-search-empty.png`
- `memory-management.png`
- `memory-selected.png` (previous debug-layout graph interaction, not latest PID)

Root visually inspected compact/hover/settings/graph and selected-node captures.
Compact/hover dark rounded frame is corrected; separate Settings no longer
overlaps Main. Windows accent titlebars on conventional Settings/Memory are
legitimate. Settings scrolls because its form exceeds window height.
Graph currently has **6 actual nodes,10 links**, old fictional demo policy and
its real learned skill/runs/contracts. Some old runs have no origin tag; do not
guess/backfill. Overview labels are small at Fit; zoom and accessible list/detail
panel provide readable inspection. Do not fabricate a larger graph for a picture.

Additional manual WinApp interactions passed on the SAME final native code in
the earlier debug-layout PID5960, before external release launch:
- Knowledge node selected, actual source/content/version/details shown.
- Hover connected lines brighten, unrelated nodes dim; visually inspected.
- Zoom twice made scrollable areas; native drag-to-pan changed UIA horizontal
  percent0→14.5033 and vertical0→39.1626.
- Relationship buttons expose actual sourced-from, knowledge-dependency and
  retrieved-evidence provenance. Relationship navigation itself still needs a
  final invoke check if making that specific verified claim.
- Tray overflow really contained EvoFlow icon; exact native context menu/settings
  action still needs final explicit verification.

UI automation discoveries:
- Main IDs: Pill,TaskText,RunButton,CancelButton,BrainButton,SettingsButton,
  PinButton,TaskScroll,RouteText,MetricsText,DetailsButton,DemoStage.
- Settings: TopmostToggle,HoverToggle,MonitorChoice,HotkeyChoice,OpenRouterKey,
  SaveKeyButton,SyncButton,CloseSettingsButton,ConnectionStatus.
- Memory: MemorySearch is AutoSuggestBox GROUP. **Its child `TextBox`** is the
  editable field; do not set-value on MemorySearch group. Type Enter to submit;
  clear with send-keys `ctrl+a backspace enter` targeting TextBox.
- MemoryGraphStatus,MemoryGraphViewport,MemoryZoomIn/Out,MemoryFit,MemoryHistory,
  MemoryTypeFilter,MemoryManage; node IDs start MemoryNode_kind:id, list IDs
  MemoryList_kind:id; relationship IDs MemoryRelationship_hash.
- Click-hold test now uses PinButton toggle-on. Blind Pill toggle after keyboard
  focus can intentionally collapse an already held panel; script must respect
  this state, not treat it as a hover bug.
- Taskbar HWND65864 and overflow HWND132908 were observed; re-discover. Taskbar
  button name Show Hidden Icons opens overflow; discover actual EvoFlow icon slug.

## Remaining work — prioritize completion, not new scope

1. Inspect current live app/worker and latest UI report/screenshots. Preserve all.
2. Finish small missing WinApp checks: tray context menu → Settings, keyboard
   shortcut/Escape/pin, intentional hover exit/reentry without collapse flicker,
   actual node relationship navigation/type filtering/keyboard list. Monitor
   selection/scaling can be checked reversibly if time; do not claim untested
   configurations. Keep evidence scoped and honest.
3. Execute a native task through the external release layout and inspect results/
   **Model & effort** tab. Existing covered demo stage1 should reuse with0 calls;
   do not assume it did. New default adaptive acquisition is separately live-tested
   already. If live inference is needed, respect90s request/3-call budgets and keep
   all attempts. No new benchmark or model tournament is required.
4. Verify owned-worker shutdown when closing EvoFlow, then relaunch the external
   layout if leaving a usable app for the user. Do not kill unrelated Python or
   native processes. Cancellation logic already has Python checks, native cancel
   can be verified if useful without unnecessary inference.
5. View each final PNG and mark/report visual review. Latest scripted report still
   has `visual_review=pending` because final outside-layout screenshots were not
   re-opened after capture at interruption. Do not set PASS without viewing them.
   Save a concise final QA/delivery evidence file with paths/hash/native+worker
   executable locations and limitations. Avoid secrets/session contents.
6. Update README/handoff stale pending statements to actual outcomes, link this
   packet/current evidence. Optionally improve delivery script fail-fast for
   SkipRuntime when bundled runtime is absent, and document tested Documents
   extraction route. Avoid unnecessary rebuilds for documentation-only changes.
7. Required final checks below; they last passed and need repeat only as required
   by new code changes. PowerShell script syntax check is appropriate too.
8. Final user response: concise works/limits, launch/delivery location,4 requested
   screenshots, tests. State unsigned/development delivery and one validated
   generator/effective effort unknown/latency limits. No publish/spend/submission.

Commands (PowerShell, repo root):

```powershell
uv sync --frozen --link-mode=copy
.venv/Scripts/python.exe -m pytest -q
.venv/Scripts/python.exe -m ruff check worker tests scripts
.venv/Scripts/python.exe -m ruff format --check worker tests scripts
dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64
winapp run native/EvoFlow --arch x64 --no-build --detach --json
# OR verified existing external release delivery:
& 'C:\Users\nbton\Documents\EvoFlow-Delivery-20261030-195427\Launch.ps1'
# Existing fresh-launch UI batch assumes compact/foreground state:
.\scripts\Test-NativeUI.ps1 -AppPid <CURRENT_PID>
```

Native test helper currently assumes100% scale for its compact physical-size
assertion; adapt expected size if testing another scale. Use structured readback
and visual evidence. The UI skill allows at most two product fix-and-rerun cycles;
the main visual fix cycles have been completed. Avoid an open-ended polish loop;
report any remaining limitations instead of inventing success.

## Suggested parallel delegation

User explicitly authorizes independent, scoped subagents. Existing earlier agents
finished; a new harness must spawn fresh agents if needed. Suggested ownership:
- Read-only QA/docs reconciliation (README/handoff/evidence only).
- Native tray/keyboard/graph UI checks: **coordinate exclusive UI ownership**;
  never have two agents inject input concurrently.
- Delivery verification/evidence (no builds that race edits or environment sync).
Root owns integration and final validation. There is no reason to recreate core
implementation or ask again for keys/toolchain authorization.
