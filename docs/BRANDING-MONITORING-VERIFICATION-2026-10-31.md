# EvoFlow branding, monitoring and publication verification

Verified October 31, 2026 (Asia/Riyadh). The working implementation was preserved;
this checkout and its existing GitHub remote had no commits before this work.
The user explicitly authorized the initial project commit and push. No deployment,
service purchase, certificate installation or hackathon submission is part of it.

## Changes

- Optional, metadata-only LangSmith monitoring using the existing per-user
  configuration and Windows DPAPI vault. Named conversation/workflow roots and
  decision/execution LLM spans correlate run/session IDs; provider receipts supply
  tokens and reported costs. Errors, cancellations, missing receipts, concurrent
  session isolation and tracing outages have regression coverage.
- The supplied Dark Obsidian Fluent Reverse ICO/PNG and borderless tray glyph
  were inspected and copied unchanged into project assets. Generated Windows
  scale/target-size images and a transparent tray ICO preserve the artwork.
  Main, Settings, Memory and Results windows use native icons; the executable
  embeds the supplied ICO. Existing native package/AUMID identity is preserved.
- README with actual native captures, navigation, expandable sections, GitHub
  Mermaid architecture/evolution diagrams, setup/testing instructions and explicit
  feature/demo/known-limit distinctions. No deployed website or custom README JS.
- The original raw historical benchmark was preserved in ignored local
  `artifacts/local/publication-backup/benchmark.raw.json`. The public
  `docs/evidence/benchmark.json` now contains aggregates and library freeze hashes,
  retaining native comparison-table compatibility while withholding sealed final
  rows, fixture seeds, plans, events and detailed feedback.

## Validation

| Check | Result |
| --- | --- |
| `uv sync --frozen` | Passed; locked environment restored. |
| `.venv/Scripts/python.exe -m pytest -q` | **166 passed**. |
| Ruff check + format check across worker/tests/scripts | Passed; 47 Python files formatted. |
| `dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64` | Passed; **0 warnings, 0 errors**. |
| Native ToolHost test executable | Passed bounded output, nonzero exit, cancellation/process-tree and path-policy checks. |
| Native connector test executable | **30 checks passed**, including real stdio/HTTP fixtures, approvals, schema changes, credential isolation and lifecycle. |
| WinApp native UI smoke | **8/8 passed**: hover traversal, pin, separate Settings/cloud, actual Memory graph, filtering, zoom/history and cancellable editor. |
| Top-anchor geometry | Passed: compact 280×44, expanded 560×520, collapsed 280×44, all y=0 at this display's 100% scale. |
| Native branding and identity | Passed on launched package: native window and executable icons extracted; original package AUMID verified. |
| README | GitHub Markdown API rendered successfully; local image/document links checked; diagrams use supported Mermaid flowchart syntax. |

Sources: [native smoke](evidence/ui-brand-native/test-results.json),
[geometry](evidence/ui-brand-top/top-anchor-results.json),
[native live/branding](evidence/ui-brand-acceptance/results.json) and
[final restart](evidence/ui-brand-final/results.json).
Native screenshots were visually inspected. A screen-strip capture included
unrelated browser UI, so those strips remain local; only geometry assertions
are published. The README uses app-window captures.

## Live LangSmith verification

The user added their key directly to
`%LOCALAPPDATA%\EvoFlow\provider-keys.env`, field **`LANGSMITH_API_KEY`**.
It was imported into the protected `langsmith` slot. Tracing is enabled for
project `EvoFlow` at `https://api.smith.langchain.com`; the workspace UUID was
left blank. No credentials were printed or placed in source, arguments or images.

[Synthetic isolated conversation](evidence/langsmith-live.json): completed with
two model calls and two server-confirmed nested LLM spans. All spans carry the
correct session ID and empty inputs. Actual usage: 1,433 input / 12 output tokens.
Only Joey returned a cost receipt ($0.00001284); DeepSeek cost is unknown.
Readback was retried for indexing without repeating model inference.

[Conversation through the launched native package](evidence/langsmith-native-live.json):
the short workflow-review checklist completed with two model calls and two
correlated LLM spans; all trace inputs are empty. Actual usage: 4,954 input /
67 output tokens. Joey reported $0.00001307; generation cost remains unknown.
No tool actions were requested or performed by this synthetic conversation.
The final package restart restored this same local conversation without another
model request. See [monitoring configuration and data boundaries](monitoring.md).

## Final delivery

Run the final self-contained development layout:

```powershell
.\delivery\EvoFlow-20261031-125713\Launch.ps1
```

The app was launched and verified with WinApp CLI from
`delivery/EvoFlow-20261031-125713/app/AppX/EvoFlow.exe`, observed PID **42280**.
Its owned worker, PID **52212**, resolves to that layout's `runtime/python.exe`.
These PIDs are volatile. The prior delivery's window/native UI checks remain
separate from the final restart and icon extraction check.

The unsigned MSIX SHA-256 is
`8cb761571baa7031b19a32aa3c54e37f4f1c4da72af9f1c74b0d54f1b5758bee`.
[Package integrity](evidence/brand-package-integrity.json) confirms all **53**
image/ICO assets and **25** worker/config payloads match the checkout. It contains
the aggregate-only benchmark. No provider entry file, DPAPI payload or checkout
worker-location file is packaged. The three original supplied assets have
matching source/project/package hashes. Runtime icon paths are relative to the
delivered application; the original Antigravity folder is never required.

Actual window and executable icon captures:
[window](evidence/ui-brand-final/native-window-icon.png),
[executable](evidence/ui-brand-final/compiled-executable-icon.png).
The green borderless tray glyph was observed at the upper-left of the
[Windows overflow capture](evidence/ui-brand-acceptance/tray-overflow.png).
No shortcut rewrite or broad icon-cache deletion was necessary.

README images: [compact](images/native-compact.png), [conversation](images/native-chat.png),
[Settings](images/native-settings.png), [Memory](images/native-memory.png).

## Publication review and limits

Candidate source/evidence files were scanned against the actual four protected
provider/monitoring keys and common credential patterns with no matches. Installed
build-time `.agents/skills`, local credentials/state, runtime binaries, packages,
delivery layouts, caches and publication backups stay outside the staged source.
Staging uses explicit project paths and is reviewed before the initial commit.

The installed Gemini CLI and connected integration catalog did not establish
availability of exact Gemini 3.8 Flash with High reasoning. No delegate or paid
model substitution was used.

Unsigned development delivery still requires Windows Developer Mode and WinApp
CLI. Signed/clean-machine installation, alternate DPI/monitors/high-contrast and
third-party OAuth consent were not newly verified. Existing Cua browser ownership
refusal and the retained Playwright Chrome startup failure remain as documented
in [MCP/Cua verification](MCP-CUA-VERIFICATION-2026-10-31.md). No new workflow
acquisition or comparative benchmark was run. No dollar/GPU savings are claimed.
