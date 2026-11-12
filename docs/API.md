# API quickstart

Local API base URL: `http://localhost:5088`.

```http
GET /health
GET /api/tasks
GET /api/tasks/{id}
GET /api/tasks/{id}/log
GET /api/tasks/{id}/diff
POST /api/tasks
Content-Type: application/json

{
  "repositoryUrl": "https://github.com/Skeedon/developer_team",
  "prompt": "Add a unit test project for the domain layer."
}
```

Task statuses: `queued`, `running`, `completed`, `failed`.

`completed` means Codex exited successfully, **not** that code quality or tests were independently verified.

There is no public authentication: keep the API loopback-only and never publish port 5088. Workspaces are in the Docker volume, not committed or pushed automatically. `/diff` includes tracked-file changes only; untracked files must be inspected in the workspace.

Future iterations will add independent QA, artifact bundles, explicit push/PR approval, and hardened task isolation.

`GET /api/tasks/{id}/initial-diff` returns the saved diff before automatic fixes (plain text; empty for older tasks). `/diff` returns the final diff. `/checks` includes the full attempt history in `testLog` and the final `testStatus`, including `failed` when the fix limit is exhausted.
