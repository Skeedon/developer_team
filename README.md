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
