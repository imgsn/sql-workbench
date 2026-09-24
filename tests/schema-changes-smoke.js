async page => {
const check=(v,m)=>{if(!v)throw new Error(m);};
await page.goto('http://localhost:5180/?tool=schema&demo=true');
const customer=page.locator('#results tbody>tr').filter({has:page.locator('td.object-name').filter({hasText:'dbo.Customers'})}).first();
check((await customer.innerText()).includes('nvarchar(255)')&&(await customer.innerText()).includes('nvarchar(150)'),'Inline demo datatype values');
check((await customer.innerText()).includes('NULL')&&(await customer.innerText()).includes('NOT NULL'),'Inline nullability values');
check(await customer.locator('mark').count()>0,'Changed portions highlighted');
const errors=[];page.on('pageerror',e=>errors.push(e.message));
const objects=[{id:1,schema:'dbo',name:'Customers',fullName:'dbo.Customers',kind:'Table',columns:[],properties:{}}];
await page.route('**/api/**',async route=>{
 const path=route.request().url().replace(/^https?:\/\/[^/]+/,'').split('?')[0];let data;
 if(path==='/api/connections')data=[{id:'test-a',name:'Test source',server:'fixture',database:'Source'},{id:'test-b',name:'Test target',server:'fixture',database:'Target'}];
 else if(path.endsWith('/schema')&&path.includes('/connections/'))data={objects};
 else if(path==='/api/profiles')data={enabled:false,profiles:[]};
 else if(path==='/api/compare/schema')data={sourceReadAt:new Date().toISOString(),targetReadAt:new Date().toISOString(),warnings:[],objects:[
 {name:'dbo.Customers',kind:'Table',status:'changed',differences:[{column:'Email',property:'Data type',source:'nvarchar(255)',target:'nvarchar(150)'},{column:'City',property:'Nullable',source:'True',target:'False'},{column:'—',property:'Definition',source:'CREATE VIEW dbo.Example AS\nSELECT 1 AS Value;',target:'CREATE VIEW dbo.Example AS\nSELECT 2 AS Value;'}]},
 {name:'dbo.NewTable',kind:'Table',status:'source',differences:[]},
 {name:'dbo.OldTable',kind:'Table',status:'target',differences:[]},
 {name:'dbo.Unchanged',kind:'Table',status:'same',differences:[]}
 ]};else return route.continue();
 await route.fulfill({json:data});
});
try {
 await page.goto('http://localhost:5180/?tool=schema');await page.waitForFunction(()=>document.querySelector('#tool-content')?.getAttribute('aria-busy')!=='true');
 await page.locator('#run-compare').click();await page.locator('#schema-rows').waitFor();
 check(await page.getByRole('columnheader',{name:'What changed',exact:true}).isVisible(),'Change column visible');
 const row=page.locator('#schema-rows tbody>tr').first();
 check((await row.innerText()).includes('nvarchar(255)')&&(await row.innerText()).includes('nvarchar(150)'),'Live inline values');
 check((await row.innerText()).includes('NOT NULL'),'Live nullability formatted');
 await row.locator('summary').click();check((await row.innerText()).includes('SELECT 2 AS Value'),'Expanded SQL definition');
 await page.locator('#result-search').fill('Email');check(await page.locator('#schema-rows tbody>tr').count()===1,'Search by changed column');
 await page.locator('#result-search').fill('');
 check((await page.locator('#schema-rows tbody>tr').nth(1).innerText()).includes('Missing'),'Object presence explained');
 await page.locator('[data-inspect]').first().click();check(await page.locator('#schema-detail .detail-panel').evaluate(e=>document.activeElement===e),'Inspect focuses detail');
 await page.setViewportSize({width:390,height:844});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Mobile has no page overflow');
 await page.setViewportSize({width:1440,height:1000});await page.screenshot({path:'preview-schema-changes.png',fullPage:true});
 check(!errors.length,errors.join(';'));
 return 'PASS: inline source/target values, change highlights, nullability, object presence, expandable definitions, column search, Inspect focus, and mobile layout.';
} finally {await page.unroute('**/api/**');await page.goto('http://localhost:5180/?tool=schema&demo=true');}
}
