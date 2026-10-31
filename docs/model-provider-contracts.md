# Direct-provider contracts

Checked 2026-10-31 against public official documentation only. No provider keys,
authenticated provider API requests, live inference, or implementation edits.
Examples below are illustrative schema shapes, not captured API receipts.
OpenRouter Decisions is outside this reference's scope.

## DeepSeek: `deepseek-flash`

Sources: [Chat Completions reference][ds-api], [Thinking Mode][ds-thinking],
[first call][ds-start].

- POST `https://api.deepseek.com/chat/completions`.
- Authentication: Bearer API key; no key was accessed for this research.
- `thinking.type`: `enabled` or `disabled`; default enabled.
- `reasoning_effort`: `none`, `low`, `high`, `max`. `none` disables thinking;
  the other three enable it. Default effort is `high`.
- Compatibility aliases: `minimal` maps to `low`; `medium`/`xhigh` map to `high`.
- Conflict precedence between contradictory `thinking` and `reasoning_effort`
  settings was **not independently verified**. Use consistent pairs or one
  control rather than relying on precedence.
- `max_tokens`: 1–393216; defaults 8K non-thinking, 64K thinking, 128K at `max`.
- Thinking ignores `temperature`. Thinking supports tools, but rejects
  `tool_choice: "required"` and forced named functions with HTTP 400.

Illustrative enabled request (vary effort among low/high/max):

```json
{
  "model": "deepseek-flash",
  "messages": [{"role": "user", "content": "Explain the supplied task."}],
  "thinking": {"type": "enabled"},
  "reasoning_effort": "high",
  "max_tokens": 2048,
  "stream": true
}
```

Disabled variant: `"thinking":{"type":"disabled"}` with
`"reasoning_effort":"none"`. Both controls are documented independently.

Response paths:

- Non-streaming: `choices[].message.content`, optional
  `choices[].message.reasoning_content`, `choices[].message.tool_calls`.
- Streaming: `choices[].delta.content`, `choices[].delta.reasoning_content`,
  `choices[].delta.tool_calls`. Assemble tool calls by their `index`, appending
  argument fragments; validate arguments before execution.
- Tool calls: `{id,type:"function",function:{name,arguments}}`, where
  `arguments` is JSON encoded as a string.
- `usage`: `prompt_tokens`, `completion_tokens`, `total_tokens`,
  `prompt_cache_hit_tokens`, `prompt_cache_miss_tokens`,
  `prompt_tokens_details.cached_tokens`, optional
  `completion_tokens_details.reasoning_tokens`.
- Stream ends with `data: [DONE]`. Final chunk includes usage regardless of
  `stream_options.include_usage`; no separate usage-only chunk. The option
  controls whether earlier chunks include `usage:null` or omit it.
- No response-side effective-effort field is documented. Reasoning token counts
  do not prove an effort setting was honored.

**Tool history rule:** for requests carrying `tools`, preserve the full
`reasoning_content` of all preceding assistant turns in subsequent requests,
including turns without a tool call; missing content causes HTTP 400. Without
`tools`, returned reasoning need not be passed back and is ignored if supplied.
The official sample appends the complete assistant message.

## MiMo: `mimo-v2.6-flash` / `mimo-v2.6-pro`

Sources: [Chat Completions reference][mi-api], [Deep Thinking][mi-thinking],
[API integration FAQ][mi-faq], [Token Plan quick access][mi-plan].

### Endpoint and credential separation

| Route | OpenAI-compatible base | Request suffix |
|---|---|---|
| Standard pay-as-you-go | `https://api.xiaomimimo.com/v1` | `/chat/completions` |
| Singapore Token Plan | `https://token-plan-sgp.xiaomimimo.com/v1` | `/chat/completions` |

The Singapore base is explicitly printed in quick-access docs, not guessed.
The account's Token Plan page is authoritative for the assigned base URL.

- Standard keys: `sk-…`; Token Plan individual: `tp-…`; team: `ttp-…`.
  These credentials are independent and cannot be interchanged. This research
  does not establish the user's actual plan, entitlement, or key validity.
- Docs support `api-key` or Bearer authentication. Examples use `api-key`.
- Anthropic-compatible standard base:
  `https://api.xiaomimimo.com/anthropic`; Singapore Token Plan base:
  `https://token-plan-sgp.xiaomimimo.com/anthropic`. Append `/v1/messages`.
  Anthropic requests/responses have a separate schema, not Chat Completions JSON.

### Thinking request

