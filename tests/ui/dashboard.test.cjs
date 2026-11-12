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
  element('#images').checked = false;
  element('#imageBudget').value = '0.35';
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
  for (const id of ['tasks','form','repo','baseBranch','prompt','pr','images','imageBudget','image-details','submit','showMemory','refresh','details','details-title','details-content']) assert.ok(html.includes(`id="${id}"`), id);
  assert.match(html,/Исполнители назначаются автоматически/);
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
test('automatic assignment, task creation payload and prompt reset',async()=>{
  const ui=setup();await ui.flush();
  ui.element('#repo').value='https://github.com/team/repo'; ui.element('#baseBranch').value='  ';
  ui.element('#prompt').value='Improve UI';
  await ui.listeners['#form:submit']({preventDefault(){}});
  const call=ui.calls.find(c=>c.options?.method==='POST');
  assert.deepEqual(JSON.parse(call.options.body),{repositoryUrl:'https://github.com/team/repo',prompt:'Improve UI',role:'auto',baseBranch:null,createPullRequest:true,enableImageGeneration:false,imageBudgetUsd:0.35});
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

function workflow(task) {
  const ui = setup();
  // Evaluate the pure presentation adapter in the same context as the dashboard.
  const context = vm.createContext({document:{querySelector:ui.element},window:{},
    setInterval(){},fetch:async()=>({ok:true,json:async()=>[]}),encodeURIComponent});
  vm.runInContext(source, context);
  context.task = task;
  return JSON.parse(vm.runInContext('JSON.stringify(taskWorkflow(task))', context));
}
test('stage transitions show completed, current and waiting milestones',()=>{
  for (const [index,currentStage] of ['assignment','development','testing','review','publication'].entries()) {
    const flow=workflow({status:'running',createPullRequest:true,currentStage});
    assert.deepEqual(flow.states,Array.from({length:5},(_,i)=>i<index?'done':i===index?'active':'pending'));
  }
});
test('errors preserve the failed stage and retries resume only publication',()=>{
  assert.deepEqual(workflow({status:'failed',currentStage:'testing',createPullRequest:true}).states,
    ['done','done','error','pending','pending']);
  for (const [status,state] of [['publish_failed','error'],['publish_queued','queued'],['publishing','active'],['completed','done']]) {
    assert.deepEqual(workflow({status,createPullRequest:true,currentStage:'testing'}).states,['done','done','done','done',state]);
  }
});
test('legacy records, unknown stages and tasks without PR remain honest',()=>{
  assert.deepEqual(workflow({status:'completed',createPullRequest:false}).states,['done','done','done','done','skipped']);
  assert.deepEqual(workflow({status:'queued'}).states,['queued','pending','pending','pending','skipped']);
  for(const status of ['running','failed']) {
    const flow=workflow({status,currentStage:'future_stage',createPullRequest:true});
    assert.equal(flow.unknown,true);assert.ok(flow.states.every(s=>s==='pending'));
  }
  assert.equal(workflow({status:'running',testStatus:'running'}).stage,2);
  assert.equal(workflow({status:'running',reviewStatus:'running'}).stage,3);
});
test('team rendering supports one or multiple assignees, automatic assignment and accessible stages',async()=>{
  for(const participants of ['developer,designer',['designer','developer']]) {
    const ui=setup([{...task,participants,currentStage:'review',status:'running',createPullRequest:true}]);await ui.flush();
    const output=ui.element('#tasks').innerHTML;
    for(const label of ['Developer','Designer','Reviewer','aria-current="step"','Reviewer: В работе','Назначение исполнителей: Завершён']) assert.ok(output.includes(label),label);
  }
  const ui=setup([{...task,role:'auto',participants:'',status:'queued',pullRequestUrl:null}]);await ui.flush();
  assert.match(ui.element('#tasks').innerHTML,/Исполнители назначаются/);
  assert.match(ui.element('#tasks').innerHTML,/Пропущен: без PR/);
});

test('image opt-in is disabled by default and opt-in payload is explicit', async () => {
  const ui=setup();await ui.flush();
  assert.equal(ui.element('#images').checked,false);
  ui.element('#images').checked=true;
  ui.element('#imageBudget').value='0.4';
  ui.element('#repo').value='https://github.com/team/repo';
  ui.element('#prompt').value='Create game art';
  await ui.listeners['#form:submit']({preventDefault(){}});
  const payload=JSON.parse(ui.calls.find(c=>c.options?.method==='POST').options.body);
  assert.equal(payload.enableImageGeneration,true);
  assert.equal(payload.imageBudgetUsd,0.4);
});

test('image-enabled task shows asset control and estimated budget',async()=>{
  const ui=setup([{...task,enableImageGeneration:true,imageStatus:'reviewing',
    imageBudgetUsd:0.35,imageReservedUsd:0.027}]);await ui.flush();
  const rendered=ui.element('#tasks').innerHTML;
  assert.match(rendered,/data-kind="images"/);
  assert.match(rendered,/0.027/);
});

test('failed image task exposes manual retry without opening a new coding task',async()=>{
  const ui=setup([{...task,status:'failed',enableImageGeneration:true,imageStatus:'failed',
    pullRequestUrl:null,imageBudgetUsd:0.35,imageReservedUsd:0.015}]);await ui.flush();
  assert.match(ui.element('#tasks').innerHTML,/data-kind="retry-images"/);
  await ui.listeners['#tasks:click']({target:{closest:()=>({disabled:false,dataset:{id:'abc',kind:'retry-images'}})}});
  await ui.flush();
  assert.ok(ui.calls.some(c=>c.url==='/api/tasks/abc/retry-images'&&c.options.method==='POST'));
});

test('legacy failed image manifest can be retried after parser upgrade',async()=>{
  const ui=setup([{...task,status:'failed',enableImageGeneration:true,
    imageStatus:'planning',pullRequestUrl:null,imageBudgetUsd:0.35,imageReservedUsd:0}]);
  await ui.flush();
  assert.match(ui.element('#tasks').innerHTML,/data-kind="retry-images"/);
});

test('image budget update is offered for failed image planning before any paid request',async()=>{
  const ui=setup([{...task,status:'failed',enableImageGeneration:true,
    imageStatus:'planning',pullRequestUrl:null,imageBudgetUsd:0.35,imageReservedUsd:0,
    error:'Insufficient budget for 2 separate image assets (minimum reserved USD 0.574)'}]);
  await ui.flush();
  const output=ui.element('#tasks').innerHTML;
  assert.match(output,/data-kind="increase-image-budget"/);
  assert.match(output,/data-budget-for="abc"/);
  assert.match(output,/value="0.75"/);
  assert.match(output,/Сохранение не запускает платную генерацию/);
});

test('failed task with budget shortfall shows budget editor but not futile retry',async()=>{
  const ui=setup([{...task,status:'failed',enableImageGeneration:true,
    imageStatus:'planning',pullRequestUrl:null,imageBudgetUsd:0.35,imageReservedUsd:0,
    error:'Insufficient budget for 2 separate image assets (minimum reserved USD 0.574, already reserved USD 0.000)'}]);
  await ui.flush();
  const rendered=ui.element('#tasks').innerHTML;
  assert.match(rendered,/data-kind="increase-image-budget"/);
  assert.doesNotMatch(rendered,/data-kind="retry-images"/);
  assert.match(rendered,/value="0.75"/);
});

test('saving increased image budget uses PATCH and never starts a paid image call',async()=>{
  const ui=setup([{...task,status:'failed',enableImageGeneration:true,
    imageStatus:'planning',pullRequestUrl:null,imageBudgetUsd:0.35,imageReservedUsd:0}]);
  await ui.flush();
  ui.element('#tasks').querySelector=() => ({value:'0.75'});
  const button={disabled:false,dataset:{id:'abc',kind:'increase-image-budget'}};
  await ui.listeners['#tasks:click']({target:{closest:()=>button}});
  await ui.flush();
  const call=ui.calls.find(c=>c.url==='/api/tasks/abc/image-budget');
  assert.ok(call);
  assert.equal(call.options.method,'PATCH');
  assert.deepEqual(JSON.parse(call.options.body),{imageBudgetUsd:0.75});
  assert.equal(ui.calls.filter(c=>c.url.includes('retry-images')).length,0);
});
