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
accessibility and lightweight CSS animations. By default, it edits only frontend
source code. Raster image generation is an **explicit, optional paid task setting**,
disabled unless the task creator checks "Разрешить генерацию изображений".

The role is stored on each task in PostgreSQL and exposed through the task API.
Older clients and tasks default to Developer. API callers can specify
`"role":"designer"` (or `"role":"developer"`) in POST /api/tasks. Designer uses
the same Codex model as Developer; this changes the role instructions, not the model.
No visual screenshot-based verification is integrated yet, so the user should
inspect the Draft PR's frontend changes before merging.

## Automatic team assignment and token tracking

New tasks are submitted with `role: auto` (also the default if the API caller
omits `role`). The platform chooses who participates without a separate paid
routing request: UI/design requests go to **Designer**, coding/backend requests
go to **Developer**, and mixed UI-plus-backend requests invoke Designer then
Developer sequentially inside one task workspace. Unclear requests default to
Developer. This initial routing is keyword-based and can misclassify ambiguous
phrasing; explicit `role: developer` or `role: designer` remains supported for
older API clients.

The independent Reviewer and tests still run after coding, and Draft PR creation
remains opt-in. Tasks expose `Participants` (the agents that were scheduled and
Reviewer when its check runs). The dashboard displays actual Developer, Designer,
and Reviewer token usage separately, including cached input and output tokens.
Developer/Designer repair attempts are attributed to the agent that ran them.
Analytics includes all three roles. Previously recorded Designer-only tasks are
migrated from the old Developer token columns exactly once, while old tasks with
missing usage remain marked as unknown.

Image generation stays **disabled by default**. Designer implements UI directly
in code whenever CSS/SVG can satisfy the task. No extra model call is made solely
to assign agents. Mixed tasks may cost more because two coding turns are
executed; the existing automatic fix/review flow is shared, not duplicated.

## Repository context and explicit Reviewer repairs

Before running Codex, the worker derives a bounded, sanitized **source-file map**
from Git's file list. This is local processing (no extra model call), helps
identify relevant modules, and does not restrict which files the agent can read.
For tasks involving both Designer and Developer, Developer additionally receives
a short (at most 2,500-character) handoff from Designer's final message. These
hints can reduce redundant exploration but are not guaranteed to lower costs;
the agents still verify source files and use the same configured models.

After a successful Reviewer pass, `HasReviewFindings` distinguishes actionable
findings from an explicit "No actionable findings". The dashboard highlights
findings and offers **Исправить замечания** when the task was completed and its
existing PR is available. The button calls `POST /api/tasks/{id}/fix-review`.
This explicitly opts into a **paid** Codex correction attempt. The worker
atomically claims the queued task, verifies that the original PR is still open,
reuses its saved workspace, implements the fix, reruns available tests and
independent review, then pushes an additional commit to the **same PR**.
It does not automatically repeat paid review corrections. There can be at most
three user-triggered repair attempts per task. Tokens accumulate under the actual
repair agent and Reviewer; original task metrics are not reset. The original
`FinishedAt` remains unchanged for analytics. Historical completed review
reports are classified on startup and may show the button if their saved
workspace and open PR still exist. Failed review runs do not count as findings.

If review has cleared all findings but updating the existing PR failed, the
dashboard offers **Повторить публикацию исправлений**: it retries publication
without another Codex call. All corrections require an intact `agent_workspaces`
volume and an open GitHub PR. Closed or merged PRs are rejected, and the worker
never pushes changes directly to `main`. No change to the model or automatic
image generation is included.

## Optional low-cost image drafts, visual review, and final assets

The task composer includes **Разрешить генерацию изображений** (unchecked by
default) and an approximate image budget, initially **$0.35**. Enabling it
explicitly requests paid image work and requires a Draft PR. If not enabled,
the platform makes **zero image generation or image evaluator API requests**.

When opted in, Designer is included in the execution team. Designer normally
chooses CSS/SVG or existing assets; it creates a root-level
`developer-team.assets.json` **only if a raster asset is necessary**:

```json
{
  "assets": [{
    "path": "public/assets/garage.png",
    "prompt": "Detailed description of a single visual scene including composition, style and exclusions.",
    "criteria": ["Important subject visible", "Correct game art perspective"],
    "size": "1024x1024",
    "transparent": false
  }]
}
```

The manifest's `criteria` list can contain more than six checks or lengthy
descriptions. The worker trims and deduplicates them, accepts a single string
or descriptive objects, and supplies two general composition checks when
none are specified. The complete manifest is still capped at 16 KB, and
unsafe image paths, overly long prompts, unsupported sizes, and impossible
multi-car sprite atlases are rejected before paid requests. Invalid manifests
mark the task as an image-stage failure, so old failed tasks can be resumed
with **Повторить генерацию (платно)** without running Designer again.

