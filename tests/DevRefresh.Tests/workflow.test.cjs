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
  const options={backupName:'bp_22-11-2026.bak',backupDate:'2026-11-22',backupSize:4*1024**3,activeSessions:0,copyBackupConfigured:true,
    shrinkBackup:{size:2.5*1024**3,dataDate:'2026-11-21'},copySources:[{name:'bp_dev_source'}]};
  ctx.api=async()=>options;

  await vm.runInContext('openRefreshDialog({name:"bp_dev_owner"})',ctx);
  assert.match(dialog.innerHTML,/<option value="shrink" selected>Shrink-копия \/ 21\.11\.2026 \/ 2,5 ГБ/);
  assert.match(dialog.innerHTML,/<option value="archive"\s*>Full-копия \/ 22\.11\.2026 \/ 4 ГБ/);
  assert.match(dialog.innerHTML,/Другая Dev-база/);
  assert.doesNotMatch(dialog.innerHTML,/name="shrink"/);

  options.shrinkBackup=null;
  options.backupDate=null;
  options.backupModifiedAt='2026-11-22T10:00:00Z';
  form.elements.sourceMode.value='archive';
  await vm.runInContext('openRefreshDialog({name:"bp_dev_owner"})',ctx);
  assert.match(dialog.innerHTML,/<option value="archive" selected>Full-копия \/ файл изменён 2026-11-22/);
  assert.match(dialog.innerHTML,/Shrink-копия \/ недоступна/);
});

test('only the second dialog can submit the replacement and approve ending target connections',async()=>{
  const {ctx,events}=fixture([]);
  ctx.state.page='databases';
  ctx.HTMLFormElement=class {
    id='refresh-form';
    elements={sourceMode:{value:'database'},runNow:{checked:true},sourceDatabase:{value:'bp_dev_source'}};
    getAttribute(){return null;}
    matches(){return false;}
    reportValidity(){return true;}
    querySelector(){return {disabled:false};}
  };
  let close, requests=[];
  const target={}, confirmation={querySelector:()=>target,showModal(){},addEventListener(name,fn){close=fn;}};
  ctx.document.querySelector=selector=>selector==='#refresh-confirmation'?confirmation:{close(){}};
  ctx.api=async (url,options)=>{requests.push([url,JSON.parse(options.body)]);return {id:'job'};};
  ctx.busy=async (_,work)=>work();
  ctx.toast=ctx.rememberDashboardJob=()=>{};
  vm.runInContext('flow.refresh={row:{name:"bp_dev_owner"}}',ctx);
  const submit=()=>events.submit({target:new ctx.HTMLFormElement(),preventDefault(){}});
  const cancelled=submit();
  assert.match(target.textContent,/bp_dev_owner/);
  assert.equal(requests.length,0);
  close();
  await cancelled;
  assert.equal(requests.length,0);
  const accepted=submit();
  confirmation.returnValue='confirm';
  close();
  await accepted;
  assert.deepEqual(requests,[['/api/refresh',{database:'bp_dev_owner',shrink:false,pii:false,
    sourceDatabase:'bp_dev_source',confirmReplace:true,confirmDisconnect:true,scheduledAt:null}]]);
});

test('DT is the fourth fallback after Shrink, Full and a usable developer copy',()=>{
  const {ctx}=fixture([]);
  const options={dtConfigured:true,dtBackup:{name:'bp.dt'}};
  ctx.options=options;
  const preferred=()=>vm.runInContext('preferredRefreshSource(options)',ctx);
  assert.equal(preferred(),'dt');
  options.copySources=[{name:'bp_dev_source'}];options.copyBackupConfigured=true;
  assert.equal(preferred(),'database');
  options.backupName='bp.bak';assert.equal(preferred(),'archive');
  options.shrinkBackup={};assert.equal(preferred(),'shrink');
  options.shrinkBackup=null;options.backupName=null;options.copyBackupConfigured=false;
  assert.equal(preferred(),'dt');
  options.dtConfigured=false;assert.equal(preferred(),'archive');
});

