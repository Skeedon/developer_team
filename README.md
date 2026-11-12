# Developer Team

A local, Docker-based foundation for autonomous code development using ASP.NET Core, PostgreSQL, and Codex CLI.

## What works in v0.1

- REST endpoints to create and inspect development tasks.
- PostgreSQL persistence and a single polling background worker.
- Git clone of a **public** GitHub repository into a dedicated workspace for each task.
- Non-interactive Codex CLI execution with `workspace-write` sandbox.
- Captured execution log and Git diff, available through the API.
- No automatic push, PR, or merge. Review the changes locally first.

**This is an experimental single-user development tool, not a hardened multi-tenant service.** Run on a trusted machine; do not expose the API to the Internet. Tasks allow code execution, repositories are untrusted, and a model API key is available to the worker process.

## Start

Requirements: Docker Desktop, Docker Compose, an OpenAI API key with Codex-compatible access.

```bash
cp .env.example .env
# Set OPENAI_API_KEY in .env
docker compose up --build -d
curl http://localhost:5088/health
```

PowerShell:

```powershell
Copy-Item .env.example .env
docker compose up --build -d
Invoke-RestMethod http://localhost:5088/health
```

API documentation: `docs/API.md`.

Example task:

```bash
curl -X POST http://localhost:5088/api/tasks \
  -H "Content-Type: application/json" \
  -d '{"repositoryUrl":"https://github.com/Skeedon/developer_team","prompt":"Add a small unit test project for domain validation."}'
```

Inspect `GET /api/tasks/{id}`, `GET /api/tasks/{id}/log`, and `GET /api/tasks/{id}/diff`. Workspaces persist in Docker volume `agent_workspaces`; to inspect manually: `docker compose exec worker sh`.

## Architecture

```
Caller -> API -> PostgreSQL <- Worker
                           |
                           +-> git clone -> codex exec -> git diff -> database
```

The API is bound to loopback (`127.0.0.1:5088`). The worker does not receive the host Docker socket. The initial release executes one task at a time and does not use a broker. The worker uses the same application binary with a different `APP_ROLE`.

## Safety and limitations

- Only public `https://github.com/OWNER/REPO` URLs are accepted; no arbitrary Git URLs.
- Never mount your home directory, SSH agent, host Docker socket, or privileged credentials into the worker.
- Codex may run commands in the worker container; use a disposable environment and a scoped API key.
- Codex's own workspace sandbox is defense-in-depth, not a hard isolation boundary. For stronger isolation, the next iteration will launch a fresh restricted container per task with network controls and explicit resource limits.
- Logs and patches are capped in the API database to avoid unbounded storage. Long-running tasks are timed out.
- `EnsureCreated` is used for this initial schema; replace with EF Core migrations before evolving the schema.
- No authenticated users, cancellation endpoint, retry, image evaluation, real QA agent, or GitHub push yet.
- API key costs are incurred by Codex; monitor your provider usage.

## Roadmap

1. Task lifecycle / execution history / limits (this release).
2. Isolated ephemeral containers and deterministic build/test commands.
3. Reviewer agent and repair loop.
4. Playwright and screenshot-based visual assessment.
5. GitHub branch push + draft pull request with explicit user-controlled credentials.
6. Multi-agent orchestration and dashboard.
