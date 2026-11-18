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
