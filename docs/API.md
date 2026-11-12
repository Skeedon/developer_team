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

### GET /api/analytics?period=7|30|all

Аналитика задач; по умолчанию последние 7 дней. Некорректный период возвращает 400.
Период применяется к `CreatedAt` в UTC: последние 7/30 × 24 часа либо всё время.
Ответ содержит `totals`, `daily` (по дате создания UTC) и `recent` (до 10 задач).
Агрегации выполняются PostgreSQL; запросы читаются в одной repeatable-read транзакции.

- `total`: все статусы; `successful`: completed; `failed`: failed и publish_failed.
- `averageDurationSeconds`: среднее FinishedAt − StartedAt для завершённых интервалов без отрицательной длительности; ожидание очереди не включено.
- `developerInput`, `developerCachedInput`, `developerOutput` и аналогичные поля Reviewer: суммы известных метрик. Cached input входит в input и не суммируется с ним повторно.
- `developerMeasuredTasks`, `reviewerMeasuredTasks`, `fixMeasuredTasks`: покрытие метриками.
- `fixAttempts`: число начатых повторных попыток исправления тестов; повторная публикация PR не учитывается.
- `tasksHelpedByFixes`: задачи с попытками исправлений и итоговым TestStatus=passed, независимо от результата публикации.
- Отсутствующие метрики возвращаются как null («Нет данных»). Известные нулевые значения сохраняются нулями. Пустой период: total=0, неизвестные метрики=null, пустые daily/recent.

При обновлении схемы старые положительные счётчики токенов считаются известными.
Старые нулевые счётчики и число исправлений не восстанавливаются предположениями.
Модели, тарифы и существующие поля API не изменяются; API задач дополнен признаками наличия метрик.
Страница: `/analytics.html`.
