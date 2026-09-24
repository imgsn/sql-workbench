async page => {
const errors=[];page.on('pageerror',e=>errors.push(e.message));
const check=(v,m)=>{if(!v)throw new Error(m);};
const idle=()=>page.locator('#tool-content').waitFor({state:'visible'}).then(()=>page.waitForFunction(()=>document.querySelector('#tool-content')?.getAttribute('aria-busy')!=='true'));
await page.goto('http://localhost:5180/?tool=connections');await idle();
for(const [name,connection] of [['Browser Source',__SOURCE_CONNECTION__],['Browser Target',__TARGET_CONNECTION__]]) {
 await page.locator('#add-live').click();await page.getByLabel('Connection name',{exact:true}).fill(name);
 await page.getByLabel('Connection string',{exact:true}).fill(connection);
 await page.getByRole('button',{name:'Test & connect',exact:true}).click();
 await page.locator('#connection-dialog').waitFor({state:'hidden'});await idle();
 check(await page.getByRole('heading',{name,exact:true}).isVisible(),'Live connection added');
}
await page.getByRole('link',{name:'Schema compare',exact:true}).click();await idle();
await page.locator('#run-compare').click();await idle();
check(await page.locator('.summary-item.changed strong').innerText()==='1','Real schema differences');
await page.locator('[data-inspect]').first().click();
check((await page.locator('#schema-detail').innerText()).includes('nvarchar(80)'),'Live datatype detail');
await page.getByRole('link',{name:'Data compare',exact:true}).click();await idle();
await page.locator('#run-data').click();await idle();
check(await page.locator('.summary-item.changed strong').innerText()==='1','Real row difference');
await page.getByRole('link',{name:'Database explorer',exact:true}).click();await idle();
check((await page.locator('#object-details').innerText()).includes('CustomerId'),'Live explorer columns');
await page.getByRole('link',{name:'Script object',exact:true}).click();await idle();
await page.locator('#generate-schema').click();await idle();
check((await page.locator('#sql-output').innerText()).includes('CREATE TABLE'),'Live SMO preview');
const d1=page.waitForEvent('download');await page.locator('#download-sql').click();check((await d1).suggestedFilename()==='database-schema.sql','Schema download');
await page.getByRole('link',{name:'INSERT generator',exact:true}).click();await idle();
await page.locator('#query').fill('SELECT CustomerId, Name, City, Amount FROM dbo.Customers ORDER BY CustomerId;');
await page.locator('#preview-query').click();await idle();
check((await page.locator('#query-preview').innerText()).includes("O'Brien"),'Live query preview');
await page.locator('#identity').check();await page.locator('#generate-inserts').click();await idle();
check((await page.locator('#sql-output').innerText()).includes("N'O''Brien'"),'Live INSERT escaped');
const d2=page.waitForEvent('download');await page.locator('#download-sql').click();check((await d2).suggestedFilename()==='table-inserts.sql','INSERT download');
await page.locator('#query').fill('DELETE FROM dbo.Customers;');await page.locator('#preview-query').click();await idle();
check((await page.locator('#live-status').innerText()).includes('SELECT'),'Write query rejected');
await page.setViewportSize({width:390,height:844});
for(const tool of ['connections','schema','data','explorer','scripts','inserts']){
 await page.goto('http://localhost:5180/?tool='+tool);await idle();
 check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),tool+' mobile overflow');
}
await page.setViewportSize({width:1440,height:1000});await page.goto('http://localhost:5180/?tool=connections');await idle();
while(await page.locator('[data-remove]').count()){await page.locator('[data-remove]').first().click();await idle();}
check(!errors.length,'Browser errors: '+errors.join(';'));
await page.screenshot({path:'preview-live.png',fullPage:true});
return 'PASS: real SQL connections, schema/data comparison, explorer, SMO and INSERT .sql downloads, write rejection, six mobile layouts, session disconnects.';
}
