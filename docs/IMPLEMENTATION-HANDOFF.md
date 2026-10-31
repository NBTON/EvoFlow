# EvoFlow implementation handoff

> Final verification: [FINAL-VERIFICATION-2026-10-30.md](FINAL-VERIFICATION-2026-10-30.md).
> Its outcomes and limitations supersede pending-status paragraphs below.
> Earlier resume packet: [CONTINUE-HERE-2026-10-30.md](CONTINUE-HERE-2026-10-30.md).
> It records the successful native build, 60 Python tests, 8/8 external-layout UI
> checks, actual running delivery paths, fixes, and remaining final checks.
> Its current-state details supersede pending-status statements below.

Updated 2026-10-30, Asia/Riyadh. Deadline: 2026-10-31 at 20:00 Asia/Riyadh.
The product is implemented. Final verification of the refreshed native UI and
delivery artifact remains pending in the current implementation session.

## Continue from the implementation

Read `AGENTS.md`, the full [build brief](brief/AI-Workflow-Hackathon-Build-Brief.md),
the [README](../README.md), current source/evidence, and relevant installed
Microsoft/LangChain/Supabase guidance before changes. Inspect Git state; preserve
unrelated files and shared skill junctions. Keep native WinUI and authenticated
loopback. No push, deployment, service purchase, or hackathon submission is
authorized.

## Implemented scope

The C#/WinUI companion has a top-center pill, task panel, JSON attachment,
fictional demo stages, streamed progress, results/evidence/export, Settings,
tray controls, and monitor/hotkey/topmost preferences. Its supervisor launches a
hidden Python/LangGraph worker, sends a random per-launch token through stdin,
uses an available loopback port, and owns worker shutdown. The API rejects
unauthenticated requests, browser origins, and invalid hosts.

The supported task is order/inventory/shipment reconciliation on two declared
schemas and a versioned policy contract. The model authors a bounded relational
program using generic operations; trusted code validates and interprets it.
The independent oracle is separate from generation. No handwritten procedure
is seeded as learned. Generated Python, shell, network, filesystem, and
unrestricted SQL execution are excluded. This is a scoped workflow-learning
MVP, not an arbitrary general agent or model-weight training.

The graph inspects contracts, retrieves scoped evidence, reuses or synthesizes,
executes/verifies, evaluates development/regression cases, then promotes or
rejects. Skills retain artifact hashes, contracts, parent/version identity,
knowledge dependencies, check results, lifecycle, and source-run provenance.
SQLite records, artifacts, and durable checkpoints persist; rollback and
schema/policy invalidation are implemented.

Knowledge, explicit preferences, and procedural skills stay separate. The small
corpus uses bounded lexical retrieval, authority/version checks, duplicate
suppression, and deletion. `/memory/graph` projects actual scoped records into
knowledge/preference/skill/run/source/contract nodes. Edges require stored
provenance, dependency hashes, evidence, contracts, or lineage references.
The native Memory window offers inspection, search, filters, history, and graph
navigation. Missing data creates no invented company knowledge; deleted records
and sensitive payloads are excluded.

Supabase uses an automatic protected device session and device-owned workspace,
without a product email/password requirement. The clients use a publishable
key and scoped user tokens, never a service-role key or MCP OAuth. Compact
record writes use an idempotent outbox; offline records survive. Raw datasets
remain local; selected artifacts can use private Storage. Device identity is
not a cross-device account-linking flow.

## Setup and launch

The existing authorized Supabase project is `csvfsltznjdaalnzvgbz`. Its migration
is [20261030125626_evoflow_initial.sql](../supabase/migrations/20261030125626_evoflow_initial.sql).
[Cloud verification](evidence/cloud-verification.json) records owner access,
cross-user denial, and private Storage checks;
[automatic-cloud.json](evidence/automatic-cloud.json) records the device path.
For a separately authorized project, apply the migration with administrative
tooling and configure Auth for this device-session path. Keep administrative
credentials outside the shipped clients.

On Windows x64 with Developer Mode, .NET SDK 10.0.403, WinApp CLI 0.7.1, and `uv`:

```powershell
.\scripts\Setup.ps1
dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64
winapp run native/EvoFlow --detach --json
```

Setup restores pinned Python 3.12/native dependencies and writes the ignored,
machine-specific `worker-location.json`. The worker prefers embedded Python
when bundled. Do not copy the development location file to another machine.
Runtime records, checkpoints, artifacts, presentation settings, and DPAPI
credentials live under `%LOCALAPPDATA%\EvoFlow`.

Save OpenRouter credentials through native Settings, never source/chat/logs.
Supabase connects automatically; Settings exposes synchronization retry. Preserve
offline work and diagnose connectivity before changing authentication. MCP
access and the product device session are separate.

Run demo stages 1–4 in order: acquisition, new-record reuse, schema/split-shipment
change, then policy change. Inspect results, Memory provenance, and exported
evidence. Existing covered app state may reuse immediately. Use a fresh CLI home
as shown in the README for clean acquisition; preserve the user's app data.

## Combined model and thinking-effort routing

[runtime.json](../config/runtime.json) enables one live-validated generator,
`nvidia/nemotron-3-ultra-550b-a55b:free`. Other generation/delegation roles remain
disabled pending independent capability and reliability checks. No paid fallback
is allowed. The initial model-role snapshot is only a proposal.

