# EvoFlow multi-provider final verification — 2026-10-31

Completed locally on October 31 Asia/Riyadh. Live provider smoke checks, isolated
workflow learning/reuse, locked Python checks, native build, external delivery,
native UI regressions, visual review and restart passed. A separate Luna reasoning
stream check received reasoning but failed its exact final-answer check (below).
No push, deployment,
service purchase or hackathon submission occurred. No provider keys or private
reasoning are included in evidence. Historical benchmarks remain unchanged.

## Provider and workflow results

The user confirmed **MiMo Standard API**. Only the endpoint assignment in
`%LOCALAPPDATA%\EvoFlow\provider-keys.env` changed to
`https://api.xiaomimimo.com/v1`; all credential values were preserved. Startup
continues importing nonempty keys into current-user DPAPI storage.

- MiMo Flash and Pro: streamed final marker correct with thinking enabled;
  complete usage receipts, native thinking reported as enabled only.
- Joey: live Decisions API accepted model/effort choice and diagnostic score
  questions; typed answers and distributions validated. Its earlier public-page
  404 did not establish API unavailability.
- GPT-6 Luna: current catalog confirmed low effort and compatible endpoints;
  streamed final marker correct. No fallback/substitution was enabled.
- Additional Luna medium-effort request: **6 reasoning-delta chunks** and 2,930
  reasoning tokens received, private content discarded. Its exact final JSON
  comparison against an exhaustive optimization oracle **failed**, with no retry
  or substitution. The original final JSON was not retained, so a result versus
  formatting mismatch cannot be diagnosed. The driver now retains synthetic
  final JSON for future checks. [Failure receipt](evidence/multi-provider-luna-reasoning-20261030T221715Z.json)
  records 183 input / 2,960 output tokens, about 61.89 s, reported cost $0.00074915,
  and complete usage. This is not a reconciliation workflow-quality result.
- Preceding DeepSeek non-thinking smoke evidence remains separate.

See [smoke receipts](evidence/multi-provider-smoke-20261030T220735Z.json) and
[full checkpoint](MULTI-PROVIDER-CHECKPOINT.md) for per-call usage and limits.
Evidence filenames use UTC; these runs occurred on October 31 in Riyadh.

[Fresh isolated workflow](evidence/multi-provider-workflow-20261030T220759Z.json):
Joey selected direct DeepSeek Flash at logical/native high. Two POSTs total,
2,706 input / 4,505 output tokens, about 25.31 s combined provider latency.
The model-authored bounded relational program passed the 12-order task check
and four independent development cases, then was admitted as active skill v1.
Input 702 reused that skill for 12 fresh orders with **zero model calls/tokens**
and passed independent verification. No repairs or fallback occurred. The home
is under ignored `artifacts/local/`; no real user skills or sealed final fixtures
were used, and the isolated engine did not sync to cloud.

## Checks

- `uv sync --frozen --link-mode=copy`: passed.
- `.venv/Scripts/python.exe -m pytest -q`: **129 passed**.
- Ruff check and format check across worker/tests/scripts: passed, 34 Python files.
- `dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64`: **0 warnings/errors**.
- WinApp CLI unsigned Release packaging: succeeded, embedded runtime included.
- [Top-anchor regression](evidence/ui-multi-provider-top/top-anchor-results.json):
  compact 280×44, expanded 560×520, recollapsed 280×44, all at y=0.
- [Chat regression](evidence/ui-multi-provider-chat/test-results.json): passed
  all four groups, including Shift+Enter newline, double-Enter duplicate guard,
  one verified turn, hidden idle Cancel, separate/reopened Settings sections.
- [Native smoke](evidence/ui-multi-provider-native/test-results.json): **8 passed,
  0 failed**, including hover traversal, pin, connected Settings, actual memory
  records, search/empty/recovery, zoom/history and cancellable preference editor.
- The native task reused the existing verified fictional-demo skill: 12 orders,
  6 delayed, 9 incomplete, **0 calls / 0 tokens**, about 0.2 s displayed. This
  native check is separate from the isolated multi-provider acquisition above.
- All 10 batch PNGs plus the final restart PNG were visually inspected. Native
  top-edge shape, conversation, Settings, graph, empty state and dialog render
  correctly. Settings intentionally scrolls; graph overview labels are small,
  with zoom and a readable accessible list for detail. The white outline around
  a keyboard-focused pill is an intentional focus indicator.

## Delivery and restart

Updated delivery, left running and Ready:

```powershell
& 'C:\Users\nbton\Documents\EvoFlow-Delivery-20261031-011103\Launch.ps1'
```

Latest observed native PID 28596, owned worker PID 4828; these are volatile.
Both executable paths are under that delivery's `app\AppX`; the worker uses
`runtime\python.exe`, not the checkout or `.venv`. Python Authenticode is Valid.
[Final runtime evidence](evidence/multi-provider-final-runtime.json) confirms
Ready and the restored 280×44 top-anchored pill.

Both the prior delivery and the updated delivery shut down their owned workers
without killing unrelated Python. An immediate native-exit assertion checked
just after worker exit was too early; a follow-up confirmed both processes
absent before relaunch. [Shutdown evidence](evidence/multi-provider-shutdown.json)
retains this verification detail.

[Integrity evidence](evidence/multi-provider-package-integrity.json): all 3,469
package entries match the external extracted `app` directory. In the activated
`AppX` directory, 812 Python bytecode cache files regenerated at runtime; every
other package entry matches. All 20 source worker/config payloads match the
package. No provider key file, DPAPI file or `worker-location.json` is packaged.

| Artifact | SHA-256 |
|---|---|
| Updated unsigned MSIX | `C31D6B0534DE957623F700DAE40A2D890171E4F5975DE94226D2264FFFE3D71E` |
| Native EXE | `09726BAF5D365D9D7DF582F6C8B12AB814FB2821A27837BB057E0649BE547E88` |
| Embedded Python EXE | `4D6F5F81A4BCA11191C4C7C6B43632694D0A4CE74E068619D8FDC161D469859A` |

The MSIX is **unsigned development delivery**, requiring WinApp CLI 0.7.1 and
Developer Mode. No certificate was trusted, and no clean-machine installer test
was performed. Windows Sandbox is unavailable on this machine; UI checks were
local. The native EXE is unchanged from the prior verified UI build; this
delivery updates the worker/provider/config payloads.

## Limits

These small checks establish connectivity and one successful DeepSeek workflow,
not comparative model quality, routing optimality, reliability or dollar/GPU
savings. MiMo/Luna workflow quality remains unmeasured; the failed additional
Luna final-answer check remains visible above. Luna low/medium requests were
accepted, but other effort levels are not live-validated. Effective effort remains unknown unless confirmed
by a provider. Direct DeepSeek/MiMo do not return full dollar cost receipts;
zero accumulators must not be presented as free inference.

No primary outage was induced: Joey→Span failure fallback remains covered by
mocks and separate historical Span calibration. New tray/hotkey/alternate-DPI,
high-contrast, monitor-change and native cancellation checks were not added;
earlier UI evidence and Python cancellation tests remain separate. The existing
`-SkipRuntime` missing-runtime guard and hard-coded package version remain
packaging limitations. No claim of OneDrive cloud synchronization or another
device's state is made.

## Screenshots

- [Compact Ready after restart](evidence/ui-multi-provider-native/restarted.png)
- [Expanded conversation](evidence/ui-multi-provider-chat/conversation.png)
- [Separate Settings](evidence/ui-multi-provider-native/settings.png)
- [Actual memory graph](evidence/ui-multi-provider-native/memory.png)
- [Visual review record](evidence/multi-provider-visual-review.json)
