async (page) => {
    const origin = 'http://localhost:5182';
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.goto(origin + '/?tool=query&demo=true');
    await page.getByRole('button', { name: 'Execute SQL', exact: true }).click();
    await page.getByRole('heading', { name: 'Result 1', exact: true }).waitFor();
    await page.getByRole('textbox', { name: 'SQL command', exact: true }).fill('DELETE dbo.Customers;');
    await page.getByRole('button', { name: 'Execute SQL', exact: true }).click();
    if (!(await page.locator('#query-status').innerText()).includes('provided sample query only')) throw new Error('Demo guard');
    await page.goto(origin + '/?tool=query');
    await page.getByRole('heading', { name: 'Add a SQL Server connection', exact: true }).waitFor();
    // Live UI fixture; actual SQL execution is covered by Integration/Program.cs.
    await page.route('**/api/connections', route => route.fulfill({json:[{id:'fixture',name:'Test database',server:'localhost',database:'Workbench_Test',environment:'Test'}]}));
    let mode = 'result', release;
    await page.route('**/api/query/execute', async route => {
        if (route.request().postDataJSON().connection !== 'fixture') throw new Error('Wrong database');
        if (!route.request().headers()['x-csrf-token']) throw new Error('No CSRF token');
        if (mode === 'cancel') { await new Promise(resolve => release = resolve); await route.abort().catch(() => {}); return; }
        await route.fulfill({json:{results:[{columns:[{name:'Value',type:'nvarchar'}],rows:[['<script>bad</script>'],[null]]}],affectedRows:2,messages:['Changed two rows.'],truncated:false,elapsedMilliseconds:12,error:mode === 'error' ? 'SQL 102, line 1: Incorrect syntax.' : null}});
    });
    await page.reload();
    await page.getByRole('textbox', { name: 'SQL command', exact: true }).fill('SELECT 1;');
    await page.getByRole('textbox', { name: 'SQL command', exact: true }).press('Control+Enter');
    await page.getByRole('heading', { name: 'Result 1', exact: true }).waitFor();
    if (!(await page.locator('#query-status').innerText()).includes('2 affected rows')) throw new Error('Affected rows');
    if (await page.locator('#query-results script').count()) throw new Error('Unescaped SQL result');
    mode = 'error';
    await page.getByRole('button', { name: 'Execute SQL', exact: true }).click();
    await page.getByText('SQL 102, line 1: Incorrect syntax.', {exact:true}).waitFor();
    mode = 'cancel';
    await page.getByRole('button', { name: 'Execute SQL', exact: true }).click();
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.getByText(/Cancellation requested/).waitFor();
    release?.();
    if (!(await page.getByRole('button', {name:'Execute SQL',exact:true}).isEnabled())) throw new Error('Run stuck disabled');
    await page.unrouteAll({behavior:'wait'});
    await page.goto(origin + '/?tool=query&demo=true');
    await page.getByRole('button', {name:'Execute SQL',exact:true}).click();
    await page.getByRole('heading', {name:'Result 1',exact:true}).waitFor();
    await page.screenshot({path:'D:/Work/SfdaUnified/workbench/preview-query-editor.png',fullPage:true});
    if (errors.length) throw new Error(errors.join('\n'));
    return 'PASS: demo and live UI, empty state, keyboard shortcut, result escaping, errors, cancellation, connection selection and CSRF header.';
}