Supported paths begin with `public/assets/`, `wwwroot/assets/`, or
`assets/` and end in `.png`. Paths are validated against traversal,
symlinks, secret-like names, and preexisting files. One raster asset per
task, at most two draft attempts, and one final attempt. The temporary
manifest is removed before Git publication.

The worker uses `gpt-image-2` for opaque assets and switches **transparent
PNG assets to `gpt-image-1.5`** for both the low-quality draft and high-quality
final. GPT Image 2 transparency is still unavailable in some API projects,
where it returns HTTP 400 `invalid_value`. GPT Image 1.5 is an older model
that supports PNG transparency. An independent `gpt-4.1-mini` vision evaluation
compares the actual rendered draft with the brief and checklist, requiring
an explicit pass and **composition score of at least 0.75**. The evaluator
uses a strict JSON schema to avoid confusing malformed answers with bad artwork.
It checks only **core subject, angle, framing and forbidden elements**, not the
draft's lack of photorealism, polish or fine details. It examines the reference
image at high vision detail, but the generated draft itself remains low-cost.
Each attempt's findings and score are retained for inspection in the task UI,
and failure messages show the concrete reasons. Rejected drafts are regenerated
only at low generation quality. If both fail, the task fails safely
without making any high-quality image request.

A passing draft becomes a base64 data-URL **reference image** for a final
Images Edit API call with `quality=high`, using the same image model selected
for the asset's background type. The final PNG
is written to the declared asset path in the repository and is included
in the usual Draft PR. Designer and Developer can reference its path;
the independent code Reviewer still runs afterward. This approach
**does not guarantee pixel-perfect consistency** between draft and final,
and final image quality still needs human inspection.

Images are stored in the existing task workspace Docker volume, not in
PostgreSQL. Draft images are kept under `.git/developer-team-image-drafts/`
so they are available for preview but not committed. The dashboard shows
the current image stage, generation attempts, vision assessment, preview
images, image/evaluator token counts and **conservative USD reservations**.
API routes: `GET /api/tasks/{id}/images` and
`GET /api/tasks/{id}/images/{assetId}/draft|final`.

For sprite-heavy games, Designer now declares **one PNG per vehicle**
rather than cramming the fleet into a single sprite atlas. A task may contain
**up to six distinct PNG assets**, each independently generated and evaluated
with the same cheap-draft → high-quality-final gate. The platform checks that
the budget can cover all requested assets *before* the first paid draft.
The default $0.35 budget is appropriate for one transparent car; for two
transparent square PNGs, the conservative minimum reservation is $0.414;
increase the budget deliberately for more cars (task maximum $2.00).

Multi-car sprite sheets, rigid pixel coordinates (for example wheel position
`y=390`), cell grids and atlases are rejected during manifest parsing for
car imagery **without any paid image calls**. The model generates **one recognizable
side-view car at a time**. Browser layout code handles cell placement, margins,
horizontal mirroring where appropriate, and wheel alignment. On manual retry,
already completed car PNGs are reused and not regenerated.

**Retry without another coding turn:** if image generation fails before
tests/review/PR publication (including HTTP 400 for an unsupported background),
the task card offers **Повторить генерацию (платно)**. This atomically queues a
new image generation attempt in the **same saved workspace**, preserving
Designer/Developer code and previously counted text tokens. The original image
brief is retained on new tasks, or reconstructed from stored `GeneratedAssets`
metadata for older failed tasks where it was removed. The retry runs new image
and vision calls, so it is **not free**; existing budget reservations carry over.
Once generation succeeds, the worker continues normal tests, code review and
Draft PR publication. The endpoint is
`POST /api/tasks/{id}/retry-images`. Do not delete the `agent_workspaces`
Docker volume before retrying.

**Cost controls and limitations:** the worker reserves estimated spending
before every image or vision call and blocks calls that would exceed the
task's specified budget. Before generating a draft, it also verifies that
there is enough remaining budget for the draft **plus** vision assessment
**plus** a possible high-quality final, so a futile paid draft is never started.
Defaults use $0.015 per draft, $0.012 per vision evaluation, and high-quality
final reserves of $0.180 (transparent square, GPT Image 1.5), $0.260
(transparent portrait/landscape), $0.270 (opaque square, GPT Image 2),
or $0.230 (opaque portrait/landscape). These are **budget allowances, not measured OpenAI charges**;
the actual invoice depends on model token usage and the provider's current
pricing, and failed requests may still incur charges. The default maximum
does not apply to the existing Codex text/reviewer usage. Model IDs are
intentionally fixed to the priced, tested configuration; unsupported
`Images:*Model` overrides fail before image calls. Images may also require
API organization verification. For a first test, enable images for **one
small, noncritical asset** in a trusted test repository.

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
