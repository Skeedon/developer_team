# Developer Team

Local coding-agent platform on **ASP.NET Core 9, PostgreSQL 16, Docker and Codex CLI**. Uses [BetterToken](https://docs.bettertoken.ai/en) as an OpenAI-compatible Responses API provider.

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

Copy `src/DeveloperTeam/appsettings.Local.example.json` to `config/appsettings.Local.json` (create the `config` directory). Fill in `BetterToken:ApiKey`, and replace `BetterToken:Model` with the **exact model ID** available in your BetterToken account.

The worker binds `config/appsettings.Local.json` to `/app/appsettings.Local.json` as read-only. The key is NOT committed and is passed to Codex as `BETTERTOKEN_API_KEY` for the child process only.

```powershell
New-Item -ItemType Directory -Force config
Copy-Item src/DeveloperTeam/appsettings.Local.example.json config/appsettings.Local.json
# Edit config/appsettings.Local.json
docker compose up --build -d
Invoke-RestMethod http://localhost:5088/health
```

Database connection string: `src/DeveloperTeam/appsettings.json` → `ConnectionStrings:Default`.
PostgreSQL data location: named Docker volume `postgres_data`, mounted under `/var/lib/postgresql/data`, PGDATA=`/var/lib/postgresql/data/pgdata`. Database name: `developer_team`. Initial password `developer-team-local-only` is **only for local trusted development**; change it in both Docker Compose and appsettings before any shared deployment. Never expose the API or database publicly.

Model provider URL: `BetterToken:BaseUrl=https://bettertoken.ai/v1`. Codex uses the Responses protocol with a custom provider. Compatibility varies by model; test a small task first. This setup does not use the official OpenAI account subscription for billing.

## API

```http
GET /health
GET /api/tasks
GET /api/tasks/{id}
GET /api/tasks/{id}/log
GET /api/tasks/{id}/diff
POST /api/tasks
Content-Type: application/json

{"repositoryUrl":"https://github.com/Skeedon/developer_team","prompt":"Add a unit test project."}
```

Current limitations: single worker, public GitHub repositories only, no auth, no PR publishing, no reviewer agent, no deterministic build/test stage, no hardened per-task container. Each task gets a dedicated Git workspace. No automatic Git push. Run only locally on trusted infrastructure; the Codex process runs code in the worker container. Logs and diffs have size caps, tasks have a 20-minute timeout.

**Security:** do not put an API key in committed appsettings.json. Keep local secrets in ignored config/appsettings.Local.json. For production migrate to Docker secrets or a secret manager; do not use the development PostgreSQL password.
