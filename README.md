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
Settings are automatically validated at startup, before any database access. Invalid settings stop startup with a list of parameter names and errors, without printing their values. `APP_ROLE` must be `api` (the default) or `worker`; `ConnectionStrings:Default` must be a PostgreSQL connection string with a host. When specified, `Worker:WorkspaceRoot` must be a nonempty valid path and `Worker:TaskTimeoutMinutes` must be an integer from 1 to 71582 (default: 20). The worker also requires nonempty `OpenAI:ApiKey` and `OpenAI:Model`; an explicit `OpenAI:ReviewerModel` must be nonempty. `GitHub:Token` remains optional and is required only when publishing a PR.

PostgreSQL data location: named Docker volume `postgres_data`, mounted under `/var/lib/postgresql/data`, PGDATA=`/var/lib/postgresql/data/pgdata`. Database name: `developer_team`. Initial password `developer-team-local-only` is **only for local trusted development**; change it in both Docker Compose and appsettings before any shared deployment. Never expose the API or database publicly.

The worker uses the built-in OpenAI provider and `https://api.openai.com/v1`. Model `gpt-6.1-sol` is an example; verify that your API account can access it, otherwise change `OpenAI:Model`. Billing uses your OpenAI Platform API credits, not ChatGPT subscription usage. Restart the worker after editing the local JSON file.

## Developer and Designer roles

The task form lets you choose between **Developer** (default) and **Designer**.
Both use the existing configured Codex model, sandboxed workspace, automatic
.NET tests when applicable, independent Reviewer, and optional GitHub Draft PR.
Designer specializes in UI/UX, component composition, styling, responsive layout,
accessibility and lightweight CSS animations. It edits frontend source code, not
image files. Image generation is **not** connected: choosing Designer makes no
image-generation API calls or image charges.

The role is stored on each task in PostgreSQL and exposed through the task API.
Older clients and tasks default to Developer. API callers can specify
`"role":"designer"` (or `"role":"developer"`) in POST /api/tasks. Designer uses
the same Codex model as Developer; this changes the role instructions, not the model.
No visual screenshot-based verification is integrated yet, so the user should
inspect the Draft PR's frontend changes before merging.

## Automated tests and independent Reviewer

After Codex edits the repository, the worker executes a bounded `dotnet test` run
when it detects a .NET test project. A root `.sln` or `.slnx` is used when available;
otherwise, each detected test project runs separately. For other stacks or absent
test projects, the result is `not_configured`; **no green test result is claimed**.
Timeout: 6 minutes per test run.

On failure, Developer receives the original task, failing test output and current
changed-file diff, and fixes the same workspace and branch. Tests run again after
each fix, including a failed or timed-out Developer invocation. `Worker:MaxFixAttempts`
sets the maximum number of fixes (default **2**, nonnegative integer; **0** disables
fixes). Configure it through worker configuration or `Worker__MaxFixAttempts`.
Passed or unconfigured tests stop the loop immediately. The independent Reviewer
runs once after the loop, then optional Draft PR publication runs once.

`TestLog` records attempt 0 (initial tests), each fix number, failure reason,
Developer corrections and subsequent test result. The final `TestStatus` remains
`failed` when fixes are exhausted; the dashboard, checks API and PR report explicitly
show it even when the task itself is `completed`. The original diff is saved before
fixes in `InitialDiff` (`GET /api/tasks/{id}/initial-diff`); `/diff` contains the final
changes. Existing tasks may have no initial diff. Publication retry reuses the
saved workspace without rerunning Developer or the fix loop.

A second, read-only `codex exec` run independently reviews the diff relative to HEAD,
looking for bugs, security problems, regressions and missing validation/tests.
Its output and test results are stored in PostgreSQL and accessible via
`GET /api/tasks/{id}/checks` and the dashboard's **Проверки** button.
The worker adds an informational comment to any newly created Draft PR.
Findings do not auto-merge or silently reject the PR; a human still decides.
The Reviewer uses `OpenAI:ReviewerModel` when specified; otherwise `OpenAI:Model`.
This second model invocation **consumes additional API tokens**.

The worker image now includes the .NET 9 SDK to run real tests; it is heavier.
For the unprivileged container user, .NET CLI home and NuGet packages live in
writable /tmp/dotnet-home and /tmp/nuget-packages. This prevents failures caused by
.NET trying to create /.dotnet. The Reviewer output is reduced to its final agent
message instead of exposing raw Codex JSON lifecycle events.
Tests execute in the same worker container as the coding agent; run **trusted repositories only**.
Future improvements should use disposable sandboxed runners with credentials isolated.

## Persistent Reviewer knowledge and token accounting

Reviewer uses the same configured Codex model as before (`OpenAI:ReviewerModel`,
falling back to `OpenAI:Model`). No model downgrade or automatic price assumptions.

On each task, it receives a bounded (4,000-character) summary of repository
architecture, invariants and recurring review concerns. It returns both its findings
and an updated summary in its final answer. The worker stores the updated notes
in the PostgreSQL `ReviewerMemories` table, keyed by normalized GitHub
repository URL. Notes are separate from the Git workspaces, so they survive
worker rebuilds and are never committed into a PR. Existing notes are considered
untrusted hints and must be verified against the current code. A failed review
or legacy unstructured response does not overwrite memory. Memory begins empty;
previous tasks cannot be retroactively summarized without rerunning an agent.
Inspect notes from the dashboard's **Память Reviewer** button or
`GET /api/tasks/reviewer-memory?repositoryUrl=https://github.com/OWNER/REPO`.

Codex usage events are now counted per task as Developer/Reviewer input, cached
input, and output tokens (including Developer repair attempts). These counts
appear in the dashboard; existing tasks show zero because historic usage was
not persisted. Token totals are not USD cost: actual billing depends on your
model pricing and cache rates. Providing memory adds a small amount of context
per review, so **reduced cost is not guaranteed**.

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
