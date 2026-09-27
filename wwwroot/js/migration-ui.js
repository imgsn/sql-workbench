(() => {
'use strict';
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
window.schemaMigration = {
 mount({host,items,generate,run=async action=>action(),source,target,demo=false,dataMode=false}) {
  const panel=document.createElement('section');panel.className='panel migration-panel';host.append(panel);
  panel.innerHTML=`<div class="panel-header"><div><h2>${dataMode?'Export data differences':'Generate SQL from differences'}</h2><p>${esc(source)} → ${esc(target)} · Make target match source${demo?' · Sample data':''}</p></div></div>
   <div class="panel-body"><p>${dataMode?'Select rows: source-only → INSERT, changed → UPDATE, target-only → DELETE.':'Select objects to include their differences under the chosen comparison mode.'} Generated SQL is for review and export only.</p>
   <div class="code-toolbar"><button class="button small" data-select-all>Select all differences</button><button class="button small" data-select-none>Clear selection</button><span data-count role="status"></span></div>
   <div class="object-checks selection-list">${items.map((o,i)=>o.status==='same'?'':`<label class="check"><input type="checkbox" data-migrate="${i}" />${esc(o.name)} <span class="muted">${esc(o.kind||'Table')} · ${dataMode?(o.status==='source'?'INSERT':o.status==='target'?'DELETE':'UPDATE'):(o.status==='source'?'Create':o.status==='target'?'Drop (data loss)':'Alter / review')}</span></label>`).join('')||'<p>No differences to script.</p>'}</div>
   ${dataMode?'<label class="check"><input type="checkbox" data-export-identity />Preserve identity values for INSERT (IDENTITY_INSERT)</label>':''}<button class="button primary" data-generate disabled>${dataMode?'Generate data SQL':'Generate migration draft'}</button>
   <div data-migration-error role="alert"></div><div data-migration-result></div></div>`;
  const $=s=>panel.querySelector(s), boxes=[...panel.querySelectorAll('[data-migrate]')];
  const invalidate=()=>{$('[data-migration-result]').innerHTML='';$('[data-migration-error]').textContent='';const n=boxes.filter(b=>b.checked).length;$('[data-count]').textContent=`${n} ${dataMode?'rows':'objects'} selected`;$('[data-generate]').disabled=!n;};
  boxes.forEach(b=>b.onchange=invalidate);
  if(dataMode) $('[data-export-identity]').onchange=invalidate;
  $('[data-select-all]').onclick=()=>{boxes.forEach(b=>b.checked=true);invalidate();};
  $('[data-select-none]').onclick=()=>{boxes.forEach(b=>b.checked=false);invalidate();};
  $('[data-generate]').onclick=()=>run(async()=>{
   $('[data-migration-result]').innerHTML='';$('[data-migration-error]').textContent='';
   try {
    const result=await generate(boxes.filter(b=>b.checked).map(b=>Number(b.dataset.migrate)),{includeIdentity:!!$('[data-export-identity]')?.checked});
    $('[data-migration-result]').innerHTML=`<div class="inline-note">${result.warnings.map(esc).join('<br>')}</div>
     <div class="table-wrap"><table><thead><tr><th>Object</th><th>Change</th><th>Review status</th><th>Details</th></tr></thead><tbody>${result.steps.map(s=>`<tr><td class="object-name">${esc(s.object)}</td><td>${esc(s.change)}</td><td><span class="badge ${s.risk==='destructive'?'target':s.risk==='manual'?'changed':'source'}">${s.risk==='destructive'?'Potential data loss':s.risk==='manual'?'Manual SQL required':'Review'}</span></td><td>${esc(s.note)}</td></tr>`).join('')}</tbody></table></div>
     <div class="panel-header"><h3>SQL preview${demo?' · Sample only':''}</h3><div class="code-toolbar"><button class="button" data-migration-copy>Copy SQL</button><button class="button primary" data-migration-download>Download .sql</button></div></div><pre class="code-pane" data-migration-sql tabindex="0" aria-label="Migration SQL"></pre>`;
    $('[data-migration-sql]').textContent=result.sql;
    $('[data-migration-copy]').onclick=async()=>{try{await navigator.clipboard.writeText(result.sql);$('[data-migration-copy]').textContent='Copied';}catch{$('[data-migration-error]').textContent='Clipboard unavailable. Use Download .sql.';}};
    $('[data-migration-download]').onclick=()=>{const url=URL.createObjectURL(new Blob(['\uFEFF',result.sql],{type:'application/sql;charset=utf-8'}));const a=document.createElement('a');a.href=url;a.download=result.filename;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
   } catch(e){$('[data-migration-error]').textContent=e.name==='AbortError'?'Generation cancelled.':e.message;}
  });
  invalidate();
 },
 demoData(rows,selected,key,columns) {
  const id=s=>'['+s.replaceAll(']',']]')+']';
  const literal=v=>v==null?'NULL':typeof v==='boolean'?(v?'1':'0'):typeof v==='number'?String(v):"N'"+String(v).replaceAll("'","''")+"'";
  const steps=selected.map(i=>{const r=rows[i],where=id(key)+' = '+literal((r.b||r.a)[key]);let sql,change;
   if(r.status==='source'){change='INSERT';const cols=[...new Set([key,...columns])];sql=`INSERT INTO [dbo].[Customers] (${cols.map(id).join(', ')}) VALUES (${cols.map(c=>literal(r.a[c])).join(', ')});`;}
   else if(r.status==='target'){change='DELETE';sql=`DELETE FROM [dbo].[Customers] WHERE ${where};`;}
   else {change='UPDATE';sql=`UPDATE [dbo].[Customers] SET ${columns.filter(c=>c!==key&&r.a[c]!==r.b[c]).map(c=>id(c)+' = '+literal(r.a[c])).join(', ')} WHERE ${where};`;}
   return {object:'Row '+(i+1)+' · '+String(r.key),change,risk:change==='INSERT'?'review':'destructive',sql,note:'Fictitious sample. Live exports include conflict checks and transaction rollback.'};
  });
  const warnings=['DEMO: simplified sample SQL using fictitious rows; not executed.','Source → target. Live exports recheck the comparison and guard target rows.'];
  return {steps,warnings,filename:'demo-data-differences.sql',sql:'-- '+warnings.join('\n-- ')+'\n\n'+steps.map(s=>'-- '+s.change+'\n'+s.sql).join('\n\n')};
 },
 demo(items,selected,target) {
  const id=s=>'['+s.replaceAll(']',']]')+']', qualified=s=>s.split('.').map(id).join('.');
  const steps=[];
  for(const index of selected){const x=items[index],table=qualified(x.name),add=(change,risk,sql,note)=>steps.push({object:x.name,change,risk,sql,note});
   if(x.status==='target')add('Drop table','destructive',`DROP TABLE ${table};`,'Permanently deletes the target table and its data.');
   else if(x.status==='source')add('Create table','review',`CREATE TABLE ${table} (\n${x.a.map(c=>'    '+id(c.name)+' '+c.type+(c.nullable?' NULL':' NOT NULL')+(c.extra?' '+c.extra:'')).join(',\n')}\n);`,'Sample definition. Review schemas and dependencies.');
   else for(const name of new Set(x.differences.map(d=>d.column))){const a=x.a.find(c=>c.name===name),b=x.b.find(c=>c.name===name),props=x.differences.filter(d=>d.column===name).map(d=>d.property);
    if(!a)add('Drop column '+name,'destructive',`ALTER TABLE ${table} DROP COLUMN ${id(name)};`,'Column values are permanently removed.');
    else {const desired=b?{...b,type:props.includes('Data type')?a.type:b.type,nullable:props.includes('Nullable')?a.nullable:b.nullable}:a;
     add((b?'Alter':'Add')+' column '+name,b?'destructive':'review',`ALTER TABLE ${table} ${b?'ALTER COLUMN':'ADD'} ${id(name)} ${desired.type}${desired.nullable?' NULL':' NOT NULL'};`,'Check existing data, defaults and dependent constraints before use.');}
   }
  }
  const warnings=['DEMO: fictitious schema and sample SQL; never applied to a database.','Direction: target is changed to match source. Review all dependencies and potential data loss.'];
  return {steps,warnings,filename:'demo-schema-differences.sql',sql:'-- '+warnings.join('\n-- ')+`\n-- Target: ${target}\n\n`+steps.map(s=>`-- ${s.risk.toUpperCase()}: ${s.object} / ${s.change}\n-- ${s.note}\n${s.sql}\nGO`).join('\n\n')};
 }
};
})();
