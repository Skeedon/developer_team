# Developer Team

Local coding-agent platform on **ASP.NET Core 9, PostgreSQL 16, Docker and Codex CLI**. Uses the official OpenAI API for Codex CLI execution.

## Solution layout

- `DeveloperTeam.sln` — Visual Studio solution at the repository root
- `src/DeveloperTeam/Program.cs` — application composition root
- `src/DeveloperTeam/Domain` — task model and requests
- `src/DeveloperTeam/Data` — EF Core PostgreSQL context
- `src/DeveloperTeam/Endpoints` — minimal API mappings
- `src/DeveloperTeam/Workers` — execution loop and Codex runner
- `src/DeveloperTeam/Validation` — repository URL validation
- `src/DeveloperTeam/appsettings.json` — default application settings

## Configure

Copy `src/DeveloperTeam/appsettings.Local.example.json` to `config/appsettings.Local.json` (create the `config` directory). Fill in `OpenAI:ApiKey` (an OpenAI Platform API key), and set `OpenAI:Model` to a model available to your API project.

The worker binds `config/appsettings.Local.json` to `/app/appsettings.Local.json` as read-only. The key is NOT committed and is passed to the non-interactive Codex child process as `CODEX_API_KEY` only when the task starts.

```powershell
New-Item -ItemType Directory -Force config
Copy-Item src/DeveloperTeam/appsettings.Local.example.json config/appsettings.Local.json
# Edit config/appsettings.Local.json
docker compose up --build -d
Invoke-RestMethod http://localhost:5088/health
```

Database connection string: `src/DeveloperTeam/appsettings.json` → `ConnectionStrings:Default`.
PostgreSQL data location: named Docker volume `postgres_data`, mounted under `/var/lib/postgresql/data`, PGDATA=`/var/lib/postgresql/data/pgdata`. Database name: `developer_team`. Initial password `developer-team-local-only` is **only for local trusted development**; change it in both Docker Compose and appsettings before any shared deployment. Never expose the API or database publicly.

The worker uses the built-in OpenAI provider and `https://api.openai.com/v1`. Model `gpt-6.1-sol` is an example; verify that your API account can access it, otherwise change `OpenAI:Model`. Billing uses your OpenAI Platform API credits, not ChatGPT subscription usage. Restart the worker after editing the local JSON file.

## Automated tests and independent Reviewer

After Codex edits the repository, the worker executes a bounded `dotnet test` run
when it detects a .NET test project. A root `.sln` or `.slnx` is used when available;
otherwise, each detected test project runs separately. For other stacks or absent
test projects, the result is `not_configured`; **no green test result is claimed**.
Timeout: 6 minutes.

A second, read-only `codex exec` run independently reviews the diff relative to HEAD,
looking for bugs, security problems, regressions and missing validation/tests.
Its output and test results are stored in PostgreSQL and accessible via
`GET /api/tasks/{id}/checks` and the dashboard's **Проверки** button.
The worker adds an informational comment to any newly created Draft PR.
Findings do not auto-merge or silently reject the PR; a human still decides.
The Reviewer uses `OpenAI:ReviewerModel` when specified; otherwise `OpenAI:Model`.
This second model invocation **consumes additional API tokens**.

The worker image now includes the .NET 9 SDK to run real tests; it is heavier.
Tests execute in the same worker container as the coding agent; run **trusted repositories only**.
Future improvements should use disposable sandboxed runners with credentials isolated.

## GitHub draft PR publishing

Add `GitHub:Token` to ignored `config/appsettings.Local.json`. Create a fine-grained GitHub PAT restricted to your target repository with **Contents: Read and write** and **Pull requests: Read and write** permissions.

The dashboard lets you provide an optional source branch. The worker clones this branch and uses it as the base of the draft PR; leaving it empty uses the repository default branch. The dashboard lets you choose whether to create a draft PR. The worker commits changes in a new `agent/<task-id>` branch, pushes to the specified GitHub repository, and opens a **draft** PR against the selected source branch. The system does not merge PRs. A task with no changes will fail PR publication instead of creating an empty PR.

**Security:** This MVP runs Codex in the same container as the credential-bearing worker. An untrusted repository could attempt to access the local secret file. Use only repositories you control, restrict the token to those repositories, and keep the API localhost-only. Hardened separation is required before untrusted or multi-user workloads.

## Retry failed GitHub publication

When coding succeeded but a GitHub push or PR operation failed, the dashboard displays
**Повторить публикацию PR** on the failed task. It calls
`POST /api/tasks/{id}/retry-publish` and queues only the existing workspace's Git
commit, push, and PR creation. **Codex is not run again** and no new OpenAI tokens are spent.
This also works with earlier tasks marked `failed` when their diff and workspace were saved.
After a successful retry, status becomes `completed` and the PR URL appears in the dashboard.

For retries to work, keep the `agent_workspaces` Docker volume intact; do not run
`docker compose down -v`. Rebuild both containers after pulling updates:
`docker compose up -d --build --force-recreate api worker`.

## Local dashboard and downloads

After starting Docker Compose, open **http://localhost:5088/**. Submit tasks, monitor status (refreshes every 5 seconds), inspect logs and diffs, and download completed workspaces as ZIP without entering Docker. The API mounts the workspace Docker volume read-only. Downloads omit `.git`, node_modules, build folders and common secret files; capped at 50 MiB of source data / 1500 files. The dashboard is **not authenticated**, so leave port 5088 bound to localhost only.

Update from this branch and recreate both services after the change:

```powershell
docker compose up -d --build --force-recreate api worker
```

## API

```http
GET /health
GET /api/tasks
GET /api/tasks/{id}
GET /api/tasks/{id}/log
GET /api/tasks/{id}/diff
GET /api/tasks/{id}/download  # completed tasks, returns ZIP
POST /api/tasks
Content-Type: application/json

{"repositoryUrl":"https://github.com/Skeedon/developer_team","prompt":"Add a unit test project."}
```

Current limitations: single worker, public GitHub repositories only, no auth, no PR publishing, no reviewer agent, no deterministic build/test stage, no hardened per-task container. Each task gets a dedicated Git workspace. No automatic Git push. Run only locally on trusted infrastructure; the Codex process can execute repository code in the worker container, and child processes may access credentials. Do not feed untrusted repositories to this MVP; future isolated runners should use a credential proxy. Logs and diffs have size caps, tasks have a 20-minute timeout.

**Security:** do not put an API key in committed appsettings.json. Keep local secrets in ignored config/appsettings.Local.json. For production migrate to Docker secrets or a secret manager; do not use the development PostgreSQL password.