test('DT dialog selects the archive and permits a future archive only for scheduled work',async()=>{
  const {ctx}=fixture([]);
  const controls={notice:{},submit:{},source:{}};
  const form={elements:{sourceMode:{value:'dt',options:[{},{},{},{}]},runNow:{checked:true},sourceDatabase:{value:''}},
    querySelector:s=>s==='#refresh-publish-notice'?controls.notice:s==='#refresh-submit'?controls.submit:controls.source,
    getAttribute:()=>null};
  const dialog={innerHTML:'',classList:{add(){}},showModal(){},querySelector:()=>form};
  ctx.document.querySelector=()=>dialog;
  const options={dtConfigured:true,dtBackup:{name:'bp_18-11-2026.dt',backupDate:'2026-11-18',size:1024**3},copySources:[],activeSessions:0};
  ctx.api=async()=>options;
  await vm.runInContext('openRefreshDialog({name:"bp_dev_owner"})',ctx);
  assert.match(dialog.innerHTML,/<option value="dt" selected>\.dt через IBCMD \/ 18\.11\.2026 \/ 1 ГБ/);
  assert.equal(controls.submit.disabled,false);
  assert.equal(form.elements.sourceMode.options[1].disabled,true);
  assert.match(controls.notice.textContent,/bp_18-11-2026\.dt/);
  options.dtBackup=null;form.elements.runNow.checked=false;
  ctx.form=form;vm.runInContext('syncRefreshSource(form)',ctx);
  assert.equal(form.elements.sourceMode.options[3].disabled,false);
  assert.equal(controls.submit.disabled,false);
  form.elements.runNow.checked=true;
  vm.runInContext('syncRefreshSource(form)',ctx);
  assert.equal(form.elements.sourceMode.options[3].disabled,true);
  assert.equal(controls.submit.disabled,true);
});

test('DT submission retains the replacement confirmation and sends an explicit source flag',async()=>{
  const {ctx,events}=fixture([]);
  ctx.state.page='databases';
  ctx.HTMLFormElement=class {
    id='refresh-form';elements={sourceMode:{value:'dt'},runNow:{checked:true},sourceDatabase:{value:''}};
    getAttribute(){return null;}matches(){return false;}reportValidity(){return true;}
    querySelector(){return {disabled:false};}
  };
  let close,request;
  const confirmationText={};
  const confirmation={querySelector:()=>confirmationText,showModal(){},addEventListener(name,fn){close=fn;}};
  ctx.document.querySelector=s=>s==='#refresh-confirmation'?confirmation:{close(){}};
  ctx.api=async(url,options)=>{request=JSON.parse(options.body);return {id:'dt-job'};};
  ctx.busy=async(_,work)=>work();ctx.toast=ctx.rememberDashboardJob=()=>{};
  vm.runInContext('flow.refresh={row:{name:"bp_dev_owner"}}',ctx);
  const pending=events.submit({target:new ctx.HTMLFormElement(),preventDefault(){}});
  assert.equal(request,undefined);
  assert.match(confirmationText.textContent,/удаление SQL-базы bp_dev_owner/);
  assert.match(confirmationText.textContent,/При ошибке прежняя база не восстановится/);
  confirmation.returnValue='confirm';close();await pending;
  assert.equal(request.dt,true);assert.equal(request.shrink,false);assert.equal(request.sourceDatabase,null);
  assert.equal(request.confirmReplace,true);assert.equal(request.confirmDisconnect,true);
});

test('job details show post-restore operation counts without account data',()=>{
  const {ctx}=fixture([]);
  ctx.number=String;
  const markup=vm.runInContext('postRestoreResultsMarkup([{kind:"windows-domain",targetDomain:"CORP",changed:4,unchanged:2}])',ctx);
  assert.match(markup,/Подмена домена → CORP: изменено 4, уже соответствовало 2/);
});
