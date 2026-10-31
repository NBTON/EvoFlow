# EvoFlow continuation

Read and follow `AGENTS.md`, the complete build brief, and
`docs/CONTINUE-HERE-2026-10-30.md` before continuing. Preserve existing untracked
implementation and user state. This is a continuation, not a new scaffold.

The user authorizes parallel scoped subagents; coordinate exclusive UI input.
Read the relevant installed skills at the paths in the continuation packet even
if they are not exposed as Claude slash commands. Native UI testing uses the
installed WinApp CLI; Codex-only tool APIs are not necessary for those checks.

Claude's project-local Supabase HTTP MCP server is configured in the user's
private `.claude.json`, restricted to project `csvfsltznjdaalnzvgbz`, matching
the Codex server feature groups. At handoff it needs Claude-specific OAuth:
`claude mcp login supabase` (or `/mcp` inside Claude). Verify with get_project_url
or list_tables afterward. Do not copy Codex OAuth tokens or put credentials in
source. This developer-tool authorization is separate from EvoFlow's already
working protected device session; never add an app email/password prompt.

No push, deployment, purchases, or hackathon submission. Do not weaken native
WinUI, authenticated loopback, skill admission, fixture separation or accounting.
