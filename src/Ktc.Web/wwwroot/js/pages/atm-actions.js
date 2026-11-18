// ATM 360: Actions tab. Data: GET /data/actions/command-types (KTC GET api/v1/CommandType, PAN-masked, secret-looking values hidden).
// "Run a command": a form built from the command's parameter template (choice -> drop-down, bool -> two options, number, date,
// text, secret), then a confirm dialog (type the ATM id; high-risk commands also retype the command name and tick "I understand")
// that runs every server check (POST /data/actions/check) before Send (POST /data/actions/send). The server sends only when
// KtcActions:SendEnabled is on; reboot, resets, in/out of service and dual-authorization commands are refused by the server too.
(function(){const K=KTC,id=window.ATM_ID,$id=s=>document.getElementById(s);let loaded=false,last=null,cur=null,pending=null;
 const yn=v=>v==null?'<span class="text-muted">–</span>':v?'Yes':'No';
 const num=v=>v==null?'<span class="text-muted">–</span>':K.esc(v);
 const cls=a=>a.commandTypeId!=null?'ok':a.suggestions.length?'warn':'unknown';
 const label=c=>c.description&&c.description!==c.name?c.description:c.name;
 const typeName={choice:'choice',string:'text',bool:'yes / no',integer:'whole number',number:'number',secret:'secret',datetime:'date and time'};

 function planned(r){
  return r.planned.map(a=>`<div class="col-xl-4 col-md-6 col-12"><div class="border rounded p-2 h-100">
   <div class="d-flex align-items-center gap-2"><b>${K.esc(a.label)}</b><span class="sbadge ${cls(a)} ms-auto">${a.commandTypeId!=null?'Mapped':'Not mapped'}</span></div>
   <div class="small text-muted mb-1">${K.esc(a.description)}</div>
   <div class="small">${K.esc(a.status)}</div>
   ${a.commandTypeId!=null?`<div class="small">KTC command: <code>${K.esc(a.matchedName)}</code> #${K.esc(a.commandTypeId)}${a.requiresDualAuth?' <span class="sbadge warn">Needs second login</span>':''}</div>`:''}
   ${a.suggestions.length&&a.commandTypeId==null?`<div class="small">Looks like: ${a.suggestions.map(s=>`<code>${K.esc(s)}</code>`).join(', ')}</div>`:''}
   <button class="btn btn-sm btn-outline-danger mt-2" disabled title="Guided flow (out of service, wait for idle, reboot, back in service) comes next"><i class="bi bi-lock"></i> ${K.esc(a.label)}</button>
  </div></div>`).join('')
  +`<div class="col-12 small text-muted mt-1">Mapping is set in configuration, e.g. <code>KtcActions:Commands:Reboot = "&lt;CommandName&gt;"</code>. These commands are not offered in "Run a command".</div>`;
 }
 const fieldsSummary=c=>c.parameters.length?c.parameters.map(p=>`<span class="text-nowrap"><code>${K.esc(p.name)}</code> <span class="text-muted">${K.esc(typeName[p.type]||p.type)}${p.type==='choice'?': '+p.choices.map(K.esc).join(' / '):''}${p.required?'':' (optional)'}</span></span>`).join('<br>'):'<span class="text-muted">none</span>';
 function commands(r){
  if(!r.commands.length)return '<div class="text-muted small">No command types to show.</div>';
  return `<div class="table-responsive"><table class="table table-sm small mb-0 counter-table"><thead><tr><th class="text-end">ID</th><th>Command</th><th>Category</th><th>Dual auth</th><th>Risk</th><th>Form</th><th class="text-end">Max retries</th><th class="text-end">Max at once</th><th>Parameters (from the template)</th></tr></thead>
   <tbody>${r.commands.map(c=>`<tr><td class="text-end num">${K.esc(c.id)}</td><td><code>${K.esc(c.name)}</code><div class="text-muted">${K.esc(c.description)}</div></td><td>${K.esc(c.category)}</td>
    <td>${c.dualAuthMode==null?'<span class="text-muted">–</span>':c.requiresDualAuth?`<span class="sbadge warn text-nowrap">Yes (${K.esc(c.dualAuthMode)})</span>`:'No'}</td>
    <td>${c.highRisk?'<span class="sbadge crit">High</span>':''}</td><td>${c.sendBlockedReason?`<span class="text-muted" title="${K.esc(c.sendBlockedReason)}">Not offered</span>`:'Offered'}</td>
    <td class="text-end num">${num(c.maxRetries)}</td><td class="text-end num">${num(c.maxSimultaneous)}</td>
    <td>${fieldsSummary(c)}${c.parametersNote?`<div class="text-warning">${K.esc(c.parametersNote)}</div>`:''}${c.parameterTemplate?`<details><summary class="text-muted">Template</summary><code class="text-break">${K.esc(c.parameterTemplate)}</code></details>`:''}</td></tr>`).join('')}</tbody></table></div>`
   +(r.fields.length?`<details class="small text-muted mt-2"><summary>Fields KTC returned</summary>${r.fields.map(f=>`<code>${K.esc(f)}</code>`).join(' ')}</details>`:'');
 }

 // ---- command picker, grouped by KTC category like KTC's own "Add Action" menu ----
 function picker(r){
  const sel=$id('actCmd'),groups={};
  r.commands.forEach(c=>{(groups[c.category||'Other']=groups[c.category||'Other']||[]).push(c);});
  sel.innerHTML='<option value="">Choose a command…</option>'+Object.keys(groups).sort((a,b)=>a==='Other'?1:b==='Other'?-1:a.localeCompare(b)).map(g=>`<optgroup label="${K.esc(g)}">${
   groups[g].sort((a,b)=>label(a).localeCompare(label(b))).map(c=>`<option value="${c.id}"${c.sendBlockedReason?' disabled':''}>${K.esc(label(c))}${c.highRisk?' ⚠':''}${c.sendBlockedReason?' (not here)':''}</option>`).join('')}</optgroup>`).join('');
  sel.disabled=!r.commands.length;sel.value='';select(null);
 }
 function input(p){
  const n='actP_'+p.name.replace(/[^A-Za-z0-9_]/g,'_'),req=p.required?' required':'',lab=`<label class="form-label small mb-1" for="${n}">${K.esc(p.label)}${p.required&&p.type!=='bool'?' <span class="text-danger">*</span>':''}</label>`;
  const help=(p.help?`<div class="form-text">${K.esc(p.help)}</div>`:'')+(p.note?`<div class="form-text text-warning">${K.esc(p.note)}</div>`:'');
  let ctl;
  if(p.type==='choice')ctl=`<select class="form-select form-select-sm" id="${n}" data-p="${K.esc(p.name)}"${req}>${p.required&&!p.default?'<option value="">Choose…</option>':p.required?'':'<option value="">(none)</option>'}${p.choices.map(c=>`<option${String(c).toLowerCase()===String(p.default||'').toLowerCase()?' selected':''}>${K.esc(c)}</option>`).join('')}</select>`;
  else if(p.type==='bool'){const on=/^(true|1|yes)$/i.test(p.default||'');
   return `<div class="col-md-4 col-12"><div class="form-label small mb-1">${K.esc(p.label)}</div><div data-p="${K.esc(p.name)}" data-bool>
    <div class="form-check form-check-inline"><input class="form-check-input" type="radio" name="${n}" id="${n}_f" value="false"${on?'':' checked'}><label class="form-check-label small" for="${n}_f">${K.esc(p.falseText||'No')}</label></div>
    <div class="form-check form-check-inline"><input class="form-check-input" type="radio" name="${n}" id="${n}_t" value="true"${on?' checked':''}><label class="form-check-label small" for="${n}_t">${K.esc(p.trueText||'Yes')}</label></div></div>${help}</div>`;}
  else if(p.type==='integer'||p.type==='number')ctl=`<input type="number" class="form-control form-control-sm" id="${n}" data-p="${K.esc(p.name)}"${req}${p.type==='integer'?' step="1"':' step="any"'}${p.min!=null?` min="${p.min}"`:''}${p.max!=null?` max="${p.max}"`:''} value="${K.esc(p.default||'')}">`;
  else if(p.type==='datetime')ctl=`<input type="datetime-local" class="form-control form-control-sm" id="${n}" data-p="${K.esc(p.name)}"${req}>`;
  else if(p.type==='secret')ctl=`<input type="password" class="form-control form-control-sm" id="${n}" data-p="${K.esc(p.name)}" autocomplete="new-password"${req}>`;
  else ctl=`<input class="form-control form-control-sm" id="${n}" data-p="${K.esc(p.name)}"${req}${p.maxLength?` maxlength="${p.maxLength}"`:''} value="${K.esc(p.default||'')}">`;
  return `<div class="col-md-4 col-12">${lab}${ctl}${help}</div>`;
 }
 function select(c){
  cur=c;$id('actFormErr').textContent='';
  if(!c){$id('actForm').classList.add('d-none');$id('actCmdInfo').innerHTML='';return;}
  $id('actCmdInfo').innerHTML=`<code>${K.esc(c.name)}</code> #${K.esc(c.id)} · ${K.esc(c.category)}${c.highRisk?' · <span class="sbadge crit">High risk: extra confirmation</span>':''}`;
  $id('actFields').innerHTML=c.parameters.length?c.parameters.map(input).join(''):'<div class="col-12 small text-muted">This command has no parameters.</div>';
  $id('actForm').classList.remove('d-none');
 }
 function values(){
  const v={};
  $id('actFields').querySelectorAll('[data-p]').forEach(el=>{
   if(el.hasAttribute('data-bool')){const r=el.querySelector('input:checked');v[el.dataset.p]=r?r.value:'false';return;}
   let x=el.value;
   if(el.type==='datetime-local'&&x)x=new Date(x).toISOString();
   v[el.dataset.p]=x;});
  return v;
 }
 const shown=(p,v)=>p.type==='secret'?'••••••':p.type==='bool'?(v==='true'?(p.trueText||'Yes'):(p.falseText||'No'))+` <span class="text-muted">(${v})</span>`:p.type==='datetime'&&v?K.local(v):K.esc(v);
 function review(e){
  e.preventDefault();if(!cur)return;
  const f=$id('actForm');
  if(!f.checkValidity()||$id('actReason').value.trim().length<10){f.classList.add('was-validated');$id('actFormErr').textContent=$id('actReason').value.trim().length<10?'Give a reason of at least 10 characters.':'Fill in the required fields.';return;}
  const v=values();
  pending={atmId:id,commandTypeId:cur.id,parameters:v,ticketNumber:$id('actTicket').value.trim(),reason:$id('actReason').value.trim()};
  $id('actSummary').innerHTML=`<table class="table table-sm small mb-0"><tbody>
   <tr><th class="w-25">ATM</th><td>${K.esc($id('title').textContent||id)} <span class="text-muted">(${K.esc(id)})</span></td></tr>
   <tr><th>Command</th><td>${K.esc(label(cur))} <code>${K.esc(cur.name)}</code> #${K.esc(cur.id)}</td></tr>
   ${cur.parameters.map(p=>`<tr><th>${K.esc(p.label)}</th><td>${v[p.name]===''?'<span class="text-muted">(empty)</span>':shown(p,v[p.name])}</td></tr>`).join('')}
   <tr><th>Ticket</th><td>${K.esc(pending.ticketNumber)}</td></tr><tr><th>Reason</th><td>${K.esc(pending.reason)}</td></tr></tbody></table>`;
  $id('actRisk').classList.toggle('d-none',!cur.highRisk);
  $id('actRisk').innerHTML=cur.highRisk?`<b>High-risk command.</b> ${K.esc(label(cur))} can take the ATM out of use or change it. Retype the command name <code>${K.esc(cur.name)}</code> and tick the box.`:'';
  $id('actTypeCmdBox').classList.toggle('d-none',!cur.highRisk);$id('actAckBox').classList.toggle('d-none',!cur.highRisk);
  $id('actTypeAtm').value='';$id('actTypeCmd').value='';$id('actAck').checked=false;$id('actChecks').innerHTML='';
  sendBtn(false,last&&last.sendEnabled?'Send to ATM':'Sending is off');
  bootstrap.Modal.getOrCreateInstance($id('actConfirm')).show();
 }
 function sendBtn(on,text){const b=$id('actSendBtn');b.disabled=!on;b.innerHTML=`<i class="bi bi-send"></i> ${K.esc(text)}`;}
 const body=()=>Object.assign({},pending,{confirmAtmId:$id('actTypeAtm').value.trim(),confirmCommandName:$id('actTypeCmd').value.trim(),acknowledgedRisk:$id('actAck').checked});
 function checksHtml(r){
  return `<div class="alert ${r.sent?'alert-success':r.checks.every(c=>c.passed||c.name==='Sending enabled')?'alert-info':'alert-warning'} py-2 small mb-2">${K.esc(r.message)}${r.actionId?` <b>KTC action ${K.esc(r.actionId)}</b>`:''} <span class="text-muted">· ref ${K.esc(r.correlationId)}</span></div>
   <ul class="list-unstyled small mb-0">${r.checks.map(c=>`<li><i class="bi ${c.passed?'bi-check-circle text-success':'bi-x-circle text-danger'}"></i> <b>${K.esc(c.name)}</b>: ${K.esc(c.detail)}</li>`).join('')}</ul>`;
 }
 async function runChecks(){
  sendBtn(false,'Checking…');
  try{const r=await K.api('/data/actions/check',{json:body()});$id('actChecks').innerHTML=checksHtml(r);
   const ok=r.checks.every(c=>c.passed||c.name==='Sending enabled');
   sendBtn(ok&&last.sendEnabled,!last.sendEnabled?'Sending is off':ok?'Send to ATM':'Fix the checks first');}
  catch(e){$id('actChecks').innerHTML=`<div class="alert alert-danger py-2 small">${K.esc(e.message)}</div>`;sendBtn(false,'Send to ATM');}
 }
 async function send(){
  sendBtn(false,'Sending…');
  try{const r=await K.api('/data/actions/send',{json:body()});$id('actChecks').innerHTML=checksHtml(r);
   if(r.sent){K.toast(r.message);sendBtn(false,'Sent');$id('actReason').value='';}else sendBtn(false,'Not sent');}
  catch(e){$id('actChecks').innerHTML=`<div class="alert alert-danger py-2 small">${K.esc(e.message)}</div>`;sendBtn(false,'Not sent');}
 }

 function render(r){
  last=r;$id('actJson').disabled=!r.commands.length;
  $id('actMeta').innerHTML=`<i class="bi bi-hdd-network"></i> GET ${K.esc(r.route)} · read ${K.local(r.readUtc)}${r.demo?' · <span class="sbadge warn">Demo data</span>':''}`;
  $id('actMode').innerHTML=r.sendEnabled
   ?'<div class="alert alert-danger py-2 small"><i class="bi bi-broadcast"></i> <b>Sending is ON.</b> Commands confirmed here are sent to the ATM through KTC. Every attempt is audited with the ticket and reason.</div>'
   :'<div class="alert alert-info py-2 small"><i class="bi bi-info-circle"></i> <b>Sending is off.</b> You can fill in a command and run every check, but nothing is sent to the ATM. An administrator turns it on with <code>KtcActions:SendEnabled = true</code> once the real command list has been reviewed.</div>';
  $id('actCount').textContent=r.commands.length?`(${r.commands.length}${r.commands.some(c=>c.requiresDualAuth)?`, ${r.commands.filter(c=>c.requiresDualAuth).length} need a second login`:''})`:'';
  $id('actMsg').innerHTML=(r.warnings||[]).length?`<div class="alert alert-warning py-2 small">${r.warnings.map(K.esc).join('<br>')}</div>`:'';
  $id('actPlanned').innerHTML=planned(r);$id('actCommands').innerHTML=commands(r);picker(r);
 }
 async function load(refresh){
  loaded=true;$id('actCommands').innerHTML='<div class="text-muted small">Loading…</div>';
  try{render(await K.api('/data/actions/command-types'+(refresh?'?refresh=true':'')));}
  catch(e){$id('actMsg').innerHTML=`<div class="alert alert-danger small">Command list unavailable: ${K.esc(e.message)}</div>`;$id('actCommands').innerHTML='';}
 }
 // The same sanitised list the tab shows, for sharing with the team (no tokens, PANs masked).
 $id('actJson').onclick=()=>{if(!last)return;const b=new Blob([JSON.stringify({route:last.route,readUtc:last.readUtc,demo:last.demo,fields:last.fields,commands:last.commands},null,2)],{type:'application/json'});
  const a=document.createElement('a');a.href=URL.createObjectURL(b);a.download='ktc-command-types.json';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);};
 $id('actCmd').onchange=e=>select(last&&last.commands.find(c=>String(c.id)===e.target.value)||null);
 $id('actForm').onsubmit=review;
 $id('actCheckBtn').onclick=runChecks;$id('actSendBtn').onclick=send;
 ['actTypeAtm','actTypeCmd','actAck'].forEach(x=>$id(x).addEventListener('input',()=>sendBtn(false,last&&last.sendEnabled?'Run checks first':'Sending is off')));
 document.querySelector('[data-actrefresh]').onclick=()=>load(true);
 document.querySelector('[data-tab="actions"]').addEventListener('shown.bs.tab',()=>{if(!loaded)load(false);});
 if($id('tab-actions').classList.contains('active'))load(false);
})();
