// Disputes, laid out like the Ticket Workbench. Data: GET /data/disputes (KTC POST api/v1/DisputeManagement/Search, states from
// GET DisputeState). Double-click (or Enter on) a row: Dispute Details popup from GET /data/disputes/{id}/details (KTC GET
// Dispute/{id} and its SMSQueue entries). Card numbers arrive masked from the server; nothing here ever sees a full number.
(function(){const K=KTC;let dt,tile=null;const f=document.getElementById('f'),$id=s=>document.getElementById(s);
 const pad=n=>String(n).padStart(2,'0');
 const ktcDate=u=>{if(!u)return '';const d=new Date(/Z$|[+-]\d\d:\d\d$/.test(u)?u:u+'Z');return `${pad(d.getDate())} ${d.toLocaleString('en-GB',{month:'short'})} ${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;};
 const dcol=k=>({data:k,render:(v,t)=>t==='display'?ktcDate(v):(v||'')});
 const money=(v,c)=>v==null?'':`${Number(v).toLocaleString(undefined,{minimumFractionDigits:2,maximumFractionDigits:2})}${c?' '+c:''}`;
 const stCls=r=>r.isClosed?'ok':/new|open/i.test(r.state)?'warn':'info';
 const kpi=(l,v,c,a)=>`<div class="kpi ${c}" ${a!=null?`data-t="${K.esc(a)}" role="button" tabindex="0" style="cursor:pointer;${tile===a?'outline:2px solid var(--s1)':''}"`:''}><div class="l">${K.esc(l)}</div><div class="v">${v}</div></div>`;
 const dayIso=(v,end)=>v?new Date(v+(end?'T23:59:59':'T00:00:00')).toISOString():'';
 let rows=[];

 async function load(){
  const p=Object.fromEntries(new FormData(f).entries());
  p.from=dayIso(p.from,false);p.to=dayIso(p.to,true);
  const r=await K.api('/data/disputes?'+K.qs(p));
  const sel=$id('fState'),keep=sel.value;
  sel.innerHTML='<option value="">All</option>'+r.states.map(s=>`<option value="${s.id}">${K.esc(s.name||('State '+s.id))} (${s.id})</option>`).join('');
  if([...sel.options].some(o=>o.value===keep))sel.value=keep;
  rows=r.data;
  const amt=r.totalAmount.filter(x=>x.amount!=null).map(x=>money(x.amount,x.currency)).join(' · ')||'—';
  $id('dsTiles').innerHTML=kpi('All open',r.open,'warn','open')+kpi('All closed',r.closed,'info','closed')+r.counts.slice(0,6).map(c=>kpi(c.name,c.count,'info','s:'+c.name)).join('')
   +kpi('All',r.data.length,'info','')+`<div class="kpi info"><div class="l">Disputed amount</div><div class="v" style="font-size:1rem">${K.esc(amt)}</div></div>`;
  $id('dsWarn').innerHTML=r.warnings.length?`<div class="alert alert-warning py-1 small">${r.warnings.map(K.esc).join('<br>')}</div>`:'';
  $id('dsSrc').textContent='From KTC: '+r.sources.join(', ')+(r.amountKnown?'':' · no amount field recognised in KTC\'s records');
  show();
 }
 function show(){
  const t=tile;const v=!t?rows:t==='open'?rows.filter(x=>!x.isClosed):t==='closed'?rows.filter(x=>x.isClosed):rows.filter(x=>'s:'+x.state===t);
  K.reload(dt,v);
  document.querySelectorAll('#dsTiles [data-t]').forEach(e=>e.style.outline=e.dataset.t===(t||'')&&t!==null?'2px solid var(--s1)':'');
 }
 dt=K.table('#tbl',{data:[],order:[[4,'desc']],createdRow:row=>{row.tabIndex=0;row.style.cursor='pointer';},columns:[
  {data:'id',render:K.esc},
  {data:'atmName',render:(v,t,r)=>t==='display'?(r.atmId?`<a href="/Atm/Detail/${encodeURIComponent(r.atmId)}" onclick="event.stopPropagation()">${K.esc(v||r.atmId)}</a>`:K.esc(v)):v},
  {data:'client',render:K.esc},
  {data:'state',render:(v,t,r)=>t==='display'?`<span class="sbadge ${stCls(r)} text-nowrap">${K.esc(v)}</span>${r.stateId!=null?` <span class="text-muted">(${r.stateId})</span>`:''}`:v},
  dcol('createdUtc'),dcol('transactionUtc'),{data:'transactionType',render:K.esc},
  {data:'amount',className:'text-end',render:(v,t,r)=>t==='display'?K.esc(money(v,r.currency)):v},
  {data:'card',render:v=>`<span class="font-monospace">${K.esc(v)}</span>`},
  {data:'customer',render:K.esc},
  {data:'reason',render:(v,t)=>t==='display'?`<span class="d-inline-block text-truncate" style="max-width:20rem" title="${K.esc(v)}">${K.esc(v)}</span>`:v},
  {data:'createdBy',render:K.esc},{data:'lastChangeBy',render:K.esc},dcol('updatedUtc'),dcol('closedUtc')]});
 const open=tr=>{const d=dt.row(tr).data();if(d)details(d.id,d.atmId);};
 $('#tbl tbody').on('dblclick','tr',function(){open(this);}).on('keydown','tr',function(e){if(e.key==='Enter')open(this);});

 // ---------- Dispute Details ----------
 const isDate=v=>typeof v==='string'&&/^\d{4}-\d\d-\d\dT\d\d:\d\d/.test(v);
 const showV=(v,empty)=>v==null||v===''?`<span class="text-muted">${K.esc(empty||'—')}</span>`:K.esc(isDate(v)?(v.startsWith('0001')?'':ktcDate(v)):v===true||v==='true'?'True':v===false||v==='false'?'False':v);
 const grid=(title,list)=>`<fieldset class="border rounded p-2 mb-2 h-100"><legend class="float-none w-auto px-1 small fw-semibold mb-0">${title}</legend>
   <dl class="row small mb-0">${list.map(([l,v,e])=>`<dt class="col-5 fw-normal text-muted">${K.esc(l)}</dt><dd class="col-7 mb-1">${showV(v,e)}</dd>`).join('')}</dl></fieldset>`;
 const kv=(list,empty)=>list&&list.length?`<table class="table table-sm small mb-0"><tbody>${list.map(x=>`<tr><th class="fw-normal text-muted w-25">${K.esc(x.name)}</th><td class="text-break">${showV(x.value)}</td></tr>`).join('')}</tbody></table>`:`<div class="text-muted small">${K.esc(empty||'Nothing returned.')}</div>`;
 const val=(list,...n)=>{for(const k of n){const x=(list||[]).find(f=>f.name.toLowerCase()===k.toLowerCase());if(x&&x.value!=='')return x.value;}return null;};
 async function details(id,atmId){
  ['dsInfo','dsCust','dsTx','dsHist','dsNotif','dsAll'].forEach(x=>$id(x).innerHTML='<div class="text-muted small">Loading from KTC…</div>');
  $id('dsTitle').textContent='Dispute Details · '+id;$id('dsSrcD').textContent='';
  $id('dsAtm').classList.toggle('d-none',!atmId);$id('dsAtm').href='/Atm/Detail/'+encodeURIComponent(atmId||'');
  bootstrap.Tab.getOrCreateInstance(document.querySelector('[data-bs-target="#dsInfo"]')).show();
  bootstrap.Modal.getOrCreateInstance($id('dsModal')).show();
  let d;try{d=await K.api('/data/disputes/'+encodeURIComponent(id)+'/details');}
  catch(e){$id('dsInfo').innerHTML=`<div class="alert alert-danger small">${K.esc(e.message)}</div>`;return;}
  const x=d.dispute,F=d.fields;
  const info=grid('Dispute Information',[['Dispute ID',x.id],['State',x.state+(x.stateId!=null?` (${x.stateId})`:'')],['Raised',x.createdUtc],['Raised By',x.createdBy],
   ['Last Change',x.updatedUtc],['Last Change By',x.lastChangeBy],['Closed',x.closedUtc,x.isClosed?'Closed':'Open'],['Reason',x.reason]]);
  const tx=grid('Disputed Transaction',[['ATM',x.atmName||x.atmId],['Bank',x.client],['Transaction ID',x.transactionId],['Session ID',val(F,'SessionID','SessionId')],
   ['Transaction Time',x.transactionUtc],['Transaction Type',x.transactionType],['Amount',x.amount==null?null:money(x.amount,x.currency)],['Card',x.card]]);
  const com=`<h6 class="small fw-semibold mt-2">Comments <span class="text-muted fw-normal">(card numbers masked)</span></h6>${d.comments.length?`<div class="table-responsive"><table class="table table-sm small mb-0"><thead><tr><th>User</th><th>Time</th><th>Comment</th></tr></thead><tbody>${d.comments.map(c=>`<tr><td class="text-nowrap">${K.esc(c.user||'')}</td><td class="text-nowrap">${K.esc(ktcDate(c.utc))}</td><td class="text-break">${K.esc(c.text)}</td></tr>`).join('')}</tbody></table></div>`:'<div class="text-muted small">No comments on the KTC record.</div>'}`;
  const W=await K.api('/data/disputes/'+encodeURIComponent(id)+'/write').catch(()=>null);
  const write=W?`<div class="border rounded p-2 mt-2" id="dsWrite"><h6 class="small fw-semibold mb-1"><i class="bi bi-arrow-left-right"></i> Change this dispute</h6>
   ${W.enabled?'':'<div class="alert alert-info py-1 small mb-2"><i class="bi bi-info-circle"></i> Writing to KTC disputes is off (<code>DisputeWrite:Enabled</code>). You can fill this in and run the checks; nothing is sent.</div>'}
   ${W.blockedReason?`<div class="small text-muted mb-2"><i class="bi bi-lock"></i> ${K.esc(W.blockedReason)}</div>`:''}
   <div class="row g-2 align-items-end">
    <div class="col-md-4"><label class="form-label small mb-1" for="dsNew">New state <span class="text-muted">(KTC's list)</span></label><select class="form-select form-select-sm" id="dsNew"><option value="">Pick…</option>${W.states.filter(s=>s.id!==x.stateId).map(s=>`<option value="${s.id}">${K.esc(s.name||'State '+s.id)} (${s.id})</option>`).join('')}</select></div>
    <div class="col-md-4"><label class="form-label small mb-1" for="dsConfirm">Type the dispute number to confirm</label><input class="form-control form-control-sm" id="dsConfirm" autocomplete="off"></div>
   </div>
   <label class="form-label small mb-1 mt-2" for="dsReason">Reason <span class="text-muted">(${W.minReasonLength}–${W.maxReasonLength} characters; card numbers are masked before sending)</span></label>
   <textarea class="form-control form-control-sm" id="dsReason" rows="2" maxlength="${W.maxReasonLength}"></textarea>
   <div class="d-flex flex-wrap gap-2 mt-2">
    <button class="btn btn-sm btn-primary" id="dsStateBtn"${W.canChangeState?'':' disabled'}><i class="bi bi-arrow-left-right"></i> Change state</button>
    <button class="btn btn-sm btn-danger" id="dsCloseBtn"${W.canClose?'':' disabled'}><i class="bi bi-check2-square"></i> Close dispute</button>
   </div><div id="dsWriteRes" class="mt-2"></div></div>`:'';
  $id('dsInfo').innerHTML=(d.warnings.length?`<div class="alert alert-warning py-1 small">${d.warnings.map(K.esc).join('<br>')}</div>`:'')
   +`<div class="row g-2"><div class="col-lg-6">${info}</div><div class="col-lg-6">${tx}</div></div>${com}${write}`;
  if(W){$id('dsStateBtn').onclick=()=>writeDispute(id,atmId,'state');$id('dsCloseBtn').onclick=()=>writeDispute(id,atmId,'close');}
  $id('dsCust').innerHTML=`<div class="small text-muted mb-1">Customer information KTC holds for this dispute. Card numbers masked, track data hidden.</div>${kv(d.customer,'KTC sent no customer details on this dispute.')}`
   +(d.extraData.length?`<h6 class="small fw-semibold mt-2">Extra data</h6>${kv(d.extraData)}`:'');
  $id('dsTx').innerHTML=`${grid('Disputed Transaction',[['Transaction ID',x.transactionId],['Transaction Time',x.transactionUtc],['Type',x.transactionType],['Amount',x.amount==null?null:money(x.amount,x.currency)],['Card',x.card]])}`
   +(d.transaction.length?`<h6 class="small fw-semibold mt-2">Transaction fields</h6>${kv(d.transaction)}`:'')+(d.session.length?`<h6 class="small fw-semibold mt-2">Session fields</h6>${kv(d.session)}`:'')
   +(x.transactionId?`<a class="btn btn-sm btn-outline-secondary mt-2" href="/Transactions?id=${encodeURIComponent(x.transactionId)}"><i class="bi bi-credit-card-2-front"></i> Open in Transactions</a>`:'');
  $id('dsHist').innerHTML=d.history.length?`<table class="table table-sm small"><thead><tr>${d.history[0].map(c=>`<th>${K.esc(c.name)}</th>`).join('')}</tr></thead><tbody>${d.history.map(h=>`<tr>${h.map(c=>`<td>${showV(c.value)}</td>`).join('')}</tr>`).join('')}</tbody></table>`
   :'<div class="small text-muted">The KTC record carries no state history. KTC\'s API has no separate history call for disputes.</div>';
  $id('dsNotif').innerHTML=d.notifications.length?d.notifications.map(n=>kv(n)).join('<hr>'):'<div class="small text-muted">No queued customer messages for this dispute in KTC (GET SMSQueue).</div>';
  $id('dsAll').innerHTML=`<h6 class="small fw-semibold">Dispute</h6>${kv(F)}`;
  $id('dsSrcD').textContent='From KTC: '+d.sources.join(', ');
 }

 // Change state / close: the server checks everything, sends only when DisputeWrite:Enabled is on, reads back, and audits each attempt.
 async function writeDispute(id,atmId,what){
  const btns=['dsStateBtn','dsCloseBtn'].map($id).filter(Boolean);const was=btns.map(b=>b.disabled);btns.forEach(b=>b.disabled=true);
  const s=$id('dsNew').value;const body={stateId:s?Number(s):null,reason:$id('dsReason').value,confirmDisputeId:$id('dsConfirm').value};
  try{const r=await K.api(`/data/disputes/${encodeURIComponent(id)}/${what}`,{json:body});
   const box=`<div class="alert ${r.done?'alert-success':'alert-warning'} py-1 small mb-1">${K.esc(r.message)} <span class="text-muted">· ref ${K.esc(r.correlationId)}</span></div>
    <ul class="list-unstyled small mb-0">${r.checks.map(c=>`<li><i class="bi ${c.passed?'bi-check-circle text-success':'bi-x-circle text-danger'}"></i> <b>${K.esc(c.name)}</b>: ${K.esc(c.detail)}</li>`).join('')}</ul>
    ${(r.ktcSteps||[]).length?`<details class="small mt-1"${r.done?'':' open'}><summary>What KTC answered</summary><ol class="mb-0 font-monospace" style="font-size:.75rem">${r.ktcSteps.map(x=>`<li>${K.esc(x)}</li>`).join('')}</ol></details>`:''}`;
   if(r.done)K.toast(r.message,'ok');
   if((r.ktcSteps||[]).length){await details(id,atmId);load().catch(()=>{});}
   if($id('dsWriteRes'))$id('dsWriteRes').innerHTML=box;
  }catch(e){if($id('dsWriteRes'))$id('dsWriteRes').innerHTML=`<div class="alert alert-danger py-1 small">${K.esc(e.message)}</div>`;}
  btns.forEach((b,i)=>{if(document.body.contains(b))b.disabled=was[i];});
 }

 // ---------- set-up (states and rules) ----------
 $id('dsSetup').addEventListener('toggle',async e=>{if(!e.target.open||e.target.dataset.done)return;e.target.dataset.done='1';
  try{const s=await K.api('/data/disputes/setup');
   const tbl=(list,empty)=>list.length?`<div class="table-responsive"><table class="table table-sm small mb-2"><thead><tr>${list[0].map(c=>`<th>${K.esc(c.name)}</th>`).join('')}</tr></thead><tbody>${list.map(r=>`<tr>${r.map(c=>`<td>${showV(c.value)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`:`<div class="text-muted">${empty}</div>`;
   $id('dsSetupBody').innerHTML=(s.warnings.length?`<div class="alert alert-warning py-1">${s.warnings.map(K.esc).join('<br>')}</div>`:'')
    +`<h6 class="small fw-semibold">States (GET DisputeState)</h6>${tbl(s.stateRecords,'KTC returned no dispute states.')}<h6 class="small fw-semibold">Rules (GET DisputeRule)</h6>${tbl(s.rules,'KTC returned no dispute rules.')}`;
  }catch(x){$id('dsSetupBody').innerHTML=`<div class="alert alert-danger py-1">${K.esc(x.message)}</div>`;}});

 const pick=e=>{const t=e.target.closest('[data-t]');if(!t)return;tile=t.dataset.t===''||tile===t.dataset.t?null:t.dataset.t;show();};
 $id('dsTiles').addEventListener('click',pick);$id('dsTiles').addEventListener('keydown',e=>{if(e.key==='Enter')pick(e);});
 $id('fState').onchange=()=>{tile=null;load().catch(x=>K.toast(x.message,'error'));};
 $id('fCard').addEventListener('input',e=>{e.target.value=e.target.value.replace(/\D/g,'').slice(0,4);});
 f.onsubmit=e=>{e.preventDefault();tile=null;load().catch(x=>K.toast(x.message,'error'));};
 load().then(()=>{const q=new URLSearchParams(location.search).get('disputeId');const r=q&&rows.find(x=>x.id===q);if(r)details(r.id,r.atmId);}).catch(e=>K.toast(e.message,'error'));
 setInterval(()=>{if(!document.hidden&&!$id('dsModal').classList.contains('show'))load().catch(()=>{});},60000);})();