- Both requested model IDs are listed by the API reference (alongside
  `mimo-v2.6-pro-ultraspeed`).
- `thinking.type`: `enabled` or `disabled`, default enabled for both.
- **No `reasoning_effort` parameter or none/low/high/max effort levels are
  documented** in the Chat Completions reference or Deep Thinking guide. Do not
  infer support from OpenAI compatibility or send effort levels as verified.
- `max_completion_tokens`: default and maximum 131072, minimum 1; includes
  reasoning plus final answer tokens.
- Thinking forces `temperature=1.0` and `top_p=0.95` even if other values are sent.
- `tool_choice` documents only `auto`; other values are removed by the backend,
  leaving auto behavior. Do not assume forced tool selection works.

Illustrative request to the selected base plus `/chat/completions`:

```json
{
  "model": "mimo-v2.6-flash",
  "messages": [{"role": "user", "content": "Explain the supplied task."}],
  "thinking": {"type": "enabled"},
  "max_completion_tokens": 2048,
  "stream": true
}
```

Change `thinking.type` to `disabled` for off; use `mimo-v2.6-pro` for Pro. With
OpenAI Python SDK, the nonstandard thinking field goes inside `extra_body`.

### Response, streaming, and tool continuation

- Non-streaming: `choices[].message.{role,content,reasoning_content,tool_calls}`.
- Streaming: `choices[].delta.{role,content,reasoning_content,tool_calls}`;
  reasoning fragments arrive first, followed by answer content. Tool fragments
  use `index` and JSON-string `function.arguments`.
- Disabled guide example omits `reasoning_content` and reports zero reasoning
  tokens. Do not require reasoning text on disabled responses.
- Usage: `prompt_tokens`, `completion_tokens`, `total_tokens`,
  `completion_tokens_details.reasoning_tokens`, and
  `prompt_tokens_details.cached_tokens` (also modality-specific counts).
- Finish reasons: `stop`, `length`, `tool_calls`, `content_filter`,
  `repetition_truncation`.
- No effective-effort response field is documented.

**Tool history rule:** in thinking-mode multi-turn conversations with historical
calls, pass back the full `reasoning_content` on assistant tool-call messages
in subsequent rounds or receive HTTP 400. The FAQ more broadly recommends
retaining all earlier assistant reasoning. Preserve complete assistant messages
rather than rebuilding them from visible text. Unlike DeepSeek's explicit rule,
MiMo's mandatory wording specifically refers to assistant turns containing calls.

Continuation message shape for either OpenAI-compatible provider (placeholders,
not real reasoning or a receipt):

```json
[
  {
    "role": "assistant",
    "content": null,
    "reasoning_content": "<unmodified returned field>",
    "tool_calls": [{"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{}"}}]
  },
  {"role":"tool","tool_call_id":"call_1","content":"<tool result>"}
]
```

MiMo's FAQ warns that malformed tool calls appearing inside reasoning can occur
with thinking enabled and recommends disabling thinking for more stable tool use.
That warning does not mean tool calling with thinking is unsupported.

Anthropic variant: `thinking.type` remains enabled/disabled; reasoning and calls
use `thinking`/`tool_use` content blocks. Streaming uses `thinking_delta` and
`input_json_delta`; preserve prior blocks. See [Anthropic reference][mi-anthropic].

## Verification boundaries

User-supplied hypotheses about DeepSeek effort values and MiMo Singapore Token
Plan routing are corroborated above by official references. The user's plan,
credentials, runtime behavior, and actual honored settings remain unverified.
MiMo effort-level support was not established; absence from these references is
not a live-server rejection test. No inference call is necessary or authorized
for this documentation-only task. Preserve protocol reasoning internally where
required, but do not expose hidden reasoning in EvoFlow UI/evidence exports.

[ds-api]: https://api-docs.deepseek.com/api/create-chat-completion
[ds-thinking]: https://api-docs.deepseek.com/guides/thinking_mode
[ds-start]: https://api-docs.deepseek.com/quick_start/first_api_call
[mi-api]: https://mimo.mi.com/docs/en-US/api/chat/openai-api
[mi-thinking]: https://mimo.mi.com/docs/en-US/quick-start/usage-guide/text-generation/deep-thinking
[mi-faq]: https://mimo.mi.com/docs/en-US/quick-start/faq/api-integration
[mi-plan]: https://mimo.mi.com/docs/en-US/tokenplan/Token%20Plan/quick-access
[mi-anthropic]: https://mimo.mi.com/docs/en-US/api/chat/anthropic-api
