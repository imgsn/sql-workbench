async page => {
 await page.goto('http://localhost:5180/?tool=schema&demo=true');
 await page.setViewportSize({width:1440,height:980});
 await page.locator('[data-filter="changed"]').click();
 await page.screenshot({path:'docs/assets/workbench.png'});
 return 'Demo screenshot saved';
}
