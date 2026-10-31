# EvoFlow development

Read `docs/brief/AI-Workflow-Hackathon-Build-Brief.md` and the relevant installed
Microsoft/LangChain/Supabase guidance before changing the architecture.

- Keep the product UI native WinUI; the worker stays on authenticated loopback.
- `.agents/skills` contains build-time instructions, never learned product skills.
- Product skills are model-authored executable plans with independent checks,
  hashes, versioned contracts and auditable promotion. Never replace the oracle
  or seed handwritten procedures and call them learned.
- Trusted local capabilities use the versioned native ToolHost contract and maintained
  bundled document handlers. No arbitrary generated Python, shell scripts or unrestricted
  SQL execution. Learned plans cannot bypass native policy, approval or independent checks.
- Preserve fixture partitions, acquisition accounting, sealed final feedback
  separation and honest usage/cost reporting. Do not claim GPU or dollar savings.
- Supabase connects automatically using a protected device session. Do not add
  an email/password requirement or embed privileged cloud credentials.
- Use `uv sync --frozen`, then `.venv/Scripts/python.exe -m pytest -q` and ruff.
- Build native with `dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64`.
  Use WinApp CLI for native UI verification and delivery.
- No push, deployment, service purchase or hackathon submission without explicit
  authorization. Preserve unrelated files and shared/junction-backed skills.
