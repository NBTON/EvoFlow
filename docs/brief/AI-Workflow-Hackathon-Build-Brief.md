# EvoFlow: implementation prompt for a self-evolving Windows agent

Prepared October 30, 2026. Submission deadline: **October 31, 2026, 20:00 Asia/Riyadh (UTC+03:00)**.

This is a portable implementation brief for a coding agent. Codex setup is included. Build the app and demonstrate it; do not stop at architecture, mockups, or a simulated success screen. Work autonomously on reversible implementation choices. Ask only for missing credentials, externally imposed permissions, or an essential unresolved requirement. Preserve unrelated work and existing skill junctions. Do not publish, purchase services, or submit the hackathon entry without explicit authorization.

## 0. Authorized repository and Supabase project

Project name: **EvoFlow**.

Repository: **https://github.com/NBTON/EvoFlow.git**. Use this repository as the code destination. Inspect its actual contents and local Git state before changing files; clone it if no suitable checkout exists. Preserve any existing work, read applicable AGENTS.md instructions, and verify the origin remote. This brief does not by itself request a push, public deployment, or hackathon submission.

Supabase project reference: **csvfsltznjdaalnzvgbz**. Dashboard: https://supabase.com/dashboard/project/csvfsltznjdaalnzvgbz . The user reports a **Supabase Pro** subscription. Inspect available capabilities and applicable limits rather than treating Pro as unlimited spending or assuming all features are enabled. This subscription does not change the application's OpenRouter model choices. Use this existing project; do not create another project or branch merely because paid features are available.

The requested Codex MCP configuration is:

```powershell
codex mcp add supabase --url 'https://mcp.supabase.com/mcp?project_ref=csvfsltznjdaalnzvgbz&features=docs%2Caccount%2Cdatabase%2Cdebugging%2Cdevelopment%2Cfunctions%2Cbranching'
codex mcp login supabase
```

Quote the entire URL, especially in PowerShell, to preserve the ampersand and query parameters. Inspect existing configuration first; retain the project scope. Reuse working OAuth authentication rather than asking the user to authorize again unnecessarily. Verify configured authentication and make a read-only MCP tool call to this project before schema changes. In an interactive Codex terminal, `/mcp` is a status command, not a shell command. A saved configuration alone is not proof that the active session has reloaded its connection.

Setup verification on October 30: the project-scoped server was configured and OAuth login completed using the Codex desktop application's bundled executable. A fresh desktop-build client discovered 20 Supabase tools with OAuth status. After the tools loaded in the preparation chat, get_project_url returned https://csvfsltznjdaalnzvgbz.supabase.co and list_tables for the public schema returned an empty table list. Project access is verified; no database objects or paid resources were changed. The separately installed terminal CLI was an older version and did not share usable authentication with this desktop build; do not diagnose that mismatch by repeatedly logging in with the wrong executable. Recheck the actual coding session's access before database work.

The configured feature list enables development/database tools, but project-scoped mode can omit account-management tools. The absence of list_projects or get_project in that mode is expected, not an authentication test. Use an available read-only tool such as get_project_url or list_tables to verify access. Do not equate MCP authorization with application-user authentication, and never put MCP OAuth tokens into the desktop app.

Supabase skill source: https://github.com/supabase/agent-skills . Use the existing relevant Supabase skills if already installed. Otherwise install through the current host-supported mechanism; upstream provides `npx skills add supabase/agent-skills`. Preserve shared/junction-backed skill ownership and avoid duplicate installations. Read the applicable Supabase guidance before implementation.

## 1. Objective and scope

Build a native Windows application for **HUMAIN Future AI Builders Hackathon, Challenge 2: Deliver AI with Less Compute**.

The app learns reusable executable workflows from completed AI tasks, uses them on fresh inputs, and proposes tested improvements when assumptions change. Demonstrate whether this lowers model inference demand while preserving task quality. Model selection supports the application; the central contribution is procedural reuse and evidence-based evolution.

