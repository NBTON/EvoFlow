# Optional LangSmith monitoring

Monitoring is off unless the per-user configuration explicitly enables it and
a protected key is available. Native WinUI and authenticated loopback remain
unchanged. No LangSmith key is required to run EvoFlow.

## Private key entry

Edit `%LOCALAPPDATA%\EvoFlow\provider-keys.env`. The exact key field is
`LANGSMITH_API_KEY`. The repository's `.env.example` contains empty placeholders
only. This private file is outside the checkout; `.gitignore` also excludes
`provider-keys.env` and `.env` files copied into the checkout.

Set `LANGSMITH_TRACING=true` and `LANGSMITH_PROJECT=EvoFlow` locally. Restart
EvoFlow to apply edits. A nonempty API key imports into the existing Windows
current-user DPAPI vault under the `langsmith` slot. An empty assignment preserves
an existing protected key. Set tracing to `false` to disable monitoring.

The default endpoint is `https://api.smith.langchain.com`. EU workspaces use
`https://eu.api.smith.langchain.com`; other endpoints are refused before importing
any keys. `LANGSMITH_WORKSPACE_ID` is an optional workspace UUID for keys requiring
explicit selection. A personal key with access to one workspace can leave it blank.
The local entry file remains a private plaintext editing source; never commit,
share, screenshot or deliberately synchronize it. Keys are not exported into
process arguments or the process environment.

## Recorded information

Explicit SDK run trees record:

- `EvoFlow · conversation` and `EvoFlow · workflow` root runs.
- Decision/execution LLM spans named with the requested model.
- `evoflow_run_id`, `session_id` and `thread_id` on roots and children.
- Provider adapter, requested/returned model, requested/submitted effort,
  provider-confirmed effective effort when available, attempt count and status.
- Provider-reported input/output/reasoning tokens and finite nonnegative cost
  receipts. Missing usage or costs stay unknown. Parent runs carry status/counts;
  usage is attached to LLM spans to avoid counting it twice.
- Workflow reuse/version and independent verification pass status.

Prompts, model responses, datasets, learned plans, file paths, tool arguments or
results, UI images, private reasoning, credentials and exception text are omitted.
Automatic framework tracing is suppressed, including when a host environment
sets `LANGSMITH_TRACING=true`, to keep raw graph state and sealed data local.
This deliberate metadata-only view does not support prompt replay in Playground.
Tool details remain in EvoFlow's local audited evidence rather than LangSmith.

Background trace delivery failures do not fail a task. Worker `/health` exposes
the optional monitor's configuration/delivery status, never a key. Configured
does not mean delivery confirmed. Shutdown attempts a bounded two-second flush.
Legacy fixed-adapter retries appear as one request span with attempt count;
incomplete receipts do not become complete billable totals after a later success.

LangSmith may independently estimate cost using its model catalog. Treat that
estimate separately from `usage_metadata.total_cost`, which EvoFlow sets only
from provider receipts. No pricing table or savings estimate is invented here.

## Verification

The [live receipt](evidence/langsmith-live.json) records a successful synthetic
conversation and server-side readback from the EvoFlow tracing project. Two
provider calls produced two nested LLM spans, all with the same session metadata
and empty inputs. Measured usage: **1,433 input and 12 output tokens**. Joey
returned **$0.00001284**; DeepSeek supplied no cost receipt, so total cost is unknown.
The actual native delivery is verified separately in the
[delivery report](BRANDING-MONITORING-VERIFICATION-2026-10-31.md).

To run a new check explicitly (uses configured model providers):

```powershell
.venv/Scripts/python.exe scripts/verify-langsmith.py --live
```

`--resume` repeats only server-side trace readback from the saved receipt; it
does not send another model request. The helper uses the current async
`client.runs.query` interface and waits for completed indexed spans. Its local
synthetic transcript/state lives under ignored `artifacts/local/`, with no
Supabase synchronization. Only a sanitized receipt is published.

Implementation follows the official [custom instrumentation](https://docs.langchain.com/langsmith/annotate-code),
[custom LLM tracing/usage metadata](https://docs.langchain.com/langsmith/log-llm-trace),
[cost tracking](https://docs.langchain.com/langsmith/cost-tracking) and
[current run-query migration](https://docs.langchain.com/langsmith/smithdb-sdk-migration-query-runs)
guidance. The SDK is explicitly pinned in `pyproject.toml` and `uv.lock`.
