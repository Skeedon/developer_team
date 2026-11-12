// Dependency-free UI contract checks. Run: node --test tests/ui/dashboard.test.cjs
const {test} = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const source = fs.readFileSync('src/DeveloperTeam/wwwroot/dashboard.js', 'utf8');
const html = fs.readFileSync('src/DeveloperTeam/wwwroot/index.html', 'utf8');

function setup(tasks = []) {
  const elements = new Map();
  const calls = [];
  const listeners = {};
  function element(selector) {
    if (!elements.has(selector)) elements.set(selector, {
      value:'', checked:true, textContent:'', innerHTML:'', dataset:{}, hidden:true,
      addEventListener(type, fn) { listeners[selector + ':' + type] = fn; },
      setAttribute() {}, contains() {return false;}, querySelector() {return null;},
      querySelectorAll() {return [];}, scrollIntoView() {}, focus() {},
    });
    return elements.get(selector);
  }
  element('input[name=agentRole]:checked').value = 'developer';
  const document = {querySelector:element,querySelectorAll:() => [element('developer'), element('designer')]};
  const context = vm.createContext({document,window:{},matchMedia:() => ({matches:true}),setInterval(){},
    fetch: async (url, options) => {
      calls.push({url,options});
      return {ok:true,json:async()=>url === '/api/tasks' && !options ? tasks : url.includes('reviewer-memory') ? {notes:'Reviewer notes'} : url.endsWith('/checks') ? {testStatus:'passed',reviewStatus:'reviewed'} : {id:'new-task'},text:async()=> 'diff or log'};
    },encodeURIComponent,console});
  vm.runInContext(source,context);
  const flush = () => new Promise(resolve=>setImmediate(resolve));
  return {element,listeners,calls,flush};
}
const task = {id:'abc',status:'completed',role:'designer',prompt:'<script>alert(1)</script>',repositoryUrl:'https://github.com/team/repo',baseBranch:'main',createdAt:'2026-11-12T10:00:00Z',testStatus:'passed',reviewStatus:'reviewed',hasDeveloperMetrics:true,developerInputTokens:123,developerOutputTokens:45,hasReviewerMetrics:false,pullRequestUrl:'https://github.com/team/repo/pull/1'};

test('form controls and assets remain connected',()=>{
  for (const id of ['tasks','form','repo','baseBranch','prompt','pr','submit','showMemory','refresh','details','details-title','details-content','roleHint']) assert.ok(html.includes(`id="${id}"`), id);
  assert.match(html,/name="agentRole" value="developer" checked/);
  assert.match(html,/name="agentRole" value="designer"/);
  assert.match(html,/dashboard.css/); assert.match(html,/dashboard.js/);
});
test('completed task renders safely with checks, metrics, ZIP and PR',async()=>{
  const ui=setup([task]); await ui.flush();
  const output=ui.element('#tasks').innerHTML;
  for(const text of ['Designer','Завершено','&lt;script&gt;','Пройдены','Проверено','123','/download','Открыть PR','data-kind="log"','data-kind="diff"','data-kind="checks"']) assert.ok(output.includes(text),text);
  assert.ok(!output.includes('<script>'));
  assert.equal(ui.element('#completedCount').textContent,1);
});
test('empty, active and failed states; retry eligibility',async()=>{
  const empty=setup(); await empty.flush(); assert.match(empty.element('#tasks').innerHTML,/Команда готова/);
  const ui=setup([{...task,status:'running',pullRequestUrl:null},{...task,id:'failed',status:'publish_failed',createPullRequest:true,pullRequestUrl:null,error:'<error>'}]); await ui.flush();
  assert.equal(ui.element('#activeCount').textContent,1);assert.equal(ui.element('#failedCount').textContent,1);
  assert.match(ui.element('#tasks').innerHTML,/data-kind="retry"/); assert.match(ui.element('#tasks').innerHTML,/&lt;error&gt;/);
  assert.ok(!ui.element('#tasks').innerHTML.includes('/download'));
});
test('role switch, task creation payload and prompt reset',async()=>{
  const ui=setup();await ui.flush();
  ui.listeners['designer:change']({target:{value:'designer'}});
  assert.match(ui.element('#roleHint').textContent,/Designer/);
  ui.element('input[name=agentRole]:checked').value='designer';
  ui.element('#repo').value='https://github.com/team/repo'; ui.element('#baseBranch').value='  ';
  ui.element('#prompt').value='Improve UI';
  await ui.listeners['#form:submit']({preventDefault(){}});
  const call=ui.calls.find(c=>c.options?.method==='POST');
  assert.deepEqual(JSON.parse(call.options.body),{repositoryUrl:'https://github.com/team/repo',prompt:'Improve UI',role:'designer',baseBranch:null,createPullRequest:true});
  assert.equal(ui.element('#prompt').value,'');assert.equal(ui.element('#submit').disabled,false);
});
test('logs, diff, checks and reviewer memory remain available',async()=>{
  const ui=setup();await ui.flush();
  for(const kind of ['log','diff','checks']) {
    await ui.listeners['#tasks:click']({target:{closest:()=>({dataset:{id:'abc',kind}})}});
    await ui.flush();
    assert.equal(ui.element('#details').hidden,false);
    assert.ok(ui.calls.some(c=>c.url===`/api/tasks/abc/${kind}`));
  }
  await ui.listeners['#showMemory:click']();
  assert.equal(ui.element('#details-content').textContent,'Reviewer notes');
});
