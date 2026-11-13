const test=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../../src/DevRefresh/wwwroot/workflow.js'),'utf8');
const dashboard=fs.readFileSync(path.join(__dirname,'../../src/DevRefresh/wwwroot/dashboard.js'),'utf8');
function fixture(backups){
  const host={innerHTML:''},events={};
  const ctx=vm.createContext({
    document:{querySelector:s=>s==='#content'?host:null,addEventListener:(name,fn)=>events[name]=fn},
    esc:value=>String(value??''),heading:()=>'',formatSize:String,formatDate:String,clearTimeout:()=>{},setTimeout:()=>{},
    state:{page:'reduction'},catalog:{backups,types:['bp_gm'],profiles:[{sourceKey:'bp_gm',backupName:'bp_gm.bak'}],analyses:[]}
  });
  vm.runInContext(dashboard+'\n'+source+'\nflow.catalog=catalog;',ctx);
  return {ctx,host,events};
}
test('first backup defines type even if SQL and profiles contain only bp_gm',()=>{
  const {ctx,host}=fixture([{name:'BP_MKO_11-11-2026.BAK',size:100,sourceKey:'bp_mko'}]);
  vm.runInContext('reductionPage()',ctx);
  assert.equal(vm.runInContext('flow.source',ctx),'bp_mko');
  assert.match(host.innerHTML,/id="analysis-source" value="bp_mko" readonly/);
  assert.match(host.innerHTML,/<option value="BP_MKO_11-11-2026.BAK" selected/);
});
test('job history keeps the final restore status above the background result',async()=>{
  const {ctx,host}=fixture([]);
  ctx.state.page='jobs';
  const row={id:'restore',kind:'refresh',status:'Running',readyAt:'2030-01-01T12:00:00Z',backgroundStatus:'Queued',stage:'Освобождение места в очереди'};
  ctx.api=async()=>[row];
  await vm.runInContext('loadJobs()',ctx);
  assert.match(host.innerHTML,/База готова к работе/);
  Object.assign(row,{status:'Succeeded',backgroundStatus:'Succeeded',stage:'База восстановлена'});
  await vm.runInContext('loadJobs()',ctx);
  assert.match(host.innerHTML,/Завершено<\/span>/);
  assert.match(host.innerHTML,/База восстановлена<small>Освобождение места завершено/);
  assert.doesNotMatch(host.innerHTML,/База готова к работе/);
});
test('changing backup updates type and removes the previous report without changing saved profiles',()=>{
  const {ctx,host,events}=fixture([{name:'bp_gm.bak',size:100,sourceKey:'bp_gm'},{name:'ut_ke.bak',size:100,sourceKey:'ut_ke'}]);
  vm.runInContext('reductionPage();flow.analysis={sourceKey:"bp_gm",backupName:"bp_gm.bak"};flow.selection.set("old",{key:"old"});',ctx);
  events.change({target:{id:'analysis-backup',value:'ut_ke.bak',dataset:{}}});
  assert.equal(vm.runInContext('flow.source',ctx),'ut_ke');
  assert.equal(vm.runInContext('flow.backupName',ctx),'ut_ke.bak');
  assert.equal(vm.runInContext('flow.analysis',ctx),null);
  assert.equal(vm.runInContext('flow.selection.size',ctx),0);
  assert.equal(vm.runInContext('flow.catalog.profiles[0].sourceKey',ctx),'bp_gm');
  assert.match(host.innerHTML,/<option value="ut_ke.bak" selected/);
  vm.runInContext('reductionPage()',ctx);
  assert.equal(vm.runInContext('flow.source',ctx),'ut_ke');
});
test('empty backup directory does not invent a source type',()=>{
  const {ctx,host}=fixture([]);vm.runInContext('reductionPage()',ctx);
  assert.equal(vm.runInContext('flow.source',ctx),'');
  assert.match(host.innerHTML,/type="submit" disabled/);
});
test('alias tab shows the effective file and analysis offers the mapped Dev type for profiles',()=>{
  const {ctx,host}=fixture([{name:'ut_11-11-2026.bak',size:100,sourceKey:'ut'}]);
  vm.runInContext('catalog.devTypes=["ut_ke"];catalog.backupAliases={ut_ke:"ut"};flow.tab="aliases";reductionPage()',ctx);
  assert.match(host.innerHTML,/id="backup-alias-form"/);
  assert.match(host.innerHTML,/ut_11-11-2026\.bak<\/td>/);
  assert.match(host.innerHTML,/data-alias-edit="ut_ke"/);
  const report=vm.runInContext('flow.analysis={sourceKey:"ut",backupName:"ut_11-11-2026.bak",tables:[],warnings:[]};analysisMarkup(flow.analysis)',ctx);
  assert.match(report,/id="profile-target"/);
  assert.match(report,/<option value="ut_ke" selected>/);
  assert.match(report,/Сохранить профиль для ut_ke/);
});

test('refresh defaults to Shrink when available and displays both archive dates',async()=>{
  const {ctx}=fixture([]);
  const form={elements:{sourceMode:{value:'shrink',options:[{}]},runNow:{checked:true},sourceDatabase:{value:''}},
    querySelector:()=>({}),getAttribute:()=>null};
  const dialog={innerHTML:'',classList:{add(){}},showModal(){},querySelector:()=>form};
  ctx.document.querySelector=()=>dialog;
  const options={backupName:'bp_22-11-2026.bak',backupDate:'2026-11-22',activeSessions:0,
    shrinkBackup:{size:2.5*1024**3,dataDate:'2026-11-21'},copySources:[{name:'bp_dev_source'}]};
  ctx.api=async()=>options;

  await vm.runInContext('openRefreshDialog({name:"bp_dev_owner"})',ctx);
  assert.match(dialog.innerHTML,/<option value="shrink" selected>Архив из папки \(shrink\) · 2,5 ГБ/);
  assert.match(dialog.innerHTML,/21\.11\.2026/);
  assert.match(dialog.innerHTML,/22\.11\.2026/);
  assert.match(dialog.innerHTML,/Другая Dev-база/);
  assert.doesNotMatch(dialog.innerHTML,/name="shrink"/);

  options.shrinkBackup=null;
  options.backupDate=null;
  options.backupModifiedAt='2026-11-22T10:00:00Z';
  form.elements.sourceMode.value='archive';
  await vm.runInContext('openRefreshDialog({name:"bp_dev_owner"})',ctx);
  assert.match(dialog.innerHTML,/<option value="archive" selected>Архив из папки \(полный\)/);
  assert.match(dialog.innerHTML,/Копии пока нет/);
  assert.match(dialog.innerHTML,/Файл изменён 2026-11-22/);
});