Product runs deterministically select a combined model/effort profile using
visible task/schema novelty, complexity, independent-check repair, and an optional
explicit enabled delegated role. Covered skill reuse makes no inference call.
Current catalog/endpoint checks use bounded metadata deadlines/cache, zero-price
checks, and provider parameter constraints. Unsupported effort is suppressed
without substitution.

`run.routing.decisions` and `metrics.calls` preserve the reason, requested
model/effort, submitted effort, returned model/provider/request ID, and usage.
Actual `effective_effort` stays `unknown` when unreported; submitted values and
reasoning-token counts do not prove provider conformance. No-inference reuse is
`not_applicable`. [routing-capability.json](evidence/routing-capability.json)
records one successful medium-effort request, not adaptive workflow quality.

Span uses the separate Decisions endpoint. Its
[four-example development calibration](evidence/decision-calibration.json) passed
a predeclared threshold. Respan rejected arbitrary object `state` with HTTP 400;
the script now sends a JSON-serialized string. Automatic scoring stays disabled,
and advisory scores cannot override deterministic checks or budgets.

## Historical evidence and reproducibility

[live-demo.json](evidence/live-demo.json) predates adaptive routing. Its four
fixed-model/fixed-setting stages completed with `1, 0, 1, 1` model calls and
skill versions `1, 1, 2, 3`.

[benchmark.md](evidence/benchmark.md) and [benchmark.json](evidence/benchmark.json)
also predate adaptive routing. Each condition had two acquisition and six
sealed final tasks: no reuse completed 7/8 with eight calls; frozen reuse 8/8
with four; evolving reuse 8/8 with two. Preserve the baseline failure; do not
report all conditions perfect. Acquisition is charged, candidates were frozen
before final tests, and memory ablation is separate.

These observations do not evaluate the new adaptive selector. New benchmark
runs explicitly use `routing_policy="fixed"` for every condition and the paired
memory ablation; the current demo/product path is adaptive. Do not rewrite old
evidence or attribute old reductions to routing. Tokens/calls measure inference
demand, not GPU/FLOP/energy savings. Zero-versus-zero model billing establishes
no dollar savings. Local timing excludes native startup/cloud synchronization;
cloud service cost was not individually metered.

## Verification and delivery

```powershell
uv sync --frozen --link-mode=copy
.venv/Scripts/python.exe -m pytest -q
.venv/Scripts/python.exe -m ruff check worker tests scripts
dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64
```

Tests cover correctness, execution boundaries, persisted reuse and lifecycle,
regressions/rollback, invalidation, cancellation, API/scope boundaries, memory
graph provenance/filtering, synchronization races, routing capabilities,
budgets, and usage reporting. Scope Ruff to application folders, preserving
bundled `.agents/skills` reference examples. On OneDrive use copy mode; rerunning
`uv sync --frozen --link-mode=copy` recovered one interrupted editable install.
No broad filesystem ACL change is needed.

`./scripts/Build-Delivery.ps1` prepares embedded Python 3.12.10 with hashed
requirements and invokes WinApp CLI for an x64 self-contained **unsigned MSIX**.
[python-runtime.json](evidence/python-runtime.json) records its archive hash and
Python signature. This is not a normal trusted installer. Signing/trust,
install/register, and an independent packaged launch still require verification.
Use the documented development launch while delivery verification is pending;
a development process launch alone does not prove an installer works.

Final current-session evidence still needs the refreshed native build and UI
launch/expand/collapse/hover/focus, tray/shortcut, monitor/scaling, Settings,
Memory graph inspection, result routing, worker lifetime/cancellation, and the
chosen delivery route outside its development environment. Use WinApp CLI and
record the actual target. Windows Sandbox was absent at setup; do not enable
features or reboot by inference. Use WinDbg only for an actual crash or hang.

## Retained setup provenance

[agent-setup.json](evidence/agent-setup.json),
[microsoft-plugins.json](evidence/microsoft-plugins.json),
[environment.json](evidence/environment.json), and `skills-lock.json` describe
initial setup, not current readiness. The initially empty Supabase schema has
been superseded by the migration and later cloud evidence.

The setup recorded all 22 LangChain entry-file hashes at source commit
`126c384d8552dcb476b226c244172186cc3f4328`, plus fresh desktop-client discovery
of the LangChain and Microsoft skills. Microsoft setup versions were WinApp
CLI/WinUI 0.7.1 and WinDbg 1.0.0. Preserve shared junctions and working tools.
The PATH `codex` was older than the desktop-bundled executable: discover the
active desktop executable for plugin maintenance instead of hard-coding its
build path or repeatedly authorizing through an older CLI. The read-only
setup verifier remains:

```powershell
python scripts/verify-agent-setup.py --codex <desktop-codex.exe> `
  --langchain-source <langchain-skills-checkout> `
  --output docs/evidence/agent-setup.json
```

Primary sources: [LangChain skills](https://github.com/langchain-ai/langchain-skills),
[Microsoft Windows skills](https://github.com/microsoft/win-dev-skills),
[LangGraph persistence](https://docs.langchain.com/oss/python/langgraph/persistence),
[Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security),
[OpenRouter reasoning metadata](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens).
The full brief retains the original scope and remaining references.
