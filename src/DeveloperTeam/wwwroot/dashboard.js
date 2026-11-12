const root = document.querySelector('#tasks');
const message = document.querySelector('#message');
const details = document.querySelector('#details');
const content = document.querySelector('#details-content');
const imageDetails = document.querySelector('#image-details');
const escapeHtml = x => String(x ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
async function read(url) {
  const response = await fetch(url);
  if (!response.ok) throw Error(await response.text());
  return response;
}
async function showImages(id) {
  try {
    const assets = await (await read('/api/tasks/' + encodeURIComponent(id) + '/images')).json();
    content.hidden = true; imageDetails.hidden = false;
    imageDetails.innerHTML = assets.length ? assets.map(asset => {
      const url = '/api/tasks/' + encodeURIComponent(id) + '/images/' + encodeURIComponent(asset.id);
      const review = asset.review ? '<pre>' + escapeHtml(asset.review) + '</pre>' : '';
      return '<div class="image-asset"><h3>' + escapeHtml(asset.path) + '</h3>' +
        '<p>Статус: ' + escapeHtml(asset.status) + ' · Черновики: ' + Number(asset.draftAttempts) +
        ' · Финал: ' + Number(asset.finalAttempts) + ' · Оценка: ' +
        (asset.score == null ? '—' : Math.round(Number(asset.score) * 100) + '%') +
        ' · Резерв: USD ' + Number(asset.reservedUsd).toFixed(3) + '</p>' +
        '<div class="image-grid"><div><strong>Черновик</strong><img loading="lazy" src="' + url + '/draft" alt="Черновик изображения"></div>' +
        (asset.finalAttempts ? '<div><strong>Финал</strong><img loading="lazy" src="' + url + '/final" alt="Финальное изображение"></div>' : '') +
        '</div>' + review + (asset.error ? '<p class="failed">' + escapeHtml(asset.error) + '</p>' : '') + '</div>';
    }).join('') : '<p>Изображения ещё не созданы. Возможно, Designer использовал CSS/SVG.</p>';
    document.querySelector('#details-title').textContent = 'Изображения Designer';
    details.hidden = false; details.scrollIntoView({behavior:'auto'});
    document.querySelector('#details-title').focus({preventScroll:true});
  } catch (e) { message.textContent = 'Ошибка изображений: ' + e.message; }
}
async function showText(id, kind) {
  try {
    content.hidden = false; imageDetails.hidden = true;
    const response = await read('/api/tasks/' + encodeURIComponent(id) + '/' + kind);
    if (kind === 'checks') {
      const checks = await response.json();
      content.textContent = 'TESTS: ' + (checks.testStatus || 'pending') + '\n' +
        (checks.testLog || '') + '\n\nREVIEWER: ' + (checks.reviewStatus || 'pending') +
        '\n' + (checks.reviewLog || '');
    } else content.textContent = await response.text();
    document.querySelector('#details-title').textContent = kind === 'log' ? 'Лог агента' : kind === 'checks' ? 'Проверки и Reviewer' : 'Git diff';
    details.hidden = false;
    details.scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'});
    document.querySelector('#details-title').focus({preventScroll:true});
  } catch (e) { message.textContent = e.message; }
}
window.showTaskText = showText;
document.querySelector('#showMemory').addEventListener('click', async () => {
  try {
    const repo = document.querySelector('#repo').value;
    const response = await read('/api/tasks/reviewer-memory?repositoryUrl=' + encodeURIComponent(repo));
    const memory = await response.json();
    document.querySelector('#details-title').textContent = 'Память Reviewer · ' + repo;
    content.hidden = false; imageDetails.hidden = true;
    content.textContent = memory.notes || 'Пока пусто. Reviewer заполнит память после первого успешного ревью.';
    details.hidden = false;
    details.scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'});
    document.querySelector('#details-title').focus({preventScroll:true});
  } catch (e) { message.textContent = 'Ошибка памяти: ' + e.message; }
});
const statuses = {queued:'В очереди',running:'В работе',publishing:'Публикация PR',publish_queued:'PR в очереди',completed:'Завершено',failed:'Ошибка',publish_failed:'Ошибка публикации',review_fix_queued:'Исправление в очереди',review_fixing:'Исправление замечаний',review_fix_failed:'Ошибка исправления',image_retry_queued:'Повторная генерация в очереди'};
const checkStatuses = {pending:'Ожидание',passed:'Пройдены',failed:'Ошибка',reviewed:'Проверено',fixing:'Исправление замечаний',skipped:'Пропущено',not_run:'Не запускались',running:'В работе'};
// API integration contract: currentStage = assignment | development | testing | review | publication.
// The task status determines whether the current stage is running, paused or failed.
const workflowStages = [
  ['assignment', 'Назначение исполнителей', 'Назначение'],
  ['development', 'Разработка', 'Разработка'],
  ['testing', 'Тестирование и исправления', 'Тесты и правки'],
  ['review', 'Reviewer', 'Reviewer'],
  ['publication', 'Публикация PR', 'Публикация PR']
];
function taskWorkflow(task) {
  const completed = task.status === 'completed';
  const failed = ['failed', 'publish_failed', 'review_fix_failed'].includes(task.status);
  const publishing = ['publishing', 'publish_queued', 'publish_failed'].includes(task.status);
  let stage = workflowStages.findIndex(([key]) => key === task.currentStage);
  if (completed) stage = task.createPullRequest || task.pullRequestUrl ? 4 : 3;
  else if (publishing) stage = 4;
  else if (stage < 0 && task.status === 'queued') stage = 0;
  else if (stage < 0 && task.reviewStatus === 'running') stage = 3;
  else if (stage < 0 && task.testStatus === 'running') stage = 2;
  // Historical running/failed tasks have no reliable stage. Do not invent one.
  const unknown = stage < 0;
  const states = workflowStages.map((_, index) => {
    if (index === 4 && !task.createPullRequest && !task.pullRequestUrl && !publishing) return 'skipped';
    if (unknown) return 'pending';
    if (completed || index < stage) return 'done';
    if (index > stage) return 'pending';
    return failed ? 'error' : ['queued', 'publish_queued', 'review_fix_queued', 'image_retry_queued'].includes(task.status) ? 'queued' : 'active';
  });
  return {stage, states, unknown, completed, failed};
}
function renderWorkflow(task) {
  const flow = taskWorkflow(task);
  const stateLabels = {done:'Завершён', pending:'Ожидает', active:'В работе', queued:'В очереди', error:'Ошибка', skipped:'Пропущен: без PR'};
  const title = flow.unknown ? 'Этап не указан' : workflowStages[flow.stage][1];
  const caption = flow.completed ? 'Все этапы завершены' : flow.unknown ? 'Нет данных об этапе' :
    flow.failed ? 'Остановлено на этапе' : task.status === 'publish_queued' ? 'Повторная публикация в очереди' : 'Текущий этап';
  return '<section class="workflow" aria-label="Этапы выполнения задачи"><div class="workflow-heading"><div><span class="workflow-caption">' + caption +
    '</span><p class="workflow-title">' + (flow.completed ? 'Задача выполнена' : title) + '</p></div><span class="workflow-count">' +
    flow.states.filter(s => s === 'done').length + ' / ' + flow.states.filter(s => s !== 'skipped').length + ' завершено</span></div>' +
    '<ol class="workflow-steps">' + workflowStages.map(([key, label, short], index) => {
      const state = flow.states[index];
      const current = !flow.completed && !flow.unknown && index === flow.stage;
      const icon = state === 'done' ? '✓' : state === 'error' ? '!' : state === 'skipped' ? '−' : index + 1;
      return '<li class="workflow-step is-' + state + '"' + (current ? ' aria-current="step"' : '') +
        '><span class="step-node" aria-hidden="true">' + icon + '</span><span class="step-label" aria-hidden="true">' + short +
        '</span><span class="sr-only">' + label + ': ' + stateLabels[state] + '</span></li>';
    }).join('') + '</ol></section>';
}
function renderParticipants(task) {
  const raw = Array.isArray(task.participants) ? task.participants : String(task.participants || '').split(',');
  const roles = [...new Set(raw.map(role => role.trim().toLowerCase()).filter(role => ['developer','designer'].includes(role)))];
  if (!roles.length && ['developer','designer'].includes(task.role)) roles.push(task.role);
  const labels = {developer:'Developer', designer:'Designer', reviewer:'Reviewer'};
  const symbols = {developer:'</>', designer:'◇', reviewer:'✓'};
  return '<div class="task-team" aria-label="Участники задачи"><span class="team-caption">Команда</span>' +
    roles.concat('reviewer').map(role => '<span class="team-member team-' + role + '"><span class="team-avatar" aria-hidden="true">' +
      escapeHtml(symbols[role]) + '</span>' + labels[role] + '</span>').join('') +
    (!roles.length ? '<span class="team-pending">Исполнители назначаются</span>' : '') + '</div>';
}
let lastTasks = '';
let loading = false;
async function loadTasks() {
  if (loading) return;
  loading = true;
  const refresh = document.querySelector('#refresh');
  refresh.disabled = true;
  root.setAttribute('aria-busy', 'true');
  try {
    const tasks = await (await read('/api/tasks')).json();
    document.querySelector('#taskCount').textContent = tasks.length;
    document.querySelector('#activeCount').textContent = tasks.filter(t => ['queued','running','publishing','publish_queued','review_fix_queued','review_fixing','image_retry_queued'].includes(t.status)).length;
    document.querySelector('#completedCount').textContent = tasks.filter(t => t.status === 'completed').length;
    document.querySelector('#failedCount').textContent = tasks.filter(t => ['failed','publish_failed','review_fix_failed'].includes(t.status)).length;
    const snapshot = JSON.stringify(tasks);
    if (lastTasks === snapshot && !root.querySelector('.load-error')) return;
    lastTasks = snapshot;
    const focused = root.contains(document.activeElement) ? {id:document.activeElement.dataset.id,kind:document.activeElement.dataset.kind} : null;
    if (!tasks.length) { root.innerHTML = '<div class="empty-state"><span class="empty-icon" aria-hidden="true">&lt;/&gt;</span><h3>Команда готова к работе</h3><p>Создайте первую задачу — здесь появятся её статус, проверки и результат.</p></div>'; return; }
    root.innerHTML = tasks.map(t => {
      const status = escapeHtml(t.status);
      const id = encodeURIComponent(t.id);
      const downloadable = t.status === 'completed';
      const prLink = t.pullRequestUrl ? '<a class="action" target="_blank" rel="noopener noreferrer" href="' + escapeHtml(t.pullRequestUrl) + '">Открыть PR</a>' : '';
      const retryButton = t.createPullRequest && ['failed','publish_failed'].includes(t.status) && !t.pullRequestUrl
        ? '<button class="action" data-id="' + id + '" data-kind="retry">Повторить публикацию PR</button>' : '';
      const reviewTag = t.hasReviewFindings
        ? '<span class="check" style="color:#f4b977;border-color:#a8733c">⚠ Reviewer нашёл замечания' +
          (t.reviewFixAttempts ? ' · попыток: ' + Number(t.reviewFixAttempts) : '') + '</span>'
        : t.reviewStatus === 'reviewed' ? '<span class="check">✓ Reviewer: без замечаний</span>' : '';
      const repairEligible = t.pullRequestUrl && (
        (t.hasReviewFindings && ['completed','review_fix_failed'].includes(t.status) && (t.reviewFixAttempts || 0) < 3) ||
        (t.status === 'review_fix_failed' && !t.hasReviewFindings && t.reviewStatus === 'reviewed'));
      const fixReviewButton = repairEligible
        ? '<button class="action" data-id="' + id + '" data-kind="fix-review">' +
          (t.hasReviewFindings ? 'Исправить замечания' : 'Повторить публикацию исправлений') + '</button>' : '';
      const checksLabel = '<div class="checks"><span class="check">Тесты: ' + escapeHtml(checkStatuses[t.testStatus] || t.testStatus || '—') +
        '</span><span class="check">Reviewer: ' + escapeHtml(checkStatuses[t.reviewStatus] || t.reviewStatus || '—') + '</span>' + reviewTag + '</div>';
      const tokens = '<p class="tokens">Токены · Dev: ' + (t.hasDeveloperMetrics ? escapeHtml(t.developerInputTokens.toLocaleString('ru-RU')) +
        ' / ' + escapeHtml(t.developerOutputTokens.toLocaleString('ru-RU')) : 'нет данных') +
        ' · Designer: ' + (t.hasDesignerMetrics ? escapeHtml(t.designerInputTokens.toLocaleString('ru-RU')) +
        ' / ' + escapeHtml(t.designerOutputTokens.toLocaleString('ru-RU')) : 'нет данных') +
        ' · Review: ' + (t.hasReviewerMetrics ? escapeHtml(t.reviewerInputTokens.toLocaleString('ru-RU')) +
        ' / ' + escapeHtml(t.reviewerOutputTokens.toLocaleString('ru-RU')) : 'нет данных') + ' <span>(вход / выход)</span></p>';
      const canAdjustImageBudget = t.status === 'failed' && t.enableImageGeneration &&
        ['planning','failed','rejected'].includes(t.imageStatus) && !t.pullRequestUrl &&
        Number(t.imageBudgetUsd || 0) < 2;
      const suggestedBudget = Math.min(2, Math.max(0.75,
        Math.ceil((Number(t.imageBudgetUsd || 0) + 0.05) * 100) / 100,
        Math.ceil((Number(t.imageReservedUsd || 0) + 0.20) * 100) / 100));
      const imageBudgetEditor = canAdjustImageBudget
        ? '<div class="image-budget-editor"><label>Бюджет изображений (USD) ' +
          '<input type="number" min="0.05" max="2" step="0.05" data-budget-for="' + id +
          '" value="' + suggestedBudget.toFixed(2) + '" aria-label="Новый бюджет изображений"></label>' +
          '<button class="action" data-id="' + id + '" data-kind="increase-image-budget">Сохранить бюджет</button>' +
          '<small>Сохранение не запускает платную генерацию. После него отдельно нажми «Повторить генерацию».</small></div>' : '';
      const retryImagesButton = t.status === 'failed' &&
        t.enableImageGeneration && (['failed', 'rejected'].includes(t.imageStatus) ||
          (t.imageStatus === 'planning' && Number(t.imageReservedUsd || 0) === 0)) &&
        !t.pullRequestUrl && Number(t.imageReservedUsd || 0) + 0.015 <= Number(t.imageBudgetUsd || 0)
        ? '<button class="action" data-id="' + id + '" data-kind="retry-images">Повторить генерацию (платно)</button>' : '';
      const images = t.enableImageGeneration ?
        '<p class="tokens">Изображения · ' + escapeHtml(t.imageStatus || 'Ожидание') +
        ' · бюджет USD ' + Number(t.imageBudgetUsd).toFixed(2) +
        ' · резерв USD ' + Number(t.imageReservedUsd || 0).toFixed(3) +
        ' <span>(оценка, не фактический счёт)</span></p>' : '';
      const error = t.error ? '<p class="failed task-error">' + escapeHtml(t.error) + '</p>' : '';
      const repository = t.repositoryUrl.replace(/^https?:\/\/github\.com\//i, '').replace(/\.git$/, '');
      return '<article class="task"><div class="task-header"><span class="agent-label"><span class="agent-icon" aria-hidden="true">' + '✳' + '</span>' + 'Задача команды' + '</span><span class="status ' + status + '">' + escapeHtml(statuses[t.status] || t.status) + '</span></div>' +
        '<h3>' + escapeHtml(t.prompt) + '</h3><div class="task-meta"><span>' + escapeHtml(repository) + '</span><span>⑂ ' + escapeHtml(t.baseBranch || 'default') + '</span><time datetime="' + escapeHtml(t.createdAt) + '">' +
        escapeHtml(new Date(t.createdAt).toLocaleString('ru-RU')) + '</time></div>' + renderWorkflow(t) + renderParticipants(t) + checksLabel + tokens + images + error + imageBudgetEditor + '<div class="task-actions">' +
        '<button class="action" data-id="' + id + '" data-kind="log">Лог</button>' +
        '<button class="action" data-id="' + id + '" data-kind="diff">Diff</button>' +
        '<button class="action" data-id="' + id + '" data-kind="checks">Проверки</button>' +
        (t.enableImageGeneration ? '<button class="action" data-id="' + id + '" data-kind="images">Изображения</button>' : '') +
        (downloadable ? '<a class="action" href="/api/tasks/' + id + '/download">Скачать ZIP</a>' : '') + prLink + retryButton + fixReviewButton + retryImagesButton +
        '</div></article>';
    }).join('');
    if (focused) {
      const target = [...root.querySelectorAll('button[data-id]')].find(b => b.dataset.id === focused.id && b.dataset.kind === focused.kind);
      target?.focus({preventScroll:true});
    }
  } catch (e) {
    lastTasks = '';
    root.innerHTML = '<div class="empty-state load-error"><h3>Не удалось загрузить задачи</h3><p>' + escapeHtml(e.message) + '</p><p>Нажмите «Обновить» или дождитесь следующей попытки.</p></div>';
  } finally {
    loading = false;
    refresh.disabled = false;
    root.setAttribute('aria-busy', 'false');
  }
}
root.addEventListener('click', e => {
  const btn = e.target.closest('button[data-id]');
  if (!btn) return;
  if (btn.dataset.kind === 'retry') {
    btn.disabled = true;
    fetch('/api/tasks/' + encodeURIComponent(btn.dataset.id) + '/retry-publish', {method:'POST'})
      .then(async response => {
        if (!response.ok) throw Error(await response.text());
        message.textContent = 'Публикация поставлена в очередь. Codex повторно не запускается.';
        await loadTasks();
      })
      .catch(err => { message.textContent = 'Ошибка: ' + err.message; btn.disabled = false; });
  } else if (btn.dataset.kind === 'fix-review') {
    btn.disabled = true;
    fetch('/api/tasks/' + encodeURIComponent(btn.dataset.id) + '/fix-review', {method:'POST'})
      .then(async response => {
        if (!response.ok) throw Error(await response.text());
        message.textContent = 'Исправление замечаний Reviewer поставлено в очередь. Это запустит Codex и потратит токены.';
        await loadTasks();
      })
      .catch(err => { message.textContent = 'Ошибка исправления: ' + err.message; btn.disabled = false; });
  } else if (btn.dataset.kind === 'increase-image-budget') {
    const input = root.querySelector('input[data-budget-for="' + btn.dataset.id + '"]');
    const newBudget = Number(input?.value);
    if (!input || !Number.isFinite(newBudget) || newBudget < 0.05 || newBudget > 2) {
      message.textContent = 'Укажи бюджет от $0.05 до $2.00.';
      return;
    }
    btn.disabled = true;
    fetch('/api/tasks/' + encodeURIComponent(btn.dataset.id) + '/image-budget', {
      method:'PATCH', headers:{'Content-Type':'application/json'},
      body:JSON.stringify({imageBudgetUsd:newBudget})
    })
      .then(async response => {
        if (!response.ok) throw Error(await response.text());
        message.textContent = 'Бюджет увеличен до USD ' + newBudget.toFixed(2) +
          '. Теперь можно нажать Повторить генерацию. Сохранение бюджета не расходует API.';
        await loadTasks();
      })
      .catch(err => { message.textContent = 'Ошибка изменения бюджета: ' + err.message; btn.disabled = false; });
  } else if (btn.dataset.kind === 'retry-images') {
    btn.disabled = true;
    fetch('/api/tasks/' + encodeURIComponent(btn.dataset.id) + '/retry-images', {method:'POST'})
      .then(async response => {
        if (!response.ok) throw Error(await response.text());
        message.textContent = 'Генерация поставлена в очередь. Это запустит платный Image API, но не Designer.';
        await loadTasks();
      })
      .catch(err => { message.textContent = 'Ошибка повторной генерации: ' + err.message; btn.disabled = false; });
  } else if (btn.dataset.kind === 'images') showImages(btn.dataset.id);
  else showText(btn.dataset.id, btn.dataset.kind);
});
document.querySelector('#images').addEventListener('change', () => {
  document.querySelector('#imageSettings').hidden = !document.querySelector('#images').checked;
});
document.querySelector('#refresh').addEventListener('click', loadTasks);
document.querySelector('#form').addEventListener('submit', async e => {
  e.preventDefault();
  const button = document.querySelector('#submit');
  button.disabled = true; message.textContent = 'Отправляю...';
  try {
    const response = await fetch('/api/tasks', {
      method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({repositoryUrl:document.querySelector('#repo').value,
        prompt:document.querySelector('#prompt').value,
        role:'auto',
        baseBranch:document.querySelector('#baseBranch').value.trim() || null,
        createPullRequest:document.querySelector('#pr').checked,
        enableImageGeneration:document.querySelector('#images').checked,
        imageBudgetUsd:Number(document.querySelector('#imageBudget').value)})
    });
    const body = await response.json();
    if (!response.ok) throw Error(body.error || JSON.stringify(body));
    message.textContent = 'Задача создана: ' + body.id;
    document.querySelector('#prompt').value = '';
    updatePromptCount();
    await loadTasks();
  } catch (err) { message.textContent = 'Ошибка: ' + err.message; }
  finally { button.disabled = false; }
});
const updatePromptCount = () => {
  document.querySelector('#promptCount').textContent = document.querySelector('#prompt').value.length.toLocaleString('ru-RU') + ' / 10 000';
};
document.querySelector('#prompt').addEventListener('input', updatePromptCount);
document.querySelector('#closeDetails').addEventListener('click', () => {
  details.hidden = true;
  document.querySelector('#showMemory').focus();
});
loadTasks();setInterval(loadTasks,5000);
