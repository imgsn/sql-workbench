// Shared presentation for live and demo schema comparisons.
window.schemaChanges = (() => {
 const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 function format(value, property) {
  if (value === null || value === undefined) return 'Missing';
  if (value === '') return 'Empty';
  if (property === 'Nullable' && /^(true|false)$/i.test(value)) return String(value).toLowerCase() === 'true' ? 'NULL' : 'NOT NULL';
  if (/^(true|false)$/i.test(value)) return String(value).toLowerCase() === 'true' ? 'Yes' : 'No';
  return String(value);
 }
 function highlighted(source, target) {
  let start = 0, end = 0;
  while (start < Math.min(source.length, target.length) && source[start] === target[start]) start++;
  while (end < Math.min(source.length, target.length) - start && source[source.length - end - 1] === target[target.length - end - 1]) end++;
  const mark = text => escape(text.slice(0, start)) + (text.length - end > start ? '<mark>' + escape(text.slice(start, text.length - end)) + '</mark>' : '') + escape(end ? text.slice(-end) : '');
  return [mark(source), mark(target)];
 }
 function render(status, differences) {
  if (status === 'same') return '<span class="muted">No differences for the selected settings</span>';
  if (!differences.length) return `<div class="schema-presence"><span>Source: <strong>${status === 'source' ? 'Present' : 'Missing'}</strong></span><span>Target: <strong>${status === 'target' ? 'Present' : 'Missing'}</strong></span></div>`;
  return '<div class="schema-changes">' + differences.map(d => {
   const source = format(d.source, d.property), target = format(d.target, d.property);
   const [left, right] = highlighted(source, target);
   const values = `<div class="schema-values"><div class="schema-source"><span class="schema-side">Source</span><code>${left}</code></div><div class="schema-target"><span class="schema-side">Target</span><code>${right}</code></div></div>`;
   const long = Math.max(source.length, target.length) > 180 || /[\r\n]/.test(source + target);
   return `<div class="schema-change"><div class="schema-change-heading">${d.column && d.column !== '—' ? `<code>${escape(d.column)}</code><span> / </span>` : ''}<span>${escape(d.property)}</span></div>${long ? `<details class="schema-definition"><summary>Show source and target definitions</summary>${values}</details>` : values}</div>`;
  }).join('') + '</div>';
 }
 return { render };
})();

