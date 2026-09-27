async page => {
 const check=(v,m)=>{if(!v)throw new Error(m);};const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto('http://localhost:5180/?tool=schema&demo=true');
 await page.locator('[data-select-all]').click();await page.locator('[data-generate]').click();
 check((await page.locator('[data-migration-sql]').innerText()).includes('ALTER COLUMN [Email] nvarchar(255) NOT NULL'),'Demo direction');
 check((await page.locator('[data-migration-result]').innerText()).includes('Potential data loss'),'Demo risk labels');
 const downloaded=page.waitForEvent('download');await page.locator('[data-migration-download]').click();check((await downloaded).suggestedFilename()==='demo-schema-differences.sql','SQL download');
 await page.locator('[data-select-none]').click();check(await page.locator('[data-generate]').isDisabled(),'No empty selection');check(await page.locator('[data-migration-sql]').count()===0,'Selection clears stale SQL');
 let sent;const now=new Date().toISOString();
 await page.route('**/api/**',async route=>{
  const path=route.request().url().replace(/^https?:\/\/[^/]+/,'').split('?')[0];let data;
  if(path==='/api/connections')data=[{id:'a',name:'Dev',server:'fixture',database:'Source'},{id:'b',name:'Stage',server:'fixture',database:'Target'}];
  else if(path.includes('/connections/'))data={objects:[{id:1,schema:'dbo',name:'T',fullName:'dbo.T',kind:'Table',columns:[],properties:{}}]};
  else if(path==='/api/profiles')data={enabled:false,profiles:[]};
  else if(path==='/api/compare/schema')data={fingerprint:'revision-1',sourceReadAt:now,targetReadAt:now,warnings:[],objects:[{name:'dbo.T',kind:'Table',status:'changed',differences:[{column:'Name',property:'Data type',source:'nvarchar(100)',target:'nvarchar(20)'}]},{name:'dbo.Same',kind:'Table',status:'same',differences:[]}]};
  else if(path==='/api/scripts/differences'){sent=route.request().postDataJSON();data={sql:'-- draft\nALTER TABLE [dbo].[T] ALTER COLUMN [Name] nvarchar(100) NULL;',filename:'schema-differences.sql',objectCount:1,warnings:['INCOMPLETE: manual steps remain.'],steps:[{object:'dbo.T',change:'Alter column Name',risk:'destructive',note:'Review conversions'},{object:'dbo.T',change:'Index',risk:'manual',note:'Manual SQL required'}]};}
  else return route.continue();await route.fulfill({json:data});
 });
 try {
  await page.goto('http://localhost:5180/?tool=schema');await page.waitForFunction(()=>!document.querySelector('#tool-content').hasAttribute('aria-busy'));
  await page.locator('#run-compare').click();await page.locator('[data-select-all]').waitFor();
  check(await page.locator('[data-migrate]').count()===1,'Identical objects excluded');await page.locator('[data-select-all]').click();await page.locator('[data-generate]').click();await page.locator('[data-migration-sql]').waitFor();
  check(sent.fingerprint==='revision-1'&&sent.comparison.source==='a'&&sent.comparison.target==='b'&&sent.selectedObjects[0]===0,'Server revalidation payload');
  check((await page.locator('[data-migration-result]').innerText()).includes('INCOMPLETE'),'Partial drafts marked');
  const dl=page.waitForEvent('download');await page.locator('[data-migration-download]').click();check((await dl).suggestedFilename()==='schema-differences.sql','Live SQL filename');
  await page.setViewportSize({width:390,height:844});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Mobile no page overflow');
  await page.locator('#mode').selectOption('types');check(await page.locator('[data-migration-sql]').count()===0,'Settings invalidate previous draft');
  check(!errors.length,errors.join(';'));return 'PASS: demo/live migration selection, direction, risk/manual warnings, payload, SQL downloads, invalidation, mobile layout.';
 } finally {await page.unroute('**/api/**');await page.setViewportSize({width:1440,height:1000});await page.goto('http://localhost:5180/?tool=schema&demo=true');}
}
