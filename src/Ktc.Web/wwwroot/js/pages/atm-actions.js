// ATM 360: Actions tab, step 1 (read-only). Data: GET /data/actions/command-types (KTC GET api/v1/CommandType, PAN-masked,
// secret-looking values hidden). Buttons are always disabled: nothing is sent to the ATM until the real command ids are confirmed.
(function(){const K=KTC,$id=s=>document.getElementById(s);let loaded=false,last=null;
 const yn=v=>v==null?'<span class="text-muted">–</span>':v?'Yes':'No';
 const num=v=>v==null?'<span class="text-muted">–</span>':K.esc(v);
 const cls=a=>a.commandTypeId!=null?'ok':a.suggestions.length?'warn':'unknown';
 function planned(r){
  return r.planned.map(a=>`<div class="col-xl-4 col-md-6 col-12"><div class="border rounded p-2 h-100">
   <div class="d-flex align-items-center gap-2"><b>${K.esc(a.label)}</b><span class="sbadge ${cls(a)} ms-auto">${a.commandTypeId!=null?'Mapped':'Not mapped'}</span></div>
   <div class="small text-muted mb-1">${K.esc(a.description)}</div>
   <div class="small">${K.esc(a.status)}</div>
   ${a.commandTypeId!=null?`<div class="small">KTC command: <code>${K.esc(a.matchedName)}</code> #${K.esc(a.commandTypeId)}${a.requiresDualAuth?' <span class="sbadge warn">Needs second login</span>':''}</div>`:''}
   ${a.suggestions.length&&a.commandTypeId==null?`<div class="small">Looks like: ${a.suggestions.map(s=>`<code>${K.esc(s)}</code>`).join(', ')}</div>`:''}
   <button class="btn btn-sm btn-outline-danger mt-2" disabled title="Sending commands is not enabled yet"><i class="bi bi-lock"></i> ${K.esc(a.label)}</button>
  </div></div>`).join('')
  +`<div class="col-12 small text-muted mt-1">Mapping is set in configuration, e.g. <code>KtcActions:Commands:Reboot = "&lt;CommandName&gt;"</code>.</div>`;
 }
 function commands(r){
  if(!r.commands.length)return '<div class="text-muted small">No command types to show.</div>';
  return `<div class="table-responsive"><table class="table table-sm small mb-0 counter-table"><thead><tr><th class="text-end">ID</th><th>Command name</th><th>Category</th><th>Description</th><th>Dual auth</th><th>ATM can start</th><th class="text-end">Max retries</th><th class="text-end">Retry wait</th><th class="text-end">Max at once</th><th class="text-end">Target type</th><th>User defined</th><th>Parameter template</th></tr></thead>
   <tbody>${r.commands.map(c=>`<tr><td class="text-end num">${K.esc(c.id)}</td><td><code>${K.esc(c.name)}</code></td><td>${K.esc(c.category)}</td><td>${K.esc(c.description)}</td>
    <td>${c.dualAuthMode==null?'<span class="text-muted">–</span>':c.requiresDualAuth?`<span class="sbadge warn text-nowrap">Yes (${K.esc(c.dualAuthMode)})</span>`:'No'}</td><td>${yn(c.allowClientInitiation)}</td>
    <td class="text-end num">${num(c.maxRetries)}</td><td class="text-end num">${num(c.retryWaitTime)}</td><td class="text-end num">${num(c.maxSimultaneous)}</td><td class="text-end num">${num(c.targetType)}</td><td>${yn(c.isUserDefined)}</td>
    <td>${c.parameterTemplate?`<code class="text-break">${K.esc(c.parameterTemplate)}</code>`:'<span class="text-muted">–</span>'}${c.subCommands?`<div class="text-muted">Sub-commands: ${K.esc(c.subCommands)}</div>`:''}</td></tr>`).join('')}</tbody></table></div>`
   +(r.fields.length?`<details class="small text-muted mt-2"><summary>Fields KTC returned</summary>${r.fields.map(f=>`<code>${K.esc(f)}</code>`).join(' ')}</details>`:'');
 }
 function render(r){
  last=r;$id('actJson').disabled=!r.commands.length;
  $id('actMeta').innerHTML=`<i class="bi bi-hdd-network"></i> GET ${K.esc(r.route)} · read ${K.local(r.readUtc)}${r.demo?' · <span class="sbadge warn">Demo data</span>':''}`;
  $id('actCount').textContent=r.commands.length?`(${r.commands.length}${r.commands.some(c=>c.requiresDualAuth)?`, ${r.commands.filter(c=>c.requiresDualAuth).length} need a second login`:''})`:'';
  $id('actMsg').innerHTML=(r.warnings||[]).length?`<div class="alert alert-warning py-2 small">${r.warnings.map(K.esc).join('<br>')}</div>`:'';
  $id('actPlanned').innerHTML=planned(r);$id('actCommands').innerHTML=commands(r);
 }
 async function load(refresh){
  loaded=true;$id('actCommands').innerHTML='<div class="text-muted small">Loading…</div>';
  try{render(await K.api('/data/actions/command-types'+(refresh?'?refresh=true':'')));}
  catch(e){$id('actMsg').innerHTML=`<div class="alert alert-danger small">Command list unavailable: ${K.esc(e.message)}</div>`;$id('actCommands').innerHTML='';}
 }
 // The same sanitised list the tab shows, for sharing with the team (no tokens, PANs masked).
 $id('actJson').onclick=()=>{if(!last)return;const b=new Blob([JSON.stringify({route:last.route,readUtc:last.readUtc,demo:last.demo,fields:last.fields,commands:last.commands},null,2)],{type:'application/json'});
  const a=document.createElement('a');a.href=URL.createObjectURL(b);a.download='ktc-command-types.json';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);};
 document.querySelector('[data-actrefresh]').onclick=()=>load(true);
 document.querySelector('[data-tab="actions"]').addEventListener('shown.bs.tab',()=>{if(!loaded)load(false);});
 if($id('tab-actions').classList.contains('active'))load(false);
})();