(() => {
'use strict';
const app = document.querySelector('.app-shell');
if (!app || app.dataset.demo !== 'true') return;
// Preserve demo mode for generated links as well as Razor navigation.
document.addEventListener('click', event => {
 const link=event.target.closest('a[href]'); if(!link)return;
 const url=new URL(link.href,location.href);
 if(url.origin===location.origin&&url.searchParams.has('tool')&&!url.searchParams.has('demo')) {url.searchParams.set('demo','true');link.href=url.href;}
},true);
const tool = app.dataset.tool, demo = window.workbenchDemo;
const $ = (s) => document.querySelector(s);
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const paths = {
 database:'M4 5c0-3 16-3 16 0s-16 3-16 0v14c0 3 16 3 16 0V5M4 12c0 3 16 3 16 0',
 folder:'M3 7V4h6l2 3h10v13H3Z', compare:'M4 7h16m-4-4 4 4-4 4M20 17H4m4-4-4 4 4 4',
 table:'M3 4h18v16H3ZM3 9h18M9 9v11M3 14h18', code:'m8 6-6 6 6 6m8-12 6 6-6 6m-3-15-2 18',
 file:'M5 3h9l5 5v13H5ZM14 3v6h5M8 13h8M8 17h5', shield:'m12 2 8 4v6c0 5-8 10-8 10S4 17 4 12V6Zm-4 10 3 3 5-6',
 help:'M9 8a3 3 0 1 1 4 3c-1 1-1 2-1 3m0 3v.1M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0',
 info:'M12 11v6m0-10v.1M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0',
 plus:'M12 5v14M5 12h14', download:'M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5', play:'m8 4 12 8-12 8Z', copy:'M8 8h12v13H8ZM16 8V3H3v13h5', arrow:'M4 12h16m-5-5 5 5-5 5', search:'M16 16l5 5M18 10A8 8 0 1 1 2 10a8 8 0 0 1 16 0', check:'m5 12 4 4 10-10'
};
const icon = (n) => `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${paths[n] || paths.table}"/></svg>`;
function icons() { document.querySelectorAll('[data-icon]').forEach(el => el.innerHTML = icon(el.dataset.icon)); }
let timer;
function toast(message) { $('#toast').textContent = message; $('#toast').classList.add('visible'); clearTimeout(timer); timer = setTimeout(() => $('#toast').classList.remove('visible'), 4000); }
function download(name, contents, mime = 'application/sql') { const url = URL.createObjectURL(new Blob(['\uFEFF', contents], {type:`${mime};charset=utf-8`})); const a = document.createElement('a'); a.href=url; a.download=name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); toast(`${name} downloaded`); }
const btn = (id, text, glyph, primary = false) => `<button id="${id}" class="button ${primary ? 'primary' : ''}">${glyph ? icon(glyph) : ''}${text}</button>`;
const labels = {changed:'Changed',source:'Source only',target:'Target only',same:'Identical'};
const badge = status => `<span class="badge ${status}">${labels[status]}</span>`;
const descriptions = {
 schema:'Find structural differences between your databases, down to the column.',
 data:'Match rows, inspect changed values, and see what’s missing on either side.',
 connections:'Your SQL Server connections, together in one workspace.',
 explorer:'Explore tables, columns, and keys before you start working.',
 scripts:'Turn database structures into readable, downloadable SQL.',
 inserts:'Turn query results into portable INSERT statements.'
};
$('#page-description').textContent = descriptions[tool];
const content = $('#tool-content');
function options(selected='dev') { return demo.connections.map(c => `<option value="${c.id}" ${c.id===selected?'selected':''}>${esc(c.name)}</option>`).join(''); }
function connectionInfo(id) { const c=demo.connections.find(x=>x.id===id); return `${c.server} / ${c.database}`; }
function pair(extra) { return `<section class="panel"><div class="connection-pair"><div class="connection-select"><div class="db-icon">${icon('database')}</div><label for="source">Source database</label><select id="source">${options()}</select><small id="source-info">${connectionInfo('dev')}</small></div><button class="swap" id="swap" aria-label="Swap source and target">${icon('compare')}</button><div class="connection-select target"><div class="db-icon">${icon('database')}</div><label for="target">Target database</label><select id="target">${options('stage')}</select><small id="target-info">${connectionInfo('stage')}</small></div></div><div class="options-row">${extra}<button id="run" class="button primary run">${icon('play')}Run comparison</button></div></section><div id="results"></div>`; }
function wirePair(run) { ['source','target'].forEach(id => $(`#${id}`).addEventListener('change',()=>{ $(`#${id}-info`).textContent=connectionInfo($(`#${id}`).value); run(); })); $('#swap').onclick=()=>{ const src=$('#source').value; $('#source').value=$('#target').value; $('#target').value=src; ['source','target'].forEach(id=>$(`#${id}-info`).textContent=connectionInfo($(`#${id}`).value)); run(); }; $('#run').onclick=()=>{run();toast('Comparison refreshed from sample data');}; }
function summary(items, noun) { return `<div class="result-summary"><div class="summary-item"><strong>${items.length}</strong><span>${noun} compared</span></div>${Object.keys(labels).map(status=>`<div class="summary-item ${status}"><strong>${items.filter(x=>x.status===status).length}</strong><span>${labels[status]}</span></div>`).join('')}</div>`; }
function tabs(filter) { return `<div class="tabs" aria-label="Filter results">${[['all','All results'],...Object.entries(labels)].map(([id,label])=>`<button class="tab ${filter===id?'active':''}" data-filter="${id}" aria-pressed="${filter===id}">${label}</button>`).join('')}</div>`; }
let schemaResults = [], schemaFilter='all', schemaSearch='', selectedObject='dbo.Customers';
function schemaCompare() {
 document.querySelector('.migration-panel')?.remove();
 const source=demo.schemas[$('#source').value], target=demo.schemas[$('#target').value], scope=$('#scope').value, mode=$('#mode').value;
 const names=[...new Set([...Object.keys(source),...Object.keys(target)])].sort();
 schemaResults=names.filter(name=>scope==='all'||(scope==='selected'?name===$('#table-scope').value:name.startsWith(scope+'.'))).map(name=>{
  const a=source[name],b=target[name]; const differences=[];
  if(a&&b) {
   for(const col of [...new Set([...a.map(c=>c.name),...b.map(c=>c.name)])]) {
    const left=a.find(c=>c.name===col), right=b.find(c=>c.name===col);
    if(!left||!right) { if(mode!=='types') differences.push({column:col,property:'Column',a:left?'Present':'Missing',b:right?'Present':'Missing'}); continue; }
    if(mode!=='names'&&left.type!==right.type) differences.push({column:col,property:'Data type',a:left.type,b:right.type});
    if(mode==='full'&&left.nullable!==right.nullable) differences.push({column:col,property:'Nullable',a:left.nullable?'NULL':'NOT NULL',b:right.nullable?'NULL':'NOT NULL'});
   }
  }
  return {name,a,b,differences,status:!b?'source':!a?'target':differences.length?'changed':'same'};
 });
 renderSchemaResults();
}
function renderSchemaResults() {
 const migrationPanel=document.querySelector('.migration-panel');
 const visible=schemaResults.filter(x=>(schemaFilter==='all'||x.status===schemaFilter)&&x.name.toLowerCase().includes(schemaSearch.toLowerCase()));
 $('#results').innerHTML=summary(schemaResults,'Tables')+`<section class="panel"><div class="panel-header"><div><h2>Comparison results <span class="filter-count">${schemaResults.filter(x=>x.status!=='same').length} differences</span></h2><p>Source and target are compared by schema and table name.</p></div><button id="export-report" class="button small">${icon('download')}Export report</button></div><div class="result-toolbar">${tabs(schemaFilter)}<input id="search-results" class="search" placeholder="Search objects…" aria-label="Search objects" value="${esc(schemaSearch)}" /></div><div class="table-wrap"><table><thead><tr><th>Object name</th><th>Type</th><th>Status</th><th>What changed</th><th></th></tr></thead><tbody>${visible.map(x=>`<tr><td class="object-name">${icon('table')}${esc(x.name)}</td><td class="muted">Table</td><td>${badge(x.status)}</td><td class="schema-changes-cell">${window.schemaChanges.render(x.status,x.differences.map(d=>({column:d.column,property:d.property,source:d.a,target:d.b})))}</td><td><button class="detail-link" data-detail="${esc(x.name)}">Inspect ${icon('arrow')}</button></td></tr>`).join('')}</tbody></table>${!visible.length?'<div class="empty-state">No matching objects. Try another filter or search.</div>':''}</div><div class="table-footer"><span>Showing ${visible.length} of ${schemaResults.length} tables</span><div class="legend"><span><i></i>Changed definition</span><span><i></i>Source only</span><span><i></i>Target only</span></div></div><div id="schema-detail"></div></section>`;
 document.querySelectorAll('[data-filter]').forEach(b=>b.onclick=()=>{schemaFilter=b.dataset.filter;renderSchemaResults();});
 $('#search-results').oninput=e=>{const position=e.target.selectionStart;schemaSearch=e.target.value;renderSchemaResults();$('#search-results').focus();$('#search-results').setSelectionRange(position,position);};
 document.querySelectorAll('[data-detail]').forEach(b=>b.onclick=()=>{selectedObject=b.dataset.detail;renderDetail();});
 $('#export-report').onclick=()=>download('schema-comparison.csv','Object,Status,Differences\r\n'+visible.map(x=>`${x.name},${labels[x.status]},${x.differences.length}`).join('\r\n'),'text/csv');
 if(visible.some(x=>x.name===selectedObject)) renderDetail();
 if(migrationPanel) $('#results').append(migrationPanel);
 else window.schemaMigration.mount({host:$('#results'),items:schemaResults,source:$('#source').selectedOptions[0].textContent,target:$('#target').selectedOptions[0].textContent,demo:true,generate:selected=>window.schemaMigration.demo(schemaResults,selected,$('#target').selectedOptions[0].textContent)});
}
function renderDetail() {
 const x=schemaResults.find(x=>x.name===selectedObject);if(!x)return;
 const differences=x.differences;
 $('#schema-detail').innerHTML=`<div class="detail-panel"><div class="detail-title"><h3><code>${esc(x.name)}</code> <span class="muted">/ Definition details</span></h3>${badge(x.status)}</div>${differences.length?`<div class="table-wrap"><table><thead><tr><th>Column</th><th>Property</th><th>Source · ${esc(demo.connections.find(c=>c.id===$('#source').value).environment)}</th><th>Target · ${esc(demo.connections.find(c=>c.id===$('#target').value).environment)}</th></tr></thead><tbody>${differences.map(d=>`<tr><td><code>${esc(d.column)}</code></td><td>${d.property}</td><td><code class="change-new">${esc(d.a)}</code></td><td><code class="change-old">${esc(d.b)}</code></td></tr>`).join('')}</tbody></table></div>`:`<p>${x.status==='same'?'This table matches for the selected comparison mode.':`This table exists only in the ${x.a?'source':'target'} database.`}</p><div class="mono">${(x.a||x.b).map(c=>esc(c.name)+' '+esc(c.type)).join(' &nbsp; · &nbsp; ')}</div>`}</div>`;
}
function schemaPage() {
 $('#page-actions').innerHTML=btn('save-profile','Save profile','file');
 content.innerHTML=pair(`<div class="field"><label for="scope">Comparison scope</label><select id="scope"><option value="all">Entire database</option><option value="dbo">Schema: dbo</option><option value="audit">Schema: audit</option><option value="selected">Selected table</option></select></div><div class="field" id="table-scope-field" hidden><label for="table-scope">Table</label><select id="table-scope">${Object.keys(demo.schemas.dev).map(n=>`<option>${n}</option>`).join('')}</select></div><div class="field"><label for="mode">Compare</label><select id="mode"><option value="full">Detailed definitions</option><option value="names">Names only</option><option value="types">Data types only</option><option value="both">Names & data types</option></select></div>`);
 wirePair(schemaCompare);$('#mode').onchange=schemaCompare;$('#table-scope').onchange=schemaCompare;$('#scope').onchange=()=>{$('#table-scope-field').hidden=$('#scope').value!=='selected';schemaCompare();};
 $('#save-profile').onclick=()=>download('schema-profile.json',JSON.stringify({demo:true,source:$('#source').value,target:$('#target').value,scope:$('#scope').value,table:$('#table-scope').value,mode:$('#mode').value},null,2),'application/json');schemaCompare();
}
let dataFilter='all';
function dataPage() {
 content.innerHTML=pair(`<div class="field"><label for="compare-table">Table</label><select id="compare-table"><option>dbo.Customers</option></select></div><div class="field"><label for="match-key">Match rows by</label><select id="match-key"><option>CustomerId</option><option>Email</option></select></div><label class="check"><input id="ignore-city" type="checkbox" />Ignore City</label>`);
 wirePair(renderData);$('#match-key').onchange=renderData;$('#ignore-city').onchange=renderData;renderData();
}
function renderData() {
 const exportContext=JSON.stringify([$('#source').value,$('#target').value,$('#match-key').value,$('#ignore-city').checked]);
 const previousExport=document.querySelector('.migration-panel');
 const a=demo.rows[$('#source').value],b=demo.rows[$('#target').value],key=$('#match-key').value;
 const cols=['Name','Email','City','IsActive'];const compareCols=cols.filter(c=>!($('#ignore-city').checked&&c==='City'));
 const keys=[...new Set([...a.map(r=>r[key]),...b.map(r=>r[key])])].sort();
 const rows=keys.map(k=>{const left=a.find(r=>r[key]===k),right=b.find(r=>r[key]===k);return {key:k,a:left,b:right,status:!right?'source':!left?'target':compareCols.some(c=>left[c]!==right[c])?'changed':'same'};});
 const visible=rows.filter(r=>dataFilter==='all'||r.status===dataFilter);
 $('#results').innerHTML=summary(rows,'Rows')+`<section class="panel"><div class="panel-header"><div><h2>Row differences <span class="filter-count">dbo.Customers</span></h2><p>Changed cells show source above target. This preview contains 7 rows per database.</p></div><button id="export-data" class="button small">${icon('download')}Export differences</button></div><div class="result-toolbar">${tabs(dataFilter)}</div><div class="table-wrap"><table><thead><tr><th>${key}</th><th>Status</th>${cols.filter(c=>c!==key).map(c=>`<th>${c}</th>`).join('')}</tr></thead><tbody>${visible.map(r=>`<tr><td class="mono">${esc(r.key)}</td><td>${badge(r.status)}</td>${cols.filter(c=>c!==key).map(c=>`<td class="comparison-cell">${r.a&&r.b&&r.a[c]!==r.b[c]&&compareCols.includes(c)?`<span class="change-new">${esc(display(r.a[c]))}</span><small><span class="change-old">${esc(display(r.b[c]))}</span></small>`:esc(display((r.a||r.b)[c]))}</td>`).join('')}</tr>`).join('')}</tbody></table></div>${!visible.length?'<div class="empty-state">No rows match this filter.</div>':''}<div class="table-footer"><span>Showing ${visible.length} of ${rows.length} matched keys</span><span>Source values in green · Target values in red</span></div></section>`;
 document.querySelectorAll('[data-filter]').forEach(b=>b.onclick=()=>{dataFilter=b.dataset.filter;renderData();});
 $('#export-data').onclick=()=>download('data-differences.json',JSON.stringify(rows.filter(r=>r.status!=='same'),null,2),'application/json');
 if(previousExport?.dataset.context===exportContext) $('#results').append(previousExport);
 else {window.schemaMigration.mount({host:$('#results'),items:rows.map((r,i)=>({name:'Row '+(i+1)+' · '+r.key,kind:'Row',status:r.status})),source:$('#source').selectedOptions[0].textContent,target:$('#target').selectedOptions[0].textContent,dataMode:true,demo:true,generate:selected=>window.schemaMigration.demoData(rows,selected,key,compareCols)});document.querySelector('.migration-panel').dataset.context=exportContext;}
}
function display(v) {return v===null?'NULL':typeof v==='boolean'?(v?'1':'0'):String(v);}
function readAdded() {try {const data=JSON.parse(sessionStorage.getItem('workbench-demo-connections')||'[]');return Array.isArray(data)?data.filter(x=>x&&typeof x.name==='string'&&typeof x.id==='string'):[];}catch{return [];}}
function connectionsPage() {
 $('#page-actions').innerHTML=btn('add-connection','Add connection','plus',true);
 const all=[...demo.connections,...readAdded()];
 content.innerHTML=`<div class="connection-grid">${all.map(c=>`<article class="connection-card"><div class="card-top"><div class="db-large">${icon('database')}</div><span class="badge ${c.environment==='Development'?'source':'changed'}">${esc(c.environment)}</span></div><h2>${esc(c.name)}</h2><p>${esc(c.server||'Sample server')}</p><div class="connection-meta"><div><small>Database</small><code>${esc(c.database||'Commerce')}</code></div><div><small>Engine</small>${esc(c.version||'Demo fixture')}</div></div><div class="card-actions">${c.id==='dev'||c.id==='stage'?`<a href="?tool=explorer&connection=${c.id}" class="button small">${icon('folder')}Explore database</a>`:'<span class="badge">Setup preview only</span>'}<button class="button small" data-test="${esc(c.id)}">Test demo</button>${c.id.startsWith('custom-')?`<button class="button small" data-remove="${esc(c.id)}">Remove</button>`:''}</div></article>`).join('')}<button class="add-card" id="add-card">${icon('plus')}<strong>Add a connection</strong><span>Paste a SQL Server connection string</span></button></div><div class="inline-note" style="margin-top:22px">Sample connections power the tools. Added connections demonstrate setup only and remain in this browser tab; no credentials are stored.</div>`;
 $('#add-connection').onclick=openConnection;$('#add-card').onclick=openConnection;
 document.querySelectorAll('[data-test]').forEach(b=>b.onclick=()=>toast('Demo validation complete. No SQL Server connection was attempted.'));
 document.querySelectorAll('[data-remove]').forEach(b=>b.onclick=()=>{try{sessionStorage.setItem('workbench-demo-connections',JSON.stringify(readAdded().filter(c=>c.id!==b.dataset.remove)));connectionsPage();toast('Demo connection removed');}catch{toast('Browser storage is unavailable');}});
}
function openConnection() {$('#connection-form').reset();$('#connection-feedback').textContent='';$('#connection-dialog').showModal();}
$('#connection-form').onsubmit=e=>{
 e.preventDefault();const f=new FormData(e.target), raw=String(f.get('connection'));const name=String(f.get('name')).trim();
 if(!name){$('#connection-feedback').textContent='Enter a connection name.';return;}
 // Only inspect recognized keys; never retain values from a pasted string.
 if(!/(?:^|;)\s*(Server|Data Source)\s*=/i.test(raw)||!/(?:^|;)\s*(Database|Initial Catalog)\s*=/i.test(raw)) {$('#connection-feedback').textContent='Include Server and Database fields in the sample connection string.';return;}
 const c={id:'custom-'+Date.now(),name,server:'Sample server (credentials discarded)',database:'Commerce demo',environment:f.get('environment')};
 try{sessionStorage.setItem('workbench-demo-connections',JSON.stringify([...readAdded(),c]));}catch{$('#connection-feedback').textContent='Browser session storage is unavailable.';return;}
 e.target.reset();$('#connection-dialog').close();connectionsPage();toast('Demo connection added. Credentials discarded.');
};
let explorerTable='dbo.Customers';
function explorerPage() {
 const initial=new URLSearchParams(location.search).get('connection')==='stage'?'stage':'dev';
 content.innerHTML=`<section class="panel"><div class="options-row"><div class="field"><label for="explorer-connection">Connection</label><select id="explorer-connection">${options(initial)}</select></div><p style="margin:0;font-size:12px">Browse the sample tables and their column definitions.</p></div></section><div class="split-layout"><section class="panel"><div class="panel-header"><h2>Database objects</h2></div><div id="object-tree" class="tree"></div></section><section class="panel" id="object-details"></section></div>`;
 $('#explorer-connection').onchange=renderExplorer;renderExplorer();
}
function renderExplorer() {
 const tables=demo.schemas[$('#explorer-connection').value];if(!tables[explorerTable])explorerTable=Object.keys(tables)[0];
 $('#object-tree').innerHTML=`<div class="tree-heading">Commerce / Tables (${Object.keys(tables).length})</div>`+Object.keys(tables).map(n=>`<button class="${explorerTable===n?'active':''}" data-object="${n}" aria-pressed="${explorerTable===n}">${icon('table')}${n}</button>`).join('');
 $('#object-details').innerHTML=`<div class="panel-header"><div><h2>${explorerTable}</h2><p>Table · ${tables[explorerTable].length} columns</p></div><a class="button small" href="?tool=scripts&connection=${$('#explorer-connection').value}&table=${encodeURIComponent(explorerTable)}">${icon('code')}Script table</a></div><div class="table-wrap"><table><thead><tr><th>Column</th><th>Data type</th><th>Nullable</th><th>Constraint</th></tr></thead><tbody>${tables[explorerTable].map(c=>`<tr><td class="object-name">${c.name}</td><td><code>${c.type}</code></td><td>${c.nullable?'Yes':'No'}</td><td>${c.extra?'<span class="badge source">Primary key</span>':'<span class="muted">—</span>'}</td></tr>`).join('')}</tbody></table></div><div class="output-message">Fixture metadata · No live server queried</div>`;
 document.querySelectorAll('[data-object]').forEach(b=>b.onclick=()=>{explorerTable=b.dataset.object;renderExplorer();});
}
let currentSql='';
function outputPanel(title='SQL preview') {return `<section class="panel"><div class="panel-header"><h2>${title}</h2><div class="code-toolbar">${btn('copy-sql','Copy','copy')}${btn('download-sql','Download .sql','download',true)}</div></div><pre class="code-pane" id="sql-output" tabindex="0" aria-label="Generated SQL"></pre><div class="output-message" id="sql-message">Generate a script to preview and download it.</div></section>`;}
function wireOutput(name) {
 $('#copy-sql').onclick=async()=>{try{await navigator.clipboard.writeText(currentSql);toast('SQL copied to clipboard');}catch{toast('Clipboard unavailable. Use Download .sql instead.');}};
 $('#download-sql').onclick=()=>download(name,currentSql);
 setSql('');
}
function setSql(sql,message='') {currentSql=sql;$('#sql-output').textContent=sql;$('#copy-sql').disabled=!sql;$('#download-sql').disabled=!sql;$('#sql-message').textContent=message||'Generate a script to preview and download it.';}
function header() {return `-- SQL Workbench | DEMO DATA ONLY\n-- Generated: ${new Date().toISOString()}\n-- Review before using. This application does not execute scripts.\n\n`;}
function quoteIdentifier(n) {return '['+n.replace(/]/g,']]')+']';}
function tableName(n) {return n.split('.').map(quoteIdentifier).join('.');}
function scriptsPage() {
 const params=new URLSearchParams(location.search),conn=params.get('connection')==='stage'?'stage':'dev';
 content.innerHTML=`<div class="editor-layout"><section class="panel settings-panel"><h2>Script settings</h2><label for="script-connection">Source database</label><select id="script-connection">${options(conn)}</select><label>Tables to include</label><div id="script-objects" class="object-checks"></div><label class="check"><input id="include-keys" type="checkbox" checked />Include primary keys</label><label class="check"><input id="include-go" type="checkbox" checked />Add GO separators</label><button class="button primary" id="generate-schema">${icon('code')}Generate schema</button><p style="font-size:11px;margin:16px 0 0">This preview scripts table columns, nullability, and primary keys from sample metadata.</p></section>${outputPanel()}</div>`;
 wireOutput('commerce-schema.sql');
 const renderObjects=()=>{$('#script-objects').innerHTML=Object.keys(demo.schemas[$('#script-connection').value]).map(n=>`<label class="check"><input type="checkbox" name="script-table" value="${n}" ${!params.get('table')||params.get('table')===n?'checked':''}/>${n}</label>`).join('');setSql('');};
 renderObjects();$('#script-connection').onchange=renderObjects;
 $('#script-objects').onchange=()=>setSql('');$('#include-keys').onchange=()=>setSql('');$('#include-go').onchange=()=>setSql('');
 $('#generate-schema').onclick=()=>{
  const tables=[...document.querySelectorAll('[name=script-table]:checked')].map(x=>x.value);if(!tables.length){setSql('','Select at least one table.');return;}
  const go=$('#include-go').checked?'\nGO\n':'\n';const schemas=[...new Set(tables.map(n=>n.split('.')[0]))].filter(n=>n!=='dbo');
  let sql=header()+schemas.map(n=>`IF SCHEMA_ID(N'${n}') IS NULL\n    EXEC(N'CREATE SCHEMA ${quoteIdentifier(n)}');${go}\n`).join('');
  sql+=tables.map(n=>`CREATE TABLE ${tableName(n)} (\n${demo.schemas[$('#script-connection').value][n].map(c=>`    ${quoteIdentifier(c.name)} ${c.type} ${c.nullable?'NULL':'NOT NULL'}${c.extra&&$('#include-keys').checked?' PRIMARY KEY':''}`).join(',\n')}\n);${go}`).join('\n');
  setSql(sql,`${tables.length} tables scripted · UTF-8 SQL file · Sample schema only`);
 };
 $('#generate-schema').click();
}
const querySamples={all:'SELECT * FROM dbo.Customers;',active:'SELECT * FROM dbo.Customers WHERE IsActive = 1;',riyadh:"SELECT * FROM dbo.Customers WHERE City = N'Riyadh';"};
function insertsPage() {
 content.innerHTML=`<div class="editor-layout"><section class="panel settings-panel"><h2>Export settings</h2><label for="insert-connection">Source database</label><select id="insert-connection">${options()}</select><label for="sample-query">Sample query</label><select id="sample-query"><option value="all">All customers</option><option value="active">Active customers</option><option value="riyadh">Customers in Riyadh</option></select><label for="destination">Destination table</label><input id="destination" value="dbo.Customers" spellcheck="false" /><label class="check"><input id="transaction" type="checkbox" checked />Wrap in a transaction</label><label class="check"><input id="identity" type="checkbox" />Use IDENTITY_INSERT</label><p style="font-size:11px">Use IDENTITY_INSERT only when CustomerId is an identity column in your destination.</p><button class="button primary" id="generate-inserts">${icon('play')}Preview & generate</button><div class="inline-note" style="margin-top:16px">The three sample queries are supported in this preview. No SQL is sent to a server.</div></section><div><section class="panel"><div class="panel-header"><h2>SELECT query</h2><span class="badge">Sample query editor</span></div><label for="query" class="skip-link">SQL query</label><textarea id="query" class="query-editor" spellcheck="false" aria-label="SQL SELECT query">${querySamples.all}</textarea><div id="query-error" role="alert"></div><div id="query-preview"></div></section>${outputPanel('Generated INSERT statements')}</div></div>`;
 wireOutput('customers-inserts.sql');
 const invalidate=()=>{setSql('');$('#query-preview').innerHTML='';$('#query-error').innerHTML='';};
 $('#sample-query').onchange=()=>{$('#query').value=querySamples[$('#sample-query').value];invalidate();};
 ['insert-connection','transaction','identity'].forEach(id=>$(`#${id}`).onchange=invalidate);$('#destination').oninput=invalidate;$('#query').oninput=invalidate;
 $('#generate-inserts').onclick=()=>{
  const normalized=s=>s.trim().replace(/;$/,'').replace(/\s+/g,' ').toLowerCase();const match=Object.keys(querySamples).find(k=>normalized(querySamples[k])===normalized($('#query').value));
  const dest=$('#destination').value.trim();
  if(!match||!/^[_a-zA-Z][_a-zA-Z0-9]*\.[_a-zA-Z][_a-zA-Z0-9]*$/.test(dest)) {invalidate();$('#query-error').innerHTML=`<div class="error-note">${!match?'This preview accepts the three sample SELECT queries only. Choose one from Sample query.':'Enter the destination as schema.Table using letters, numbers, and underscores.'}</div>`;return;}
  const rows=demo.rows[$('#insert-connection').value].filter(r=>match==='all'||(match==='active'?r.IsActive:r.City==='Riyadh'));const columns=Object.keys(rows[0]||demo.rows.dev[0]);
  $('#query-error').innerHTML='';
  $('#query-preview').innerHTML=`<div class="table-wrap"><table><thead><tr><th>CustomerId</th><th>Name</th><th>City</th><th>IsActive</th></tr></thead><tbody>${rows.map(r=>`<tr><td class="mono">${r.CustomerId}</td><td>${esc(r.Name)}</td><td>${esc(display(r.City))}</td><td>${display(r.IsActive)}</td></tr>`).join('')}</tbody></table></div><div class="output-message">${rows.length} sample rows · All ${columns.length} columns included in generated SQL</div>`;
  const literal=v=>v===null?'NULL':typeof v==='boolean'?(v?'1':'0'):typeof v==='number'?String(v):"N'"+v.replace(/'/g,"''")+"'";
  const target=tableName(dest),identity=$('#identity').checked,transaction=$('#transaction').checked;
  const sql=header()+(transaction?'BEGIN TRANSACTION;\n\n':'')+(identity?`SET IDENTITY_INSERT ${target} ON;\n\n`:'')+rows.map(r=>`INSERT INTO ${target} (${columns.map(quoteIdentifier).join(', ')})\nVALUES (${columns.map(c=>literal(r[c])).join(', ')});`).join('\n\n')+(identity?`\n\nSET IDENTITY_INSERT ${target} OFF;`:'')+(transaction?'\n\nCOMMIT TRANSACTION;':'')+'\n';
  setSql(sql,`${rows.length} INSERT statements · UTF-8 · Includes Unicode and NULL handling`);
 };
 $('#generate-inserts').click();
}
$('#help-button').onclick=()=>$('#help-dialog').showModal();
document.querySelectorAll('[data-close]').forEach(b=>b.onclick=()=>b.closest('dialog').close());
$('#connection-dialog').addEventListener('close',()=>$('#connection-form').reset());
({schema:schemaPage,data:dataPage,connections:connectionsPage,explorer:explorerPage,scripts:scriptsPage,inserts:insertsPage,query:()=>window.workbenchQueryEditor({connections:demo.connections,demo:true})}[tool])();
icons();
})();
