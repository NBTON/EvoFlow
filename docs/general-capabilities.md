# General Windows capabilities and packaging fix

October 31, 2026. This supersedes the initial app/command/UI allowlists and the
earlier browser and MSIX limitations in `local-tool-architecture.md` and the
first local-tools verification report.

## Installed applications

`application_search` discovers Windows App Paths in both registry views, Start
menu shortcuts, and Store/MSIX applications through the Windows shell catalog.
`application_launch` accepts a discovered name or app ID, rather than a four-item
enum. It resolves ambiguity using exact names, executable names, then whole words;
unresolved ambiguity returns choices. Word is discovered as Microsoft Word with
its actual WINWORD.EXE path. Chrome/Edge retain product-specific resolution to
avoid Chromium forks impersonating them through per-user registrations.

Desktop apps launch through Process with fixed/native arguments. Packaged apps
use IApplicationActivationManager. No model-supplied shell strings or executable
arguments are accepted on this route. A stable visible window is required for
verified success. Installed background-only apps or inaccessible windows can
still produce an unverified result rather than a fabricated success.

## Terminal and Windows UI

Terminal accepts installed native console executables from system/user PATH or
an explicit absolute local path, with bounded argument arrays, output, timeout
and process-tree cancellation. The existing diagnostic commands remain automatic.
Other commands require exact-command native approval, showing the resolved
executable, JSON argument array and working directory. Script/interpreter hosts,
SQL clients, inline-code switches and GUI command-line escape routes are denied.
Path arguments pass the same credential/protected-path policy as filesystem
tools. The environment does not inherit provider credentials or worker tokens.

FlaUI interactions are available to accessible applications generally. They are
no longer restricted to Notepad, Calculator and Paint. Select a unique AutomationId
or a controlPath returned by inspection. Missing AutomationId is tolerated.
Inspection is bounded to 150 controls and depth 8. Password controls, command
consoles, credential prompts and EvoFlow approval UI remain protected; browser
interaction uses the dedicated browser tool. Unsupported patterns and stale
selectors return failures. Invoke alone is not task-completion evidence.

## Browser automation is implemented

The `browser` contract uses **Microsoft.Playwright for .NET 1.64.1**, including
its bundled Node driver. It supervises an owned Chrome profile under protected
EvoFlow device storage, independent of the user's ordinary Chrome profile.
Navigation, DOM text/control inspection, reference-based click/fill, bounded
keyboard actions, back, screenshot and close are implemented. References refresh
on navigation/click. No generated JavaScript is accepted.

Each action goes through the existing native policy/approval handoff. The long-lived
browser helper communicates through private stdin/stdout, not an MCP or public
HTTP server. Cancellation terminates its process tree; a later call recreates the
session. URLs and network requests are restricted to public HTTP(S), excluding
loopback/private addresses and file/javascript URLs. Downloads are disabled and
password entry is manual. Tool results include actual page observations; the
model must use them to check the requested outcome.

## Expanded PDFs

Maintained handlers now support compatible text-object replacement, text stamps,
form fields, page rotation, crop, insertion/deletion, PDF merging, append and title
metadata. Sources pass native path review. Outputs reopen before atomic publish;
text replacements and form updates have independent readback checks. Cropping
is not secure redaction. PDF font encodings, text split across separate objects,
complex reflow and OCR are not universal operations; unsupported replacements
fail without publishing a changed file.

## MSIX root cause and fix

The failing payload included python-docx's **unpacked development template**,
including OPC-reserved names such as `[Content_Types].xml` and `_rels/.rels`.
MakeAppx failed with `0x8007007b`. Reproducing serially identified that tree;
excluding its 17 development files made the same payload package successfully.
The application project now excludes that tree while retaining the actual runtime
template, `docx/templates/default.docx`. No shared skills or source runtime files
were deleted. Helper executables/data are `None` payload items rather than
localizable WinUI `Content`, avoiding resource-qualifier warnings from driver
filenames. The delivery script uses MakeAppx's serial option for reproducibility.

The project now builds a real self-contained unsigned MSIX. It includes the native
host, FlaUI, Playwright driver, Python and document libraries. The extracted package
is tested with WinApp activation. Unsigned output still requires signing/trust for
normal installer distribution; no certificate was installed or trusted by this work.

## Evidence

The verified build is running from
`delivery/EvoFlow-20261031-034228/Launch.ps1` (PID 50764). The final MSIX is
`EvoFlow_1.0.0.0_x64.msix`. Its publish sources, package members, extracted
delivery and running layout have matching hashes for the native app/helper and
maintained handlers. Approval details scroll independently of the always-visible
Approve/Decline buttons; broader terminal calls are labeled ProcessExecution.

Required checks passed: `uv sync --frozen`, **154 Python tests**, Ruff check and
format check, and the native x64 build with **0 warnings and 0 errors**.

- [Final live native user-flow results](evidence/ui-general-capabilities-verified/test-results.json):
  Word launch with independent window check; approved ipconfig execution; live
  Example Domain through the packaged browser; PDF text replacement/stamp/rotation
  with independent saved-content and rotation verification.
- [Package contents and hash](evidence/general-capabilities-package.json).
- [Live execution records](evidence/general-capabilities-live-runs.json) and
  [redacted risk/authorization audit](evidence/general-capabilities-audit.json).
- Native harness `--ui-smoke`: real FlaUI inspection/invocation outside the old
  allowlist, with subsequent observation of the changed control state.
- Native harness `--browser-smoke`: public navigation, DOM inspection, link click
  and private/file URL rejection. Python tests cover PDF forms, merge, crop,
  replacement and atomic output validation.

The learned plan interpreter, oracle, sealed fixtures and acquisition accounting
remain separate. No handwritten procedures were promoted as learned skills.

Implementation references: [Microsoft activation API](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication),
[MakeAppx documentation](https://learn.microsoft.com/en-us/windows/uwp/packaging/create-app-package-with-makeappx-tool),
[Playwright browser configuration](https://playwright.dev/dotnet/docs/browsers),
[pypdf page transformations](https://pypdf.readthedocs.io/en/stable/user/cropping-and-transforming.html).
