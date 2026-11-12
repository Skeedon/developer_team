const root = document.querySelector('#tasks');
const message = document.querySelector('#message');
const details = document.querySelector('#details');
const content = document.querySelector('#details-content');
const escapeHtml = x => String(x ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
async function read(url) {
  const response = await fetch(url);
  if (!response.ok) throw Error(await response.text());
  return response;
}
async function showText(id, kind) {
  try {
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
    content.textContent = memory.notes || 'Пока пусто. Reviewer заполнит память после первого успешного ревью.';
    details.hidden = false;
    details.scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'});
    document.querySelector('#details-title').focus({preventScroll:true});
  } catch (e) { message.textContent = 'Ошибка памяти: ' + e.message; }
});
const statuses = {queued:'В очереди',running:'В работе',publishing:'Публикация PR',publish_queued:'PR в очереди',completed:'Завершено',failed:'Ошибка',publish_failed:'Ошибка публикации'};
const checkStatuses = {pending:'Ожидание',passed:'Пройдены',failed:'Ошибка',reviewed:'Проверено',skipped:'Пропущено',not_run:'Не запускались',running:'В работе'};
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
    document.querySelector('#activeCount').textContent = tasks.filter(t => ['queued','running','publishing','publish_queued'].includes(t.status)).length;
    document.querySelector('#completedCount').textContent = tasks.filter(t => t.status === 'completed').length;
    document.querySelector('#failedCount').textContent = tasks.filter(t => ['failed','publish_failed'].includes(t.status)).length;
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
      const checksLabel = '<div class="checks"><span class="check">Тесты: ' + escapeHtml(checkStatuses[t.testStatus] || t.testStatus || '—') +
        '</span><span class="check">Reviewer: ' + escapeHtml(checkStatuses[t.reviewStatus] || t.reviewStatus || '—') + '</span></div>';
      const tokens = '<p class="tokens">Токены · Dev: ' + (t.hasDeveloperMetrics ? escapeHtml(t.developerInputTokens.toLocaleString('ru-RU')) +
        ' / ' + escapeHtml(t.developerOutputTokens.toLocaleString('ru-RU')) : 'нет данных') +
        ' · Designer: ' + (t.hasDesignerMetrics ? escapeHtml(t.designerInputTokens.toLocaleString('ru-RU')) +
        ' / ' + escapeHtml(t.designerOutputTokens.toLocaleString('ru-RU')) : 'нет данных') +
        ' · Review: ' + (t.hasReviewerMetrics ? escapeHtml(t.reviewerInputTokens.toLocaleString('ru-RU')) +
        ' / ' + escapeHtml(t.reviewerOutputTokens.toLocaleString('ru-RU')) : 'нет данных') + ' <span>(вход / выход)</span></p>';
      const error = t.error ? '<p class="failed task-error">' + escapeHtml(t.error) + '</p>' : '';
      const repository = t.repositoryUrl.replace(/^https?:\/\/github\.com\//i, '').replace(/\.git$/, '');
      return '<article class="task"><div class="task-header"><span class="agent-label"><span class="agent-icon" aria-hidden="true">' + '✳' + '</span>' + escapeHtml((t.participants || t.role || 'developer').split(',').join(' · ')) + '</span><span class="status ' + status + '">' + escapeHtml(statuses[t.status] || t.status) + '</span></div>' +
        '<h3>' + escapeHtml(t.prompt) + '</h3><div class="task-meta"><span>' + escapeHtml(repository) + '</span><span>⑂ ' + escapeHtml(t.baseBranch || 'default') + '</span><time datetime="' + escapeHtml(t.createdAt) + '">' +
        escapeHtml(new Date(t.createdAt).toLocaleString('ru-RU')) + '</time></div>' + checksLabel + tokens + error + '<div class="task-actions">' +
        '<button class="action" data-id="' + id + '" data-kind="log">Лог</button>' +
        '<button class="action" data-id="' + id + '" data-kind="diff">Diff</button>' +
        '<button class="action" data-id="' + id + '" data-kind="checks">Проверки</button>' +
        (downloadable ? '<a class="action" href="/api/tasks/' + id + '/download">Скачать ZIP</a>' : '') + prLink + retryButton +
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
  } else showText(btn.dataset.id, btn.dataset.kind);
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
        createPullRequest:document.querySelector('#pr').checked})
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
