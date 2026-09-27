async page => {
 const check=(v,m)=>{if(!v)throw new Error(m);};const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto('http://localhost:5180/?tool=data&demo=true');
 await page.locator('[data-select-all]').click();await page.locator('[data-generate]').click();
 const sample=await page.locator('[data-migration-sql]').innerText();
 check(['INSERT INTO','UPDATE ','DELETE FROM'].every(s=>sample.includes(s)),'Demo all DML operations');
 let download=page.waitForEvent('download');await page.locator('[data-migration-download]').click();check((await download).suggestedFilename()==='demo-data-differences.sql','Demo download');
 await page.locator('[data-export-identity]').check();check(await page.locator('[data-migration-sql]').count()===0,'Options clear stale output');
 let sent,stale=false;const now=new Date().toISOString();
 const object={id:1,schema:'dbo',name:'T',fullName:'dbo.T',kind:'Table',columns:[{name:'Id',type:'int',primaryKey:true},{name:'Label',type:'nvarchar(100)'}],properties:{}};
 await page.route('**/api/**',async route=>{
  const path=route.request().url().replace(/^https?:\/\/[^/]+/,'').split('?')[0];let data;
  if(path==='/api/connections')data=[{id:'a',name:'Dev',server:'fixture',database:'Source'},{id:'b',name:'Stage',server:'fixture',database:'Target'}];
  else if(path.includes('/connections/'))data={objects:[object]};
  else if(path==='/api/compare/data')data={fingerprint:'data-revision-1',sourceReadAt:now,targetReadAt:now,columns:[{source:'Id',target:'Id'},{source:'Label',target:'Label'}],rows:[
   {key:[1],status:'changed',source:[1,'new'],target:[1,'old'],changedColumns:['Label']},
   {key:[2],status:'source',source:[2,'insert'],target:null,changedColumns:[]},
   {key:[3],status:'target',source:null,target:[3,'delete'],changedColumns:[]},
   {key:[4],status:'same',source:[4,'same'],target:[4,'same'],changedColumns:[]}]};
  else if(path==='/api/scripts/data-differences'){
   sent=route.request().postDataJSON();if(stale)return route.fulfill({status:409,json:{title:'Compared data changed. Run data comparison again.'}});
   data={sql:'-- SQL draft\nUPDATE [dbo].[T] SET [Label]=N\'new\' WHERE [Id]=1;',filename:'data-differences.sql',warnings:['Never executed.'],steps:[{object:'Row 1',change:'UPDATE',risk:'destructive',note:'Expected values checked.'}]};
  }else return route.continue();await route.fulfill({json:data});
 });
 try {
  await page.goto('http://localhost:5180/?tool=data');await page.waitForFunction(()=>!document.querySelector('#tool-content').hasAttribute('aria-busy'));
  await page.locator('#run-data').click();await page.locator('[data-migrate]').first().waitFor();
  check(await page.locator('[data-migrate]').count()===3,'Identical rows excluded');await page.locator('[data-migrate="0"]').check();await page.locator('[data-generate]').click();await page.locator('[data-migration-sql]').waitFor();
  check(sent.selectedRows.length===1&&sent.selectedRows[0]===0&&sent.fingerprint==='data-revision-1'&&!sent.includeIdentity,'Selected row and fingerprint sent');
  download=page.waitForEvent('download');await page.locator('[data-migration-download]').click();check((await download).suggestedFilename()==='data-differences.sql','Live download');
  await page.locator('[data-export-identity]').check();await page.locator('[data-generate]').click();await page.locator('[data-migration-sql]').waitFor();check(sent.includeIdentity,'Identity option sent');
  await page.locator('[data-filter="target"]').click();check(await page.locator('[data-migrate="0"]').isChecked(),'Selection survives display filtering');
  await page.setViewportSize({width:390,height:844});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Mobile no overflow');
  stale=true;await page.locator('[data-generate]').click();await page.locator('[data-migration-error]').filter({hasText:'Compared data changed'}).waitFor();check(await page.locator('[data-migration-sql]').count()===0,'Stale comparison blocks export');
  await page.locator('#ignore-case').check();check(await page.locator('.migration-panel').count()===0,'Changed comparison settings invalidate selection and SQL');
  check(!errors.length,errors.join(';'));return 'PASS: demo/live data difference selection, INSERT/UPDATE/DELETE samples, identity option, SQL downloads, stale-data rejection, filter retention and mobile layout.';
 } finally {await page.unroute('**/api/**');await page.setViewportSize({width:1440,height:1000});await page.goto('http://localhost:5180/?tool=data&demo=true');}
}
