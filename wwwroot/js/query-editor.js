'use strict';
window.workbenchQueryEditor = ({connections, base = '/', demo = false}) => {
    const $ = s => document.querySelector(s);
    const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;'}[c]));
    $('#page-description').textContent = 'Write SQL, execute it against a connection, and inspect the results.';
    const content = $('#tool-content');
    if (!connections.length) {
        content.innerHTML = '<section class="panel"><div class="empty-state"><h2>Add a SQL Server connection</h2><p>Choose a database before executing SQL.</p><a class="button primary" href="?tool=connections">Add connection</a><a class="button" href="?tool=query&demo=true">Try demo</a></div></section>';
        return;
    }
    content.innerHTML = `<section class="panel"><div class="options-row"><div class="field"><label for="query-connection">Connection</label><select id="query-connection">${connections.map(c => `<option value="${esc(c.id)}">${esc(c.name)}</option>`).join('')}</select></div><div id="query-database" class="muted"></div></div><div class="panel-header"><h2>SQL editor</h2><div class="code-toolbar"><button class="button" id="query-download">Download .sql</button><button class="button" id="query-cancel" hidden>Cancel</button><button class="button primary" id="query-execute">Execute SQL</button></div></div><label class="query-label" for="query-sql">SQL command</label><textarea id="query-sql" class="query-editor" rows="10" maxlength="50000" spellcheck="false">SELECT DB_NAME() AS [Database], @@VERSION AS [Version];</textarea><div class="output-message">${demo ? 'Sample mode: the provided query returns fictitious database details. No SQL is sent to a server.' : 'Executes all SQL in the editor, including INSERT, UPDATE, DELETE and schema commands. Changes may commit immediately. Use explicit transactions in the same run when needed.'}</div><div class="output-message">Ctrl+Enter to execute · One SQL batch per run; GO separators are not supported · Each run opens a new connection; finish transactions within that run.</div></section><div id="query-status" role="status" aria-live="polite"></div><div id="query-results"></div>`;
    const editor = $('#query-sql'), selector = $('#query-connection'), execute = $('#query-execute'), cancel = $('#query-cancel');
    let controller = null;
    const showStatus = (text, error = false) => {
        $('#query-status').className = error ? 'error-note' : 'inline-note';
        $('#query-status').textContent = text;
    };
    const databaseInfo = () => {
        const c = connections.find(c => c.id === selector.value);
        $('#query-database').textContent = `${c.server} / ${c.database} · ${c.environment || ''}`;
        $('#query-results').replaceChildren();
        $('#query-status').replaceChildren();
    };
    selector.onchange = databaseInfo;
    databaseInfo();
    $('#query-download').onclick = () => {
        const url = URL.createObjectURL(new Blob(['\uFEFF', editor.value], {type:'application/sql;charset=utf-8'}));
        const link = document.createElement('a'); link.href = url; link.download = 'query.sql'; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    };
    cancel.onclick = () => controller?.abort();
    const render = result => {
        showStatus(result.error || `Completed in ${result.elapsedMilliseconds} ms${result.affectedRows >= 0 ? ` · ${result.affectedRows} affected rows` : ''}${demo ? ' · Sample results' : ''}`, !!result.error);
        $('#query-results').innerHTML = (result.truncated ? '<div class="inline-note">Result display limit reached. Rows, large cell values, or result sets were omitted; this does not limit which SQL statements execute.</div>' : '') +
            result.results.map((r, i) => `<section class="panel"><div class="panel-header"><h2>Result ${i + 1}</h2><span>${r.rows.length} rows retained</span></div><div class="query-result-grid table-wrap"><table><thead><tr>${r.columns.map(c => `<th>${esc(c.name)}<small class="query-column-type">${esc(c.type)}</small></th>`).join('')}</tr></thead><tbody>${r.rows.map(row => `<tr>${row.map(v => `<td>${v === null ? '<span class="muted">NULL</span>' : esc(v)}</td>`).join('')}</tr>`).join('')}</tbody></table>${!r.rows.length ? '<div class="empty-state">No rows returned.</div>' : ''}</div></section>`).join('') +
            `<section class="panel"><div class="panel-header"><h2>Messages</h2></div><pre class="query-messages">${esc(result.messages.join('\n') || (result.error ? 'Execution stopped.' : 'SQL execution completed.'))}</pre></section>`;
    };
    execute.onclick = async () => {
        if (controller) return;
        if (!editor.value.trim()) { showStatus('Enter a SQL command.', true); editor.focus(); return; }
        const query = editor.value, connection = selector.value;
        controller = new AbortController();
        execute.disabled = true; selector.disabled = true; editor.readOnly = true; cancel.hidden = false;
        $('#query-results').replaceChildren();
        showStatus('Executing SQL…');
        try {
            if (demo) {
                if (query.trim() !== 'SELECT DB_NAME() AS [Database], @@VERSION AS [Version];') throw new Error('Demo mode supports the provided sample query only. Open the live workspace to execute your own SQL.');
                render({results:[{columns:[{name:'Database',type:'nvarchar'},{name:'Version',type:'nvarchar'}],rows:[[connections.find(c => c.id === connection).database,'Microsoft SQL Server (sample)']]}],affectedRows:-1,messages:['Sample execution only; no database was contacted.'],truncated:false,elapsedMilliseconds:0,error:null});
            } else {
                const response = await fetch(base + 'api/query/execute', {method:'POST', credentials:'same-origin', signal:controller.signal, headers:{'Content-Type':'application/json','X-CSRF-TOKEN':$('meta[name=csrf-token]').content},body:JSON.stringify({connection,query})});
                const result = await response.json().catch(() => null);
                if (!response.ok) throw new Error(result?.title || 'Execution request failed.');
                render(result);
            }
        } catch (e) {
            showStatus((e.name === 'AbortError' ? 'Cancellation requested.' : e.message) + (demo ? '' : ' Earlier statements may have committed. Check database state before running again.'), true);
        } finally {
            controller = null; execute.disabled = false; selector.disabled = false; editor.readOnly = false; cancel.hidden = true;
        }
    };
    editor.onkeydown = e => { if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); execute.click(); } };
};
