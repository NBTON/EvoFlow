# Conversation execution and session isolation fix

Verified October 31, 2026, Asia/Riyadh. This supersedes earlier evidence that called
the reconciliation-only interface a working conversational assistant.

## Confirmed cause

The native `RunTask` handler sent all text to `POST /runs`, with a default demo
stage and no session ID or conversation context. When the request omitted a
dataset, the worker automatically constructed a fictional reconciliation fixture.
`Engine.run` only synthesized/verified reconciliation programs, and the native
handler displayed its order summary/report. A greeting therefore ran the demo
pipeline; an ordinary assistant answer never existed to render.

The currently running old app reproduced the user's exact `hi my name is omar`
exchange. Its screenshot is [preserved here](evidence/ui-conversation-sessions/before-fix.png).
The original task and fictional report remain in local storage.

## Changes

- Normal messages use `POST /chats/{session_id}/messages`, then session-tagged
  run events/results. Actual user text and bounded completed context reach the
  existing Decisions router and selected generation provider. The general chat
  prompt does not contain fixture contracts or relational-program instructions.
- `POST /runs` requires supplied data or explicit demo selection. Settings runs
  sample demonstrations explicitly. Editing a later draft cannot retain a demo flag.
- New Chat creates a UUID session, clears messages, attachments, diagnostics and
  execution state, and focuses the input. History reopens prior chats. Earlier
  task results have a separate submenu and visibly label fictional demonstrations.
- SQLite `chat_sessions`, `chat_messages`, and `chat_runs` tables preserve local
  transcripts independently of Memory and the cloud outbox. Context is limited to
  48 messages/48,000 characters, preserving the newest user message. Failed and
  canceled turns do not become future model context.
- Older running requests remain scoped to their original sessions. Native view
  guards protect streamed events, result fetches and cancellation callbacks.
  Shutdown/restart marks interrupted work with an explicit error. Offline-to-device
  adoption during an in-flight request preserves its scoped completion.
- Attached Dataset JSON reaches the actual conversation model, including every
  accepted row, with a 32,000-character limit and explicit rejection above that.
  Chat does not claim tool execution or independent verification of prose answers.
- API, authentication, missing-credential, empty-answer and incomplete-stream
  failures remain errors. No fixture substitution or generation-model fallback
  is added. Existing bounded Decisions fallback remains intact.
- The top edge, native shape, bottom corners, hover/expansion animation, green
  processing animation, Run task, Memory, Settings and provider integrations remain.

## Verification

`uv sync --frozen` succeeded. The full Python suite passed **139 tests**; scoped
ruff lint/format checks passed for `worker`, `tests`, and `scripts`. Native x64
build passed with zero warnings and errors.

[Live API evidence](evidence/conversation-live-20261030T222822Z.json) records actual
Decisions and generation requests, responses, provider receipts and usage:

| Check | Observed result |
|---|---|
| A: Hi, my name is Omar. | Natural greeting acknowledging Omar; no order data. |
| B: What is my name? | Omar, based on this conversation. |
| C: fresh session, earlier name? | No name supplied in this session. |
| D: What is Python? | Relevant explanation; DeepSeek Flash, effort `none`. |
| D: DAG longest-path algorithm/proof | Code, proof and complexity; DeepSeek Flash, effort `low`. |
| Orders with no dataset | Asked for actual data; no invented analysis. |
| E: no configured generation credentials | Failed with an explicit credential error; no assistant result. |

Each successful live check made one Decisions request and one generation request,
with zero workflow tool calls. HTTP 401, missing/incomplete output, cancellation,
restart, same-session concurrency and cross-session access also have mocked
provider/API regressions. The credential failure check uses an empty vault in an
isolated test app; saved user credentials were not changed. Test data/history is
under ignored `artifacts/local`, without cloud startup or synchronization.

The selected generation model may be the same for simple and complex tasks;
effort is selected jointly. Requested/submitted settings are recorded. Provider
receipts do not independently confirm an effective thinking effort, so it stays
`unknown`. This verification does not establish comparative model quality,
hardware compute savings or dollar savings.

The [native session regression](evidence/ui-conversation-sessions/test-results.json)
passed on the packaged release with real model calls. It verified greeting/follow-up,
New Chat while the old task was still processing, no old name/output in the new
chat, reopening the old transcript, and the legacy-results submenu. Reviewed
screenshots show [same-chat context](evidence/ui-conversation-sessions/same-chat-context.png),
[new-chat isolation](evidence/ui-conversation-sessions/new-chat-isolated.png), and
[processing](evidence/ui-conversation-sessions/processing.png).

The [top-anchor regression](evidence/ui-conversation-sessions/anchor/top-anchor-results.json)
passed: compact 280×44, expanded 560×520, and recollapsed 280×44, all at y=0.
No shape/animation implementation changed.

An unsigned self-contained development delivery was built with WinApp CLI,
copied to `C:\Users\nbton\Documents\EvoFlow-Delivery-20261031-013337`, and launched
through its `Launch.ps1`. A launch attempt while the copy was still finishing
showed a startup error; after the copy completed, restarting succeeded. The final
running app is PID 37404 with its owned bundled worker PID 43904. [Key payload and
package SHA-256 comparisons](evidence/ui-conversation-sessions/external-delivery-integrity.json)
all match the built delivery.

[Restart preservation checks](evidence/ui-conversation-sessions/restart-preservation.json)
passed after reopening the previous transcript from History in that standalone
delivery. A further New Chat preserved the exact Memory/settings hashes and all
12 legacy executions. The original incorrectly processed greeting can be opened
with its [archived fictional demonstration label](evidence/ui-conversation-sessions/legacy-result-labeled.png).
The app is left running with an [empty new chat](evidence/ui-conversation-sessions/ready-new-chat.png).

The earlier `scripts/Test-ChatUI.ps1` entry point now runs the actual conversation
session regression instead of validating an implicit order-demo result. These UI
tests make actual model requests using locally saved credentials.

## Boundaries

Ordinary conversation currently receives session history and explicit attachments,
not persistent Memory. Starting a new chat does not delete Memory/settings. Generated
code in a conversational answer is text; the app does not execute arbitrary code.
No push, deployment, paid service purchase or hackathon submission was performed.
