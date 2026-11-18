const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname,'../../src/DevRefresh/wwwroot/app.js'),'utf8')
  .replace(/\nstart\(\);\s*$/, '\n');

function context() {
  const content = {innerHTML:''};
  const listeners = {};
  class Form {}
  const ctx = vm.createContext({Date,Intl,AbortSignal,HTMLFormElement:Form,
    document:{querySelector:()=>content,addEventListener:(name,handler)=>{listeners[name]=handler;}},
    renderDashboardJobs:()=>{},setTimeout,clearTimeout});
  vm.runInContext(source,ctx);
  vm.runInContext(`
    loadUpdatePanel=async()=>{};
    updateAuthenticationFields=()=>{};
    state.session={user:{isAdmin:true,ownerKey:'alice'}};
    state.settings={connection:{configured:false},windowsAuthentication:{enabled:true,autoSignIn:false},
      windowsAdmins:[],testContourOperators:['CORP\\\\alice'],localUsername:'admin',logRetentionDays:14};
  `,ctx);
  return {ctx,content,listeners,Form};
}

test('settings renders the shared test-contour role as an editable AD account list',()=>{
  const {ctx,content}=context();
  vm.runInContext('settingsPage()',ctx);
  assert.match(content.innerHTML,/name="testContourOperators"/);
  assert.match(content.innerHTML,/Обновление тестового контура \(_tk\)/);
  assert.match(content.innerHTML,/CORP\\alice/);
  assert.match(content.innerHTML,/Общий список для всех баз/);
  vm.runInContext(`state.settings.testContourOperators=['CORP\\\\<unsafe>'];settingsPage()`,ctx);
  assert.match(content.innerHTML,/&lt;unsafe&gt;/);
  assert.doesNotMatch(content.innerHTML,/<unsafe>/);
});

test('saving and clearing the role includes its list in the security API request',async()=>{
  const {ctx,listeners,Form}=context();
  const requests=[];
  ctx.capture=(url,options)=>{requests.push({url,body:JSON.parse(options.body)});};
  vm.runInContext(`api=async(url,options)=>capture(url,options);busy=async(form,action)=>action();
    session=async()=>{};navigate=async()=>{};toast=()=>{}`,ctx);
  const form=new Form();
  form.id='security-form';form.method='post';
  form.elements={windowsAdmins:{value:''},newPassword:{value:''},windowsEnabled:{checked:true},
    windowsAutoSignIn:{checked:false},testContourOperators:{value:'CORP\\alice\r\nCORP\\bob'}};
  await listeners.submit({target:form,preventDefault(){}});
  assert.equal(requests[0].url,'/api/admin/security');
  assert.deepEqual(requests[0].body.testContourOperators,['CORP\\alice','CORP\\bob']);
  form.elements.testContourOperators.value='';
  await listeners.submit({target:form,preventDefault(){}});
  assert.deepEqual(requests[1].body.testContourOperators,['']);
});

test('database list identifies shared test contour and keeps developer owner labels',()=>{
  const {ctx,content}=context();
  vm.runInContext(`state.databases={configured:true,databases:[
    {name:'uho_ke_tk',sourceKey:'uho_ke',ownerKey:'',state:'ONLINE',isTestContour:true},
    {name:'uho_ke_dev_alice',sourceKey:'uho_ke',ownerKey:'alice',state:'ONLINE',isTestContour:false}]};
    databasePage()`,ctx);
  assert.match(content.innerHTML,/uho_ke_tk/);
  assert.match(content.innerHTML,/<small>Тестовый контур<\/small>/);
  assert.match(content.innerHTML,/<small>alice<\/small>/);
});

test('settings exposes DT catalogs, IBCMD location, credentials, timeout and a harmless probe',()=>{
  const {ctx,content}=context();
  vm.runInContext(`state.settings.connection.dtRestore={enabled:true,backupDirectory:'D:\\\\dt',
    ibcmdDirectory:'C:\\\\1cv8\\\\bin',workingDirectory:'',infobaseUser:'Administrator',
    infobasePasswordConfigured:true,sqlUsername:'',sqlPasswordConfigured:false,timeoutMinutes:240};settingsPage()`,ctx);
  for(const name of ['dtEnabled','dtBackupDirectory','ibcmdDirectory','ibcmdWorkingDirectory',
    'ibcmdInfobaseUser','ibcmdInfobasePassword','ibcmdClearInfobasePassword','ibcmdSqlUsername','ibcmdSqlPassword','ibcmdTimeoutMinutes'])
    assert.match(content.innerHTML,new RegExp(`name="${name}"`));
  assert.match(content.innerHTML,/data-action="test-ibcmd"/);
  assert.match(content.innerHTML,/name="ibcmdTimeoutMinutes"[^>]*value="240"/);
});

test('database list displays a DT archive when no BAK is available',()=>{
  const {ctx,content}=context();
  vm.runInContext(`state.databases={configured:true,databases:[{name:'uho_ke_tk',sourceKey:'uho_ke',ownerKey:'',
    state:'ONLINE',isTestContour:true,dtBackup:{name:'uho_ke.dt',modifiedAt:'2026-11-18T12:00:00Z',size:123}}]};databasePage()`,ctx);
  assert.match(content.innerHTML,/uho_ke\.dt/);assert.match(content.innerHTML,/\.dt \/ IBCMD/);
  assert.doesNotMatch(content.innerHTML,/Файл не найден/);
});

test('post-restore settings assign a domain checkbox and value to an exact database and archive type',()=>{
  const {ctx,content}=context();
  vm.runInContext(`state.settings.restorePipelines=[{server:'DEV-SQL',database:'bp_dev_owner',sourceKind:'dt',
    operations:[{kind:'windows-domain',targetDomain:'CORP'}]}];settingsPage()`,ctx);
  for(const name of ['database','sourceKind','rewriteDomain','targetDomain'])
    assert.match(content.innerHTML,new RegExp(`name="${name}"`));
  for(const kind of ['full','shrink','dt','database'])
    assert.match(content.innerHTML,new RegExp(`<option value="${kind}">`));
  assert.match(content.innerHTML,/data-pipeline-edit="0"/);
  assert.match(content.innerHTML,/bp_dev_owner/);
});

test('pipeline submit sends checkbox state, target domain and archive type',async()=>{
  const {ctx,listeners,Form}=context();const requests=[];
  ctx.capture=(url,options)=>requests.push({url,body:JSON.parse(options.body)});
  vm.runInContext('api=async(url,options)=>capture(url,options);busy=async(form,action)=>action();navigate=async()=>{};toast=()=>{}',ctx);
  const form=new Form();form.id='restore-pipeline-form';form.method='post';
  form.elements={database:{value:' bp_dev_owner '},sourceKind:{value:'shrink'},rewriteDomain:{checked:true},targetDomain:{value:' CORP '}};
  await listeners.submit({target:form,preventDefault(){}});
  assert.deepEqual(requests[0],{url:'/api/admin/restore-pipelines',body:{database:'bp_dev_owner',sourceKind:'shrink',rewriteDomain:true,targetDomain:'CORP'}});
  form.elements.rewriteDomain.checked=false;
  await listeners.submit({target:form,preventDefault(){}});
  assert.equal(requests[1].body.rewriteDomain,false);
});