Use a single operational-data task family: reconcile orders, inventory, and shipments; identify delayed or incomplete orders; explain the supported causes; export a structured result and readable report. Use generated, fictional datasets. The model must discover and synthesize the procedure, rather than select one of several handwritten answer scripts. A handwritten fixture generator and independent correctness oracle are appropriate.

The learning happens in external executable skills, metadata, and scoped procedural notes. Model weights remain fixed. Cite Hermes and Prime Agent as inspiration and accurately describe our narrower implementation. Do not claim a reproduction of the full Prime Agent harness or novel model training.

## 2. Required development skills

Install and read skills in the coding agent's actual project environment. Verify discovery before coding and record the source version/commit. Preserve existing instructions and links; do not replace junctions with independent copies. Keep build-time coding-agent skills separate from the app's learned workflow library.

### LangChain

Source: https://github.com/langchain-ai/langchain-skills

The upstream README documents this project-local installation:

```powershell
npx skills add langchain-ai/langchain-skills --skill '*' --yes
```

For Codex, its skill installer is another supported route; use the installed skill-installer's documented procedure if selected. Avoid installing duplicate copies through multiple mechanisms. Read the relevant framework selection, dependency management, LangGraph fundamentals and persistence skills. Read evaluation guidance where applicable, but do not make a new evaluation platform a prerequisite for this small benchmark.

### Microsoft

Official catalog: https://github.com/microsoft/win-dev-skills

Install the `winappcli`, `winui`, and `windbg` plugins using the host's supported mechanism. For Codex, the current upstream commands are:

```powershell
codex plugin marketplace add microsoft/win-dev-skills
codex plugin add winappcli@win-dev-skills
codex plugin add winui@win-dev-skills
codex plugin add windbg@win-dev-skills
```

Inspect existing installation state first. Verify these commands against the current catalog and host help. Codex loads the plugins' skills, rather than their agent personas. If discovery requires another session, report that precisely and arrange the handoff before app implementation. Other hosts should follow the upstream instructions for that host.

Use WinUI skills for setup, design, implementation, and UI verification; WinApp CLI skills for packaging and delivery; WinDbg skills for real crashes or hangs and evidence-based diagnosis. Load the debugging guidance, but do not manufacture a crash to demonstrate using a debugger. Check prerequisites before installing anything; avoid unrelated machine changes and reboots.

## 3. Architecture

**Follow-up revision:** The user authorized general installed-app launch and fixing
the remaining limits. Installed app discovery replaces the four-app enum; native
console commands outside automatic diagnostics use approval; FlaUI supports
accessible apps generally. Playwright for .NET browser operations are implemented
now, rather than deferred. Structured PDF edits are expanded, and the MSIX failure
is fixed by excluding python-docx development-only OPC template payloads. See
[current boundaries and evidence](../general-capabilities.md). This revision
supersedes the earlier scope notes below. Generated scripts and unrestricted SQL
remain forbidden; learned-plan verification and accounting remain unchanged.

**October 31 authorized revision — implemented local tools:** The native client
now supervises a self-contained .NET ToolHost using direct typed JSON IPC. Native
System.IO, Process/ArgumentList and FlaUI implement local filesystem, curated
terminal, installed-application launch and Windows UI tools. The conversation
worker advertises versioned contracts, validates function calls and runs a bounded
model/tool/result loop. Native policy and risk-based approval precede effects.
Structured document operations invoke maintained handlers in embedded Python
with python-docx, python-pptx, openpyxl, ReportLab, pdfplumber and pypdf. This revises
the earlier blanket filesystem/execution restriction for trusted implementations;
arbitrary model-generated scripts, shell strings and unrestricted SQL remain
forbidden. See [implemented policy, packaging and limitations](../local-tool-architecture.md).
The benchmark's learned-plan interpreter and sealed evaluator remain unchanged.
Core tools do not use MCP; outbound MCP connectors are optional. Browser automation
uses Playwright for .NET in Phase 4. Chrome launch works now through the registered
application tool and requires verified window evidence.

Use **C# / WinUI 3 / Windows App SDK** for native desktop UI and **Python / LangGraph** for the agent runtime. Select compatible supported SDK versions from the official templates, pin dependencies, and commit lockfiles.

