const $ = id => document.getElementById(id);
const number = value => value == null ? 'Нет данных' : value.toLocaleString('ru-RU');
const escapeHtml = value => String(value ?? 'Нет данных').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const tokens = (row, role) => row[role + 'Input'] == null ? 'Нет данных' :
  ['Input', 'CachedInput', 'Output'].map(key => number(row[role + key])).join(' / ');
const total = (row, role) => row[role + 'Input'] == null ? null : row[role + 'Input'] + row[role + 'Output'];
let request;
async function load() {
  request?.abort();
  const controller = request = new AbortController();
  $('refresh').disabled = true;
  $('report').hidden = true;
  $('message').textContent = 'Загрузка…';
  try {
    const response = await fetch('/api/analytics?period=' + $('period').value, {signal:controller.signal});
    if (!response.ok) throw Error('HTTP ' + response.status);
    const {totals:t, daily, recent} = await response.json();
    const cards = [ ['Всего задач', number(t.total)], ['Успешных', number(t.successful)],
      ['Неуспешных', number(t.failed)], ['Средняя длительность', t.averageDurationSeconds == null ? 'Нет данных' : number(Math.round(t.averageDurationSeconds)) + ' сек'],
      ['Попыток исправления', number(t.fixAttempts)], ['Задач с успешным исправлением', number(t.tasksHelpedByFixes)] ];
    $('cards').innerHTML = cards.map(([title,value]) => `<section class="card"><h2>${title}</h2><div class="value">${value}</div></section>`).join('');
    $('roles').innerHTML = ['developer','reviewer'].map(role => `<tr><th>${role === 'developer' ? 'Developer' : 'Reviewer'}</th>${['Input','CachedInput','Output'].map(key => `<td>${number(t[role+key])}</td>`).join('')}<td>${number(t[role+'MeasuredTasks'])} / ${number(t.total)}</td></tr>`).join('');
    $('coverage').textContent = `Счётчик исправлений есть у ${number(t.fixMeasuredTasks)} из ${number(t.total)} задач. Суммы включают только известные метрики; исторические данные не считаются нулевыми. Успех — completed; ошибки — failed и publish_failed. Средняя длительность — от старта до завершения, без очереди.`;
    const max = Math.max(1,...daily.flatMap(d => [total(d,'developer') ?? 0,total(d,'reviewer') ?? 0]));
    $('chart').innerHTML = daily.length ? daily.map(d => {
      const date = d.date.slice(0,10);
      return `<div class="day"><div class="bars">${['developer','reviewer'].map(role => {
        const value = total(d,role);
        const label = `${date} ${role}: ${number(value)}`;
        return value == null ? `<span title="${label}" aria-label="${label}">—</span>` : `<div class="bar ${role === 'developer' ? 'dev' : 'review'}" style="height:${value/max*140}px" title="${label}" aria-label="${label}"></div>`;
      }).join('')}</div><time>${date}</time></div>`;
    }).join('') : '<p>Задач за период нет.</p>';
    $('daily').innerHTML = daily.map(d => `<tr><td>${d.date.slice(0,10)}</td><td>${number(total(d,'developer'))}</td><td>${number(total(d,'reviewer'))}</td></tr>`).join('');
    $('recent').innerHTML = recent.length ? recent.map(task => `<tr><td class="prompt"><strong>${escapeHtml(task.prompt)}</strong><p>${escapeHtml(task.repositoryUrl)}</p><small>${escapeHtml(task.createdAt)}<br>${escapeHtml(task.id)}</small></td><td>${escapeHtml(task.status)}</td><td class="tokens">${tokens(task,'developer')}</td><td class="tokens">${tokens(task,'reviewer')}</td><td>${escapeHtml(task.testStatus)}</td><td>${escapeHtml(task.reviewStatus)}</td><td>${number(task.fixAttempts)}</td></tr>`).join('') : '<tr><td colspan="7">Задач за период нет.</td></tr>';
    $('report').hidden = false;
    $('message').textContent = '';
  } catch(error) {
    if (error.name !== 'AbortError') $('message').textContent = 'Не удалось загрузить аналитику: ' + error.message;
  } finally {
    if (request === controller) $('refresh').disabled = false;
  }
}
$('period').addEventListener('change',load);
$('refresh').addEventListener('click',load);
load();
