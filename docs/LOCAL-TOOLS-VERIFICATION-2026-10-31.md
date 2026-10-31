# Local tools verification — October 31, 2026

Local and document execution is implemented and verified through the native
user-facing agent. The final running development layout is:

`delivery/EvoFlow-local-tools-20261031-021716/Launch.ps1`

The running app is PID 7232 in that delivery's WinApp-staged `app/AppX` layout.
It uses its embedded Python runtime and self-contained native ToolHost.
The source/staged/delivered/running binaries and maintained handlers match in
[the integrity evidence](evidence/local-tools-delivery-integrity.json).

## What passed

- `uv sync --frozen`.
- `.venv/Scripts/python.exe -m pytest -q`: **151 passed**.
- Ruff check and format check over `worker`, `tests`, and `scripts`.
- `dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64`: **0 warnings, 0 errors**.
- Native test harness: nonzero exits, bounded stdout/stderr, cancellation killing
  the grandchild process, normalized paths and credential/device/stream denial.
- Document tests: structured Word, PowerPoint, Excel and PDF creation/editing,
  format reopen verification, safe Excel literal text, atomic failure behavior.
- Native dispatch tests: schema/version rejection, approval denial preserving
  existing files, terminal policy, execution failures, junction denial and audit
  redaction. Worker tests cover authenticated result delivery, single-use nonce,
  late/canceled result rejection, bounded loops and streamed tool-call fragments.
- The isolated delivery imports all six document libraries without a machine-wide
  Python installation.

## Live UI verification

[Final WinApp results](evidence/ui-local-tools-final/test-results.json) passed
on the delivered app using actual configured models and native tools:

1. Entered **Open Chrome** in a new EvoFlow conversation. The registered native
   tool launched Google Chrome and verified a stable visible window and executable.
   An independent process/window check passed. Product resolution now prioritizes
   Google Chrome's installed location; this machine's per-user `chrome.exe` App
   Paths registration points to BrowserClaw, so basename-only resolution was unsafe.
2. Asked EvoFlow to create a Word file containing an exact title and paragraph.
   The native tool invoked the bundled maintained handler and returned verified
   creation evidence and a usable path.
3. Asked EvoFlow to replace the exact paragraph. The visible native approval area
   identified this file and the destructive risk. Approved only that test edit.
   The agent executed and inspected the document. Independent Word XML readback
   confirmed the new text and absence of the old text; the file hash changed.

The final example is
`C:\Users\nbton\Documents\EvoFlow Files\EvoFlow-local-tools-20261031-023548.docx`.

Live [runs](evidence/local-tools-live-runs.json) and the content-free
[audit extract](evidence/local-tools-audit.json) retain outcome hashes, measured
usage, native execution time, risk and authorization source. Screenshots were
visually reviewed: [Chrome](evidence/ui-local-tools-final/chrome-verified.png),
[creation](evidence/ui-local-tools-final/document-created.png),
[visible approval](evidence/ui-local-tools-final/document-approval.png), and
[verified edit](evidence/ui-local-tools-final/document-edited.png).

The initial live checks exposed wrong-product Chrome resolution and an offscreen
approval placement. Those checks were retained separately; the final evidence
uses the corrected resolver, stable-window verification and dedicated approval
area. A requested call was never sufficient for the final success claim.

## Delivery and limits

The launchable development layout includes .NET, Windows App SDK, FlaUI, Python
and the locked document libraries. It needs Windows Developer Mode and WinApp CLI
0.7.1, and model/network credentials for live agent requests. Rebuild it using
`scripts/Build-LocalLayout.ps1`; refresh embedded dependencies using
`scripts/Sync-BundledRuntime.ps1`.

The attempted unsigned MSIX build failed in MakeAppx with **0x8007007b**; it is
not reported as a verified installer. The separately prepared native development
layout was launched and tested end to end. Signing and a clean-machine installer
remain unverified.

The initial terminal policy is deliberately a curated diagnostic executable
allowlist. UI mutation supports Notepad, Calculator and Paint with accessible
FlaUI patterns; protected/inaccessible/unsupported controls fail clearly. PDF
editing supports page append and title metadata, not arbitrary layout edits/OCR.
Full browser automation is future **Phase 4**, using Playwright for .NET.

See [implemented architecture, contracts and policy](local-tool-architecture.md).
Learned plans, independent oracle, partitions and acquisition accounting remain
separate. No pushes, service purchases, remote deployment or submission occurred.