```text
Native WinUI overlay and detail window
       | authenticated local HTTP + streamed events
       v
Local Python LangGraph worker
       |-- OpenRouter adapters: generation / decisions / embeddings
       |-- isolated workflow execution
       |-- local persistent checkpoints and artifact cache
       `-- Supabase Auth, Postgres, private Storage
```

The app starts and supervises its worker without a visible terminal. Bind the local API to loopback on an available port, require a per-launch random authentication token, restrict accepted clients, and shut down only the worker process tree this app owns. Provide readiness, cancellation, clean shutdown, and useful startup errors. Keep expensive work off the UI thread. Do not accept arbitrary shell commands through the local API.

Supabase is the backend service for accounts and durable records. Run the Python agent and generated workflows locally; do not try to run a persistent Python process inside a Supabase Edge Function. Prefer this local-worker design over adding cloud hosting before the deadline.

Use durable local LangGraph checkpoints for running jobs. Use explicit, versioned records for cross-run skills; checkpoints are not a skill library and do not implement self-improvement automatically. Store large data and intermediate computations outside model messages; pass file references, schemas, bounded samples, and compact computed results.

## 4. Native desktop experience

Use https://www.heyclicky.com/ as a reference for an accessible desktop companion. Implement the user's requested **Dynamic Island-style top-center presence**, with our own visual identity. The top-center requirement comes from the user; reproducing every HeyClicky feature is outside this MVP.

- A small rounded pill near the selected monitor's top edge, approximately 280 x 44 device-independent pixels when idle.
- An expandable panel, approximately 560 x 480 DIPs, for a task, attached datasets, progress, and results. Allow a larger native detail window for tables and comparisons.
- Click or a configurable hotkey to expand. Escape collapses. Provide a tray menu with open, pause, settings, and quit.
- Show concise states such as Ready, Working, Reusing a skill, Testing an improvement, Complete, and Needs attention.
- Support a task text box, native file picker, Run, Cancel, and an explicit demo entry point. Add drag-and-drop if straightforward.
- Display results first, with expandable execution summaries, skill versions, verification outcomes, and measured resource usage.
- Support monitor changes, Windows display scaling, keyboard navigation, and screen-reader names. Do not steal focus while idle or invisibly block clicks outside the panel. Make topmost behavior configurable and avoid obstructing full-screen work.
- Use native XAML controls and Windows composition where appropriate. Do not substitute a browser window or WebView-rendered product UI for the requested native application.

Voice, continuous screen capture, autonomous browser actions, and elaborate animation remain extensions. Bounded FlaUI screen/control inspection and supported interactions are now implemented through registered native tools with approval; full Playwright browser automation remains Phase 4.

## 5. OpenRouter model configuration

Keep model IDs, role mappings, budgets, and permitted fallbacks in configuration. Treat the accompanying model-role file as an October 30 metadata snapshot and proposal, not a quality benchmark or runtime availability guarantee. Recheck endpoint metadata and make a small authenticated capability test before relying on each adapter. Do not ask the user to paste keys into chat or write them into source control.

Suggested initial roles:

| Role | Candidate ID | Initial usage |
|---|---|---|
| Decision/behavior scoring | `respan/span-01-lite:free` | Bounded trace/behavior scores where they affect a decision |
| General agent/planning | `nvidia/nemotron-3.5-lightning:free` | Default generation and tool use |
| Workflow code generation/repair | `cohere/north-mini-code:free` | Candidate program synthesis and targeted fixes |
| More difficult reasoning | `thinkingmachines/inkling-small:free` | Optional, explicit escalation after testing |
| Text skill retrieval | `nvidia/nemotron-3-embed-1b:free` | Optional embeddings with compatible vector dimensions |

If the first general model can also synthesize the required programs reliably, start with one generative model to simplify the experiment; keep the coding-role option available. Assignments are hypotheses to validate, not claims that a model is best.

**Span-01 Lite is a specialized decisions/behavior-scoring model, not a general chat planner.** Its endpoint metadata advertises decisions output. Implement a separate adapter using the current OpenRouter Decisions interface, validate the supported question types for this model, and calibrate thresholds on development examples. Its scores may flag a failed assumption or an unproductive trace; they cannot override deterministic correctness checks. Do not call a scoring model on every graph edge when code already supplies the answer.

Generation, decisions, embeddings, reranking, and speech use distinct interfaces. Validate outputs explicitly, send only supported parameters, and do not assume all free chat endpoints support strict JSON Schema response formats. Count bounded formatting retries.

The user's other models remain optional candidates: Inkling, Nemotron 3 Ultra, Gemma 4 26B A4B and 31B, Nano Omni for perception, Content Safety for moderation, the VL embedding/reranking models, and Fish Audio for speech. Add a feature only when its task requires it. Moderation scores do not validate program correctness.

Both Poolside Laguna 2.1 variants advertise **expiration on October 31, 2026**, the submission date. Keep them disabled as defaults and outside the critical demonstration.

Require bounded retries, respect Retry-After, expose quota/capacity failures, and preserve the job for retry. Do not silently switch to paid models. Record the actual model, provider when available, settings, and fallback events. Missing credentials can support clearly labeled fixture playback for UI development, but playback must never be reported as a live benchmark.

## 6. Execution and evolution

Suggested graph responsibilities: inspect inputs; retrieve candidate skill; check applicability; execute an approved skill or solve the task; verify the result; record outcome and resources; propose a skill; evaluate the candidate; promote or reject. Use conditional edges and bounded loops. A fixed control framework can execute dynamically generated skill programs without permitting the agent to rewrite its evaluator.

Each skill version must include task family, input contract, assumptions, executable artifact/hash, parameters, output contract, dependencies, independent check requirements, parent version, triggering evidence, observed performance, and lifecycle state. Use candidate/active/rejected/retired states with an auditable promotion transaction and rollback.

Candidate skills are synthesized from actual executions; do not fill the demo library with prewritten procedures and label them learned. Retain both useful successes and failure lessons. A successful run is evidence for proposing a skill, not sufficient proof that it generalizes.

Execute generated code in an isolated worker environment with read-only dataset mounts, a limited output directory, no inherited credentials, no network, and bounded time/process/memory use. A Python subprocess by itself is not a security boundary. Use an available OS/container boundary; expose a narrow declarative workflow language if robust arbitrary-code isolation cannot be delivered in time. Do not trade away isolation by running generated code unrestricted in the desktop process. The model can generate a validated executable plan interpreted by trusted operations as an MVP.

Promote only when a candidate passes independent development checks and improves the predeclared efficiency objective without a quality regression on the covered cases. Preserve old-case regression checks. Cap improvement attempts and total learning expenditure. Never modify the correctness oracle, final evaluation fixtures, budget accounting, or admission criteria from agent-generated skills.

Load only relevant skill details. Track schema and business-rule versions; stale assumptions must cause adaptation or an honest unsupported result, rather than plausible but wrong output. Cancellation and repeated failure must terminate cleanly.

## 6A. Agentic retrieval and company learning

Add a small company knowledge and preference layer alongside executable skill learning. Agentic RAG means the agent chooses when and how to retrieve evidence; a growing index and learning from feedback require explicit ingestion and memory-update logic. LangGraph supplies orchestration and persistence primitives, not automatic knowledge validation.

Keep three distinct record types:

- Company knowledge: source-backed policies, definitions, and business rules, with source/version/effective date and access scope.
- Preferences: persistent user/team requirements such as report format, with author, scope, and whether the instruction was explicitly stated or merely inferred.
- Procedural skills: tested executable workflows from section 6, linked to the policy versions they depend on.

After each request, check cheaply whether there is anything new worth retaining. A request may produce no durable update. Store explicit persistent preferences in their proper user/team scope. Treat inferred preferences and factual claims as candidates until corroborated by an authoritative source or appropriately authorized confirmation. A one-time instruction does not change company policy. Agent-generated answers and repeated unsupported assertions do not become evidence simply through repetition.

Implement the loop: retrieve scoped evidence and preferences -> execute the task -> verify -> extract a bounded candidate update when useful -> check authority, contradictions, duplicates, and scope -> version accepted knowledge and index only changed content. Keep source references, support correction/deletion, and exclude superseded versions from current retrieval. Retrieved text is data, not authority to change execution rules. Apply access filters before supplying search results to the model.

Use Supabase Postgres for metadata and preferences, private Storage for selected source documents, and pgvector for semantic retrieval if needed. Match vector dimensions to the tested embedding model. For a tiny corpus, structured/full-text lookup is an acceptable first implementation; semantic search must justify its extra cost. Bound retrieval to relevant evidence rather than placing the whole growing library into each prompt. Set ingestion, query, reflection, and retrieval budgets, and count their full cost in the experiment.

Extend the demo with a few fictional policy documents and an explicit preference, for example: 'For future reports, group delays by warehouse and show unresolved orders first.' On a fresh request/session, retrieve the supported business rule, apply the saved scoped preference, and reuse the correct workflow. Then update an authoritative rule and show that dependent skills are rechecked rather than reused with stale assumptions. Do not require broad enterprise integrations or a new autonomous knowledge-curation platform before tomorrow's submission.

Evaluate retrieval support, preference retention, duplicate suppression, contradiction/version handling, stale-skill invalidation, and user/workspace isolation. To isolate procedural evolution, give all three main benchmark conditions the same approved knowledge corpus and preference snapshot. Measure adaptive memory separately with memory enabled/disabled on paired tasks; do not attribute its gains to workflow evolution. Keep final-test feedback out of the learning loop.

## 7. Supabase records and credentials

Use a dedicated project selected or authorized by the user; do not modify an unrelated connected project or create billable infrastructure by inference. Read current Supabase guidance, use migrations, and test access policies. Keep data tables compact: workspaces/memberships, runs, run_events, skills, skill_versions, skill_evaluations, artifacts, and benchmark_results. Add scoped knowledge entries/chunks and preference records for section 6A. Add columns/indexes needed for ownership, idempotent event writes, version identity, timestamps, and lookup.

Use Supabase Auth for the user identity, Postgres for records, and private Storage for explicitly synchronized artifacts. Apply RLS and matching storage policies with actual ownership/membership checks. Test both allowed and denied access with two users. Keep raw fixture data local unless upload is needed or explicitly selected. Store hashes and references instead of repeatedly uploading large inputs.

The native app **and its shipped local worker are public clients**. Use a publishable key and user-scoped authenticated sessions. Never embed a Supabase secret/service-role key or a privileged database connection string in either component. Store per-user OpenRouter credentials and refresh material in Windows-protected credential storage; do not include them in generated-code environments, logs, or cloud artifacts. Cloud-only secrets, if eventually needed, belong on a controlled server.

Persist current runs and skills locally so connectivity loss does not erase progress. Queue idempotent cloud synchronization and expose sync status. For this single-device MVP, avoid simultaneous editing of a skill version on multiple devices. Ship a real Supabase round-trip test; label any unconfigured backend honestly.

## 8. Demonstration and evaluation

Build three chronological scenarios:

1. First task: the agent analyzes fictional datasets, returns a correct result, and proposes an executable workflow.
2. New records with the same rules: reuse the learned procedure and compute new answers. Show the actual reduction, if any, in inference demand.
3. Changed assumptions: introduce split shipments or a schema/business-rule change. Show the old skill failing an independent check, a proposed revision, regression testing, promotion or rejection, and correct subsequent execution.

Maintain learning, development-validation, and sealed final-test partitions. The agent must not read final-test answers. Freeze candidates before final testing; do not use the final test to choose revisions. Use small but varied cases including duplicate IDs, missing rows, partial shipments, changed thresholds, and new schemas. Explain any unsupported cases.

Compare (A) no reusable skills, (B) a frozen learned library, and (C) evolving skills. Use the same tools, verifier, model-role assignments, and comparable model/provider/settings across conditions so routing does not explain the result. Give each condition isolated state. Charge skill acquisition to the conditions that acquire skills. Repeat paired tasks when quota permits and report sample counts and variation; mark small-sample findings as preliminary.

Report correctness/success rate, input/output/reasoning tokens where reported, model calls, tool calls, retries, end-to-end latency, local execution time, and actual billed cost. Include failures, candidate learning, verification, background work, and amortization in totals. Show cold-start, reuse, and break-even separately.

Free inference means the billed model cost may be zero for both approaches. **Do not claim dollar savings from zero versus zero.** Primary evidence is reduced inference demand per successful task at maintained quality. Token counts across different models are not a hardware-compute measure. Do not invent GPU, FLOP, power, or energy reductions. Any hypothetical paid-price estimate must be separately labeled with its pricing assumptions.

Expose an exportable evidence bundle: benchmark configuration, fixture hashes, skill versions, structured execution events, metrics, and readable comparison. Do not expose hidden chain-of-thought; concise execution summaries and verification evidence are sufficient. Display no fabricated savings counters.

## 9. Deadline order and completion

Deliver a thin vertical slice early: native pill -> task -> real worker/model -> verified result -> saved run. Then add learned reuse, a tested revision, and comparison/export. Refine appearance after that works. Timebox packaging exploration; preserve a working native launchable build throughout.

Required before submission: a working native build and launch instructions, one reproducible end-to-end learning demonstration, model/credential setup, Supabase migrations and a verified persistence round trip, cancellation and failure handling, and an honest benchmark report. Aim to freeze features several hours before October 31 at 20:00 Riyadh time, leaving time to record and verify the demo. Check the platform's actual submission requirements rather than assuming video length or hosting rules.

Use WinApp CLI for a tested delivery route. Prefer MSIX when it works reliably; a documented native development/portable build is a fallback, with prerequisites and signing limitations disclosed. Test outside the development launch environment. Do not claim an installer or package was verified if only a debug process launched.

Meaningful checks: native launch/expand/collapse and scaling; worker startup/shutdown/cancel; live supported-model requests; independent task correctness; no duplicate counting with split shipments; skill persistence after restart; rejected regression and rollback; isolation boundary; user data access denial; and reproducible benchmark totals. Run WinDbg diagnosis when actual native issues warrant it.

Provide the repository, pinned dependencies, configuration examples without secrets, migration/setup instructions, runnable native artifact, benchmark evidence, and a short demonstration script. Finish by reporting what works, how it was verified, measured findings, and remaining limitations. The coding agent's setup skills, the product's learned skills, and the benchmark oracle must remain separate.

## Primary references

- [LangChain Skills](https://github.com/langchain-ai/langchain-skills)
- [Microsoft Windows development skills](https://github.com/microsoft/win-dev-skills)
- [Windows App SDK window management](https://learn.microsoft.com/en-us/windows/apps/develop/ui/manage-app-windows)
- [HeyClicky interaction reference](https://www.heyclicky.com/)
- [Prime Agent paper](https://arxiv.org/html/2608.23552v1)
- [Hermes procedural skills](https://hermes-agent.nousresearch.com/docs/user-guide/features/skills)
- [LangGraph persistence](https://docs.langchain.com/oss/python/langgraph/persistence)
- [LangChain retrieval architectures](https://docs.langchain.com/oss/python/deepagents/retrieval)
- [LangChain memory concepts](https://docs.langchain.com/oss/python/concepts/memory)
- [Supabase vector storage](https://supabase.com/docs/guides/ai/vector-columns)
- [OpenRouter generation model catalog](https://openrouter.ai/api/v1/models)
- [Span-01 Lite description](https://openrouter.ai/respan/span-01-lite/providers)
- [OpenRouter Decisions SDK reference](https://openrouter.ai/docs/client-sdks/python/sdks/decisions/README.md)
- [OpenRouter embeddings catalog reference](https://openrouter.ai/docs/api/api-reference/embeddings/list-embeddings-models)
- [OpenRouter limits](https://openrouter.ai/docs/api_reference/limits)
- [Supabase API keys](https://supabase.com/docs/guides/getting-started/api-keys)
- [Supabase row-level security](https://supabase.com/docs/guides/database/postgres/row-level-security)
- [Supabase Edge Function limits](https://supabase.com/docs/guides/functions/limits)
