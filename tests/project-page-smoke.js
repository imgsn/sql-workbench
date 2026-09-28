async page => {
 const check=(v,m)=>{if(!v)throw new Error(m);};const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto('http://127.0.0.1:5181/');await page.setViewportSize({width:1440,height:1000});
 await page.getByRole('tab',{name:'Inspect',exact:true}).click();check((await page.getByRole('tabpanel').innerText()).includes('255 characters'),'Inspect example');
 await page.getByRole('tab',{name:'Export SQL',exact:true}).click();check((await page.getByRole('tabpanel').innerText()).includes('ALTER TABLE'),'SQL example');
 await page.keyboard.press('ArrowLeft');check(await page.getByRole('tab',{name:'Inspect',exact:true}).getAttribute('aria-selected')==='true','Keyboard tabs');
 check(await page.locator('img[src="assets/workbench.png"]').evaluate(e=>e.complete&&e.naturalWidth===1440),'Screenshot loads');
 await page.getByRole('tab',{name:'Compare',exact:true}).click();await page.screenshot({path:'preview-project-page.png',fullPage:true});
 for(const width of [390,768]){await page.setViewportSize({width,height:844});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Responsive '+width);}
 check(await page.locator('meta[property="og:image"]').getAttribute('content')==='https://imgsn.github.io/sql-workbench/assets/workbench.png','Share image');
 check(!errors.length,errors.join(';'));await page.setViewportSize({width:1440,height:1000});return 'PASS: tabs, keyboard navigation, screenshot, sharing metadata and responsive layout.';
}
