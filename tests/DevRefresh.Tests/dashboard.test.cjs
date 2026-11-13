const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname,'../../src/DevRefresh/wwwroot/dashboard.js'),'utf8');
function context(extra = {}) {
  const ctx = vm.createContext({Date,Set,Map,AbortController,setTimeout,clearTimeout,
    document:{querySelector:()=>null},state:{page:'databases',session:{user:{kind:'local',name:'admin'}}},...extra});
  vm.runInContext(source,ctx);return ctx;
}
test('five minute indicator expires; active work never ages out',()=>{
  const ctx = context();ctx.now=Date.parse('2030-01-01T12:00:00Z');
  ctx.jobs=[
    {id:'running',status:'Running',createdAt:'2029-01-01T00:00:00Z'},
    {id:'queued',status:'Queued',createdAt:'2029-01-02T00:00:00Z'},
    {id:'recent',status:'Succeeded',updatedAt:'2030-01-01T11:55:00.001Z'},
    {id:'expired',status:'Succeeded',updatedAt:'2030-01-01T11:55:00Z'}];
  const ids=vm.runInContext('visibleDashboardJobs(jobs,now,300000).map(j=>j.id).join(",")',ctx);
  assert.equal(ids,'running,queued,recent');
});
test('late poll cannot restore data or timers after leaving the main page',async()=>{
  let reply;let timers=0;
  const ctx=context({api:()=>new Promise(resolve=>{reply=resolve}),setTimeout:()=>{timers++;return 1},clearTimeout:()=>{}});
  const pending=vm.runInContext('pollDashboardJobs(dashboard.generation)',ctx);
  vm.runInContext('stopDashboardJobs(true);state.page="settings"',ctx);
  reply({serverTime:new Date().toISOString(),completedVisibleSeconds:300,jobs:[{id:'old',status:'Running'}]});
  await pending;
  assert.equal(vm.runInContext('dashboard.jobs.length',ctx),0);assert.equal(timers,0);
});
test('switching users drops cached job identities',()=>{
  const ctx=context({api:()=>new Promise(()=>{}),clearTimeout:()=>{}});
  vm.runInContext('dashboard.user="domain:old-user";dashboard.jobs=[{id:"private"}];startDashboardJobs()',ctx);
  assert.equal(vm.runInContext('dashboard.jobs.length',ctx),0);
  vm.runInContext('stopDashboardJobs(true)',ctx);
});
test('completed restore updates both database size and status without replacing the table',async()=>{
  const row={name:'bp_dev_demo_owner',state:'RESTORING',sizeBytes:100};
  const badge={dataset:{databaseState:row.name},textContent:'RESTORING',className:''};
  const size={dataset:{databaseSize:row.name},textContent:'100'};
  const ctx=context({
    state:{page:'databases',databases:{databases:[row]}},
    api:async()=>({databases:[{name:row.name.toUpperCase(),state:'ONLINE',sizeBytes:9876543210}]}),
    databaseSizeLabel:r=>String(r.sizeBytes),
    document:{querySelector:()=>null,querySelectorAll:selector=>selector==='[data-database-state]'?[badge]:[size]}
  });
  await vm.runInContext('refreshDashboardDatabaseStates(dashboard.generation)',ctx);
  assert.equal(row.sizeBytes,9876543210);assert.equal(row.state,'ONLINE');
  assert.equal(size.textContent,'9876543210');assert.equal(badge.textContent,'ONLINE');
});
test('main page lists each active job and keeps the per-database status',()=>{
  const host={hidden:false,innerHTML:'',replaceChildren(){this.innerHTML=''},querySelectorAll:()=>[]};
  const cell={dataset:{databaseJob:'bp_dev_demo_owner'},hidden:true,innerHTML:'',querySelector:()=>null};
  const ctx=context({esc:value=>String(value),document:{querySelector:()=>host,querySelectorAll:()=>[cell]}});
  ctx.jobs=[
    {id:'restore',target:'bp_dev_demo_owner',kind:'refresh',status:'Running',stage:'Restore',createdAt:new Date().toISOString()},
    {id:'queued',target:'ut_dev_demo_owner',kind:'refresh',status:'Queued',stage:'В очереди',createdAt:new Date().toISOString()}];
  vm.runInContext('dashboard.jobs=jobs;renderDashboardJobs()',ctx);
  assert.match(host.innerHTML,/dashboard-summary/);assert.match(host.innerHTML,/Все задания/);
  assert.equal([...host.innerHTML.matchAll(/<li data-dashboard-job-id=/g)].length,2);
  assert.match(host.innerHTML,/bp_dev_demo_owner/);assert.match(host.innerHTML,/ut_dev_demo_owner/);
  assert.match(cell.innerHTML,/<progress/);assert.equal(cell.hidden,false);
});
test('historical progress is capped until success and missing history shows elapsed time',()=>{
  const ctx=context({jobDuration:ms=>`${ms/1000} с`});
  ctx.job={status:'Running',startedAt:'2030-01-01T12:00:00Z',estimatedDurationMs:100000};
  ctx.now=Date.parse(ctx.job.startedAt)+45000;
  assert.equal(vm.runInContext('dashboardProgressValue(job,now).percent',ctx),45);
  assert.equal(vm.runInContext('dashboardProgressValue(job,now+80000).percent',ctx),99);
  assert.equal(vm.runInContext('dashboardProgressValue({...job,status:"Succeeded"},now).percent',ctx),100);
  assert.equal(vm.runInContext('dashboardProgressValue({...job,estimatedDurationMs:null},now).label',ctx),'Прошло 45 с');
});
test('a ready database shows completion after the background work finishes',()=>{
  const ctx=context({esc:String});
  ctx.job={status:'Running',readyAt:'2030-01-01T12:00:00Z',backgroundStatus:'Running',stage:'Сжатие файлов'};
  assert.match(vm.runInContext('dashboardProgress(job)',ctx),/База готова к работе/);
  ctx.job={...ctx.job,status:'Succeeded',backgroundStatus:'Succeeded',stage:'База восстановлена'};
  const markup=vm.runInContext('dashboardProgress(job)',ctx);
  assert.match(markup,/Завершено/);
  assert.match(markup,/База восстановлена/);
  assert.doesNotMatch(markup,/База готова к работе/);
});
test('scheduled refresh shows its time and does not show a progress bar',()=>{
  const host={hidden:false,innerHTML:'',replaceChildren(){this.innerHTML=''},querySelectorAll:()=>[]};
  const cell={dataset:{databaseJob:'bp_dev_demo_owner'},hidden:true,innerHTML:'',querySelector:()=>null};
  const ctx=context({esc:String,formatDate:String,document:{querySelector:()=>host,querySelectorAll:()=>[cell]}});
  ctx.jobs=[{id:'future',target:cell.dataset.databaseJob,kind:'refresh',status:'Scheduled',
    scheduledAt:'2030-01-02T02:30:00Z',createdAt:'2030-01-01T12:00:00Z'}];
  vm.runInContext('dashboard.jobs=jobs;renderDashboardJobs()',ctx);
  assert.match(host.innerHTML,/Запланировано: 1/);
  assert.match(cell.innerHTML,/2030-01-02T02:30:00Z/);
  assert.doesNotMatch(host.innerHTML+cell.innerHTML,/dashboard-progress/);
});
