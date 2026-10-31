# Multi-provider implementation — live verification

Updated 2026-10-31 after authorized live checks. All registered providers passed
connectivity smoke checks; Joey → DeepSeek synthesis → independent verification
and admission → zero-call fresh-input reuse passed in isolated development state.
An additional Luna medium-effort stream check observed reasoning deltas but
failed its exact final-answer check; that failure is retained below.
Updated delivery and native UI checks are complete; see
[final QA](FINAL-VERIFICATION-2026-10-31.md) for the tested Documents launch path.
The application is still the scoped reconciliation workflow learner, not a general
chat/code/browser agent. Native shape, animations, cancellation and admission are
preserved. This document supersedes earlier one-generator configuration descriptions;
historical benchmark/live evidence remains unchanged and does not validate these models.

## Live results — 2026-10-31 Asia/Riyadh

The user confirmed **Standard API** for MiMo. Only `MIMO_BASE_URL` in the local
editing file was changed to `https://api.xiaomimimo.com/v1`; key values were
preserved. The previous Singapore-endpoint 401 is retained as historical handoff
context, not a result from this run. The standard endpoint is documented by the
[official MiMo API reference](https://mimo.mi.com/docs/en-US/api/chat/openai-api).

[Smoke receipts](evidence/multi-provider-smoke-20261030T220735Z.json):

| Check | Result | Input / output tokens | Approx. POST latency |
|---|---|---:|---:|
| MiMo Flash, thinking enabled | Final marker correct | 18 / 24 | 4.04 s |
| MiMo Pro, thinking enabled | Final marker correct | 18 / 57 | 3.04 s |
| Joey Decisions, choice + score | Validated answers/probabilities | 890 / 3 | 0.84 s |
| GPT-6 Luna, low effort | Final marker correct; metadata gate passed | 16 / 8 | 3.07 s |

MiMo reported 18 and 51 reasoning tokens respectively. Luna's trivial marker
request reported zero reasoning tokens. A separate
[medium-effort optimization check](evidence/multi-provider-luna-reasoning-20261030T221715Z.json)
observed **6 reasoning-delta chunks**, 24 visible chunks, and 2,930 reasoning
tokens. The exact final JSON comparison against an exhaustive optimum **failed**;
no retry/substitution was attempted. Original final JSON was not retained in
that run, so result versus formatting mismatch cannot be diagnosed. Later
script runs retain synthetic final JSON separately from private reasoning.
This one request used 183 input / 2,960 output tokens, about 61.89 s POST
latency, and reported cost $0.00074915 with complete usage. It establishes
reasoning-stream handling, not correct optimization or workflow quality.
Every effort level remains untested; effective effort remains unknown for all.
DeepSeek's preceding non-thinking connectivity check is retained separately in
[provider-live-smoke.json](evidence/provider-live-smoke.json).

[Workflow receipts](evidence/multi-provider-workflow-20261030T220759Z.json):

- Fresh isolated home under `artifacts/local/`; no user skill library or cloud
  sync used. Synthetic development inputs 701 then 702, no sealed final feedback.
- Joey selected `deepseek-flash`, logical/native high. One Decisions POST plus
  one generation POST, 2,706 input / 4,505 output tokens, about 25.31 s combined
  POST latency. No repair or fallback was needed.
- Independently verified task plus four development checks; skill v1 admitted.
- Fresh-input reuse independently verified with **0 model calls / 0 tokens**.
- Usage receipts complete. Full dollar cost is **not reported** by direct
  DeepSeek/MiMo; a zero accumulator is not a claim of free inference.

These are small development checks, not comparative model quality, cost, or
latency benchmarks. MiMo and Luna have not been workflow-quality validated.
Span failure fallback remains covered by mocks and separate historical live
calibration; this run did not force a primary outage. No automatic substitution.

Reproduce only when live provider use is intended:

```powershell
.venv/Scripts/python.exe -I scripts/validate-multi-provider.py smoke --live
.venv/Scripts/python.exe -I scripts/validate-multi-provider.py workflow --live
.venv/Scripts/python.exe -I scripts/validate-multi-provider.py luna-reasoning --live
```

Each writes a timestamped evidence file and returns nonzero on a failed check.
No credentials or private reasoning are exported.

## Implementation

- `providers.MultiProvider` is wired into desktop worker startup. Fixed-policy
  experiments retain the old free adapter, model/settings and oracle. Covered skill
  reuse makes no decision or generation calls.
- Adaptive synthesis sends a compact task/contract/evidence-count/context summary
  and credential-eligible allowlisted candidates to **baseten/joey-1** through
  `POST https://openrouter.ai/api/alpha/decisions`, not Chat Completions.
  Typed `choice` answers select a model and logical effort; typed `score` is only
  diagnostic. All answers, optional probabilities and IDs are validated.
- Only primary routing failure invokes **respan/span-01-lite:free** using its
  existing binary `noul` question interface. The highest supported binary scores
  select offered candidates/efforts; scores remain independent binary probabilities,
  not invented categorical probabilities. Both routing failures stop generation.
- No automatic generator substitution. Authentication/rate-limit/capacity/invalid
  results remain explicit errors. No retry loop: each routing stage makes at most
  one request. Global cap 7 calls; Decisions cap 4; generation cap 3, with existing
  bounded repair/admission loop. All attempted POSTs are charged to model_calls,
  distinguished as decision/execution, with latency and usage receipts. Unknown
  error-attempt usage cannot become complete because a later request succeeds.
- Model registry and output budgets are in `config/runtime.json`.
  - DeepSeek `deepseek-flash`: direct `/chat/completions`; logical minimal→low,
    medium/xhigh→high; native none/low/high/max. Explicit consistent thinking toggle.
  - MiMo `mimo-v2.6-flash`, `mimo-v2.6-pro`: native thinking enabled/disabled only;
    any logical non-none effort is recorded as enabled, never as different native
    intensities. `max_completion_tokens` is used.
  - OpenRouter `openai/gpt-6-luna`: `reasoning.effort`; current catalog must confirm
    the chosen effort and endpoints must support reasoning/token budget before
    generation. Missing/null metadata fails closed, not silent suppression.
- Budgets: 4096 output tokens thinking off, 16384 enabled, 32768 max (configurable).
  Streaming consumes empty visible deltas while reasoning runs and reads final
  answer separately; private reasoning content is discarded, not logged/exported.
  Incomplete/truncated/error/tool-call streams cannot be admitted as plans.
- **Tool-turn boundary:** this product still synthesizes a bounded relational DSL,
  executes it with trusted operations, and independently verifies it. It does not
  run generated tool calls or chain raw provider tool turns. Thus provider-specific
  reasoning-history replay is not invoked; arbitrary tool calls are rejected rather
  than claiming unsupported tool-turn orchestration. Adding that would require a
  separate execution-boundary design. See `model-provider-contracts.md` for the
  providers' actual reasoning-history requirements.
- Requested logical effort, submitted native config, returned model/provider and
  effective effort are separate evidence fields. Effective effort remains unknown
  unless explicitly confirmed. Decision probabilities never override verification.
  No validated latency/cost/quality rankings exist for these newly registered models;
  candidate descriptions are intentions and the router receives this limitation.

## Credentials

Actual edit target: `%LOCALAPPDATA%\EvoFlow\provider-keys.env`.
Example: repository `.env.example` (no secrets). `.gitignore` also excludes
`provider-keys.env` copied anywhere into the checkout.

```dotenv
DEEPSEEK_API_KEY=
MIMO_API_KEY=
MIMO_BASE_URL=https://api.xiaomimimo.com/v1
OPENROUTER_API_KEY=
```

The bounded literal parser reads the file at worker startup, validates all entries
before import, copies nonempty keys into Windows current-user DPAPI slots, and
returns only the MiMo endpoint. Empty assignments preserve existing DPAPI values;
no saved key is copied into the edit file. One OpenRouter slot is shared by Joey,
Luna and Span. The file remains unchanged so Ctrl+S works normally; it is a local
plaintext editing source and should never be shared or synchronized deliberately.
MiMo base URL is restricted to exact Singapore Token Plan or standard HTTPS URL;
choose the endpoint matching your credential subscription. No endpoint/key guessing.

The earlier API-key-entry pause was explicitly resumed. Live verification above
used the saved protected keys; no keys were requested in chat. Endpoint selection
must still match the user's account; never guess a different endpoint silently.
See the current final QA for delivery/native verification status.

## Offline verification

- Full suite: 129 passed before final formatting/build checks.
- Typed Joey 404→Span fallback→mocked synthesis→actual verifier/admission→fresh
  input reuse passes end-to-end in temporary test state. Second task makes zero
  HTTP requests. Auth/429/503/invalid answers, no private reasoning in evidence,
  mappings, streaming, budget and cancellation tests all use MockTransport/fake keys.
- Official SDK schema checked: Decisions returns `answers`, `model`, `usage`;
  `noul` answer has numeric `noul`; `choice` has label and optional confidence /
  probabilities; `score` numeric score. SDK source:
  https://github.com/OpenRouterTeam/python-sdk/tree/main/src/openrouter/components
- Joey public model webpage had returned 404 at the documentation checkpoint;
  live Decisions API acceptance is now verified above. Public page availability
  is distinct from API entitlement. GPT generation checks the model catalog as
  well as endpoint metadata; the live low-effort smoke passed these checks.

No comparative reliability improvements or model-ranking accuracy are claimed.
No push, cloud deployment, service purchase or submission was performed.
