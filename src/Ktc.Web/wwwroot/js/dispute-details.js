// Dispute Details, laid out like KTC's Windows client (bank screenshots): KTC.disputeDetails(id, atmId, onChanged) opens a modal with the
// tabs "Dispute Information" (dispute fields with Status "Change", Customer Info, Extra Data, Comments), "Transaction" (the dispute's
// transaction: details, EJ, session, fields, dispense, as in the transaction popup), "Queued SMS" and "All KTC fields". Add Comment and Change open small dialogs like the client's; the server checks everything, sends
// only when DisputeWrite is on, reads the dispute back and audits each attempt. Data: GET /data/disputes/{id}/details and /write.
// KTC.disputeColumns() and KTC.disputeStateOptions() give the list columns and the "Dispute states filter" of the client's Disputes tab.
// Card numbers arrive masked from the server; nothing here ever sees a full number.
(function(){const K=window.KTC;if(!K)return;let modal=null,cur=null;const $id=s=>document.getElementById(s);
 const pad=n=>String(n).padStart(2,'0');
 const ktcDate=u=>{if(!u||String(u).startsWith('0001'))return '';const d=new Date(/Z$|[+-]\d\d:\d\d$/.test(u)?u:u+'Z');return `${pad(d.getDate())} ${d.toLocaleString('en-GB',{month:'short'})} ${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;};
 K.ktcDate=K.ktcDate||ktcDate;
 const isDate=v=>typeof v==='string'&&/^\d{4}-\d\d-\d\dT\d\d:\d\d/.test(v);
 const showV=v=>v==null||v===''?'':K.esc(isDate(v)?ktcDate(v):v===true||v==='true'?'True':v===false||v==='false'?'False':v);
 const kv=(list,empty)=>list&&list.length?`<table class="table table-sm small mb-0"><thead><tr><th class="w-50">Name</th><th>Value</th></tr></thead><tbody>${list.map(x=>`<tr><td class="text-muted">${K.esc(x.name)}</td><td class="text-break">${showV(x.value)}</td></tr>`).join('')}</tbody></table>`:`<div class="text-muted small">${K.esc(empty||'Nothing returned.')}</div>`;

 // List columns of the client's Disputes tab: Dispute Id, Transaction Id, Created At, Client, Status, Dispute Rules, Last Comment, Closed.
 K.disputeColumns=(withAtmLink=true)=>[
  {data:'id',render:K.esc},
  {data:'transactionId',render:K.esc},
  {data:'createdUtc',render:(v,t)=>t==='display'?ktcDate(v):(v||'')},
  {data:'atmName',render:(v,t,r)=>t==='display'?(withAtmLink&&r.atmId?`<a href="/Atm/Detail/${encodeURIComponent(r.atmId)}" onclick="event.stopPropagation()">${K.esc(v||r.atmId)}</a>`:K.esc(v||r.atmId)):(v||r.atmId)},
  {data:'state',render:(v,t,r)=>t==='display'?`<span class="sbadge ${r.isClosed?'ok':'info'} text-nowrap">${K.esc(v)}</span>`:v},
  {data:'rule',render:(v,t)=>t==='display'?`<span class="d-inline-block text-truncate" style="max-width:22rem" title="${K.esc(v)}">${K.esc(v)}</span>`:v},
  {data:'lastComment',render:(v,t)=>t==='display'?`<span class="d-inline-block text-truncate" style="max-width:18rem" title="${K.esc(v)}">${K.esc(v)}</span>`:v},
  {data:'closedUtc',render:(v,t,r)=>t==='display'?(r.isClosed?(ktcDate(v)||'Closed'):'Not Closed'):(v||'')}];
 // "Dispute states filter": KTC's states in its order; state 0 is "All" (statusfilter 0 = all states). Added when KTC does not list it.
 K.disputeStateOptions=(states,keep)=>{const list=(states||[]).slice().sort((a,b)=>a.id-b.id);if(!list.some(s=>s.id===0))list.unshift({id:0,name:'All'});
  return list.map(s=>`<option value="${s.id}"${String(s.id)===String(keep??0)?' selected':''}>${K.esc(s.name||('State '+s.id))}</option>`).join('');};

 function shell(){
  if(modal)return modal;
  document.body.insertAdjacentHTML('beforeend',`<div class="modal fade" id="dsModal" tabindex="-1" aria-labelledby="dsTitle" aria-hidden="true"><div class="modal-dialog modal-xl modal-dialog-scrollable"><div class="modal-content position-relative">
   <div class="modal-header"><h5 class="modal-title" id="dsTitle">Dispute Details</h5><button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button></div>
   <div class="modal-body"><ul class="nav nav-tabs small" role="tablist">
     <li class="nav-item" role="presentation"><button class="nav-link active" data-bs-toggle="tab" data-bs-target="#dsInfo" type="button" role="tab">Dispute Information</button></li>
     <li class="nav-item" role="presentation"><button class="nav-link" data-bs-toggle="tab" data-bs-target="#dsTx" type="button" role="tab" id="dsTxTab">Transaction</button></li>
     <li class="nav-item" role="presentation"><button class="nav-link" data-bs-toggle="tab" data-bs-target="#dsSms" type="button" role="tab">Queued SMS <span class="badge text-bg-secondary d-none" id="dsSmsN"></span></button></li>
     <li class="nav-item" role="presentation"><button class="nav-link" data-bs-toggle="tab" data-bs-target="#dsAll" type="button" role="tab">All KTC fields</button></li>
    </ul><div class="tab-content pt-2"><div class="tab-pane fade show active" id="dsInfo" role="tabpanel"></div><div class="tab-pane fade" id="dsTx" role="tabpanel"></div><div class="tab-pane fade" id="dsSms" role="tabpanel"></div><div class="tab-pane fade" id="dsAll" role="tabpanel"></div></div></div>
   <div class="modal-footer"><span class="small text-muted me-auto" id="dsSrcD"></span><a class="btn btn-sm btn-outline-secondary" id="dsAtm" href="#"><i class="bi bi-hdd-rack"></i> Open ATM</a>
    <button type="button" class="btn btn-sm btn-outline-primary" id="dsAddBtn"><i class="bi bi-chat-left-dots"></i> Add Comment</button><button type="button" class="btn btn-sm btn-primary" data-bs-dismiss="modal">OK</button></div>
   <div id="dsDlg" class="d-none position-absolute top-0 start-0 w-100 h-100 d-flex align-items-center justify-content-center" style="background:rgba(0,0,0,.35);z-index:20;border-radius:inherit">
    <div class="card shadow" style="width:min(32rem,94%)" role="dialog" aria-modal="true" aria-labelledby="dsDlgTitle"><div class="card-header d-flex align-items-center"><b id="dsDlgTitle" class="small"></b><button type="button" class="btn-close ms-auto" aria-label="Cancel" data-dlg-cancel></button></div>
     <div class="card-body small" id="dsDlgBody"></div>
     <div class="card-footer d-flex gap-2 justify-content-end"><button type="button" class="btn btn-sm btn-outline-secondary" data-dlg-cancel>Cancel</button><button type="button" class="btn btn-sm btn-primary" id="dsDlgOk">OK</button></div></div></div>
  </div></div></div>`);
  modal=new bootstrap.Modal($id('dsModal'));
  $id('dsDlg').classList.remove('d-flex');
  $id('dsDlg').addEventListener('click',e=>{if(e.target.closest('[data-dlg-cancel]'))closeDlg();});
  $id('dsModal').addEventListener('keydown',e=>{if(e.key==='Escape'&&!$id('dsDlg').classList.contains('d-none')){e.stopPropagation();e.preventDefault();closeDlg();}},true);
  $id('dsAddBtn').onclick=()=>cur&&openDlg('comment');
  // Transaction tab: the dispute's transaction (details, EJ, session, fields, dispense), read from KTC the first time the tab is opened.
  $id('dsTxTab').addEventListener('shown.bs.tab',()=>{const x=cur&&cur.d&&cur.d.dispute;if(!x||cur.txLoaded)return;cur.txLoaded=true;
   if(!x.transactionId){$id('dsTx').innerHTML='<div class="small text-muted">This dispute has no transaction id in KTC.</div>';return;}
   if(!K.txPanel){$id('dsTx').innerHTML='<div class="small text-muted">Transaction details are not available on this page.</div>';return;}
   K.txPanel($id('dsTx'),{id:x.transactionId,atmId:x.atmId||cur.atmId,timeUtc:x.transactionUtc});});
  return modal;}

 const field=(l,v,btn)=>`<div class="row g-1 align-items-center mb-1"><div class="col-4 text-muted">${K.esc(l)}</div><div class="col${btn?'-6':''}"><div class="form-control form-control-sm bg-body-tertiary text-truncate" style="min-height:1.9rem" title="${K.esc(v||'')}">${K.esc(v||'')}</div></div>${btn?`<div class="col-2">${btn}</div>`:''}</div>`;

 async function render(){
  const {id,atmId}=cur;
  ['dsInfo','dsSms','dsAll'].forEach(x=>$id(x).innerHTML='<div class="text-muted small">Loading from KTC…</div>');
  let d;try{d=await K.api('/data/disputes/'+encodeURIComponent(id)+'/details?'+K.qs({atmId:atmId||''}));}
  catch(e){$id('dsInfo').innerHTML=`<div class="alert alert-danger small">${K.esc(e.message)}</div>`;return;}
  if(!cur||cur.id!==id)return;
  const x=d.dispute;cur.d=d;cur.atmId=x.atmId||atmId;
  cur.w=await K.api('/data/disputes/'+encodeURIComponent(id)+'/write?'+K.qs({atmId:cur.atmId||''})).catch(()=>null);
  $id('dsAtm').classList.toggle('d-none',!cur.atmId||!!cur.onAtmPage);$id('dsAtm').href='/Atm/Detail/'+encodeURIComponent(cur.atmId||'');
  const info=`<fieldset class="border rounded p-2 h-100"><legend class="float-none w-auto px-1 small fw-semibold mb-1">Dispute Information</legend>
    ${field('Dispute Id',x.id)}
    ${field('Transaction Id',x.transactionId)}
    ${field('Client Name',x.atmName||x.atmId)}${field('Dispute Rules',x.rule)}
    ${field('Dispute Status',x.state,'<button type="button" class="btn btn-sm btn-outline-secondary w-100" id="dsChange">Change</button>')}
    ${field('Added Time',ktcDate(x.createdUtc))}${field('Last updated on',ktcDate(x.updatedUtc))}${field('Closed Time',ktcDate(x.closedUtc))}</fieldset>`;
  const cust=`<fieldset class="border rounded p-2 h-100"><legend class="float-none w-auto px-1 small fw-semibold mb-1">Customer Info</legend>${kv(d.customer,'KTC sent no customer info on this dispute.')}</fieldset>`;
  const extra=`<fieldset class="border rounded p-2 h-100"><legend class="float-none w-auto px-1 small fw-semibold mb-1">Extra Data</legend>${kv(d.extraData,'KTC sent no extra data on this dispute.')}</fieldset>`;
  const com=`<fieldset class="border rounded p-2 mt-2"><legend class="float-none w-auto px-1 small fw-semibold mb-1">Comments</legend>${d.comments.length?`<div class="table-responsive"><table class="table table-sm small mb-0"><thead><tr><th>User</th><th>Time</th><th>Comment</th></tr></thead><tbody>${d.comments.map(c=>`<tr><td class="text-nowrap">${K.esc(c.user||'')}</td><td class="text-nowrap">${K.esc(ktcDate(c.utc))}</td><td class="text-break">${K.esc(c.text)}</td></tr>`).join('')}</tbody></table></div>`:'<div class="text-muted small">No comments on the KTC record.</div>'}</fieldset>`;
  $id('dsInfo').innerHTML=(d.warnings.length?`<div class="alert alert-warning py-1 small">${d.warnings.map(K.esc).join('<br>')}</div>`:'')
   +`<div class="row g-2 small"><div class="col-lg-5">${info}</div><div class="col-lg-4">${cust}</div><div class="col-lg-3">${extra}</div></div>${com}<div id="dsLastRes" class="mt-2"></div>`;
  $id('dsChange').onclick=()=>openDlg('state');
  $id('dsSms').innerHTML=d.notifications.length?d.notifications.map(n=>kv(n)).join('<hr>'):'<div class="small text-muted">No queued SMS for this dispute in KTC (POST SMSQueue/Disputes).</div>';
  $id('dsSmsN').textContent=d.notifications.length;$id('dsSmsN').classList.toggle('d-none',!d.notifications.length);
  $id('dsAll').innerHTML=`<div class="small text-muted mb-1">Every field of the KTC record. Card numbers masked, track / CVV / PIN data hidden.</div>${kv(d.fields)}`;
  $id('dsSrcD').textContent='From KTC: '+d.sources.join(', ');
  if($id('dsTx').classList.contains('active')&&!cur.txLoaded)$id('dsTxTab').dispatchEvent(new Event('shown.bs.tab'));
 }

 // ---------- Add Comment / Change Dispute State dialogs ----------
 function openDlg(what){
  const W=cur.w,x=cur.d&&cur.d.dispute;if(!x)return;
  const role=W&&W.userAllowed===false?'<div class="alert alert-warning py-1 mb-2"><i class="bi bi-lock"></i> Your portal role is not Operator or Admin, so the portal will refuse this. Add your user name to Portal:OperatorUsers (or Portal:AdminUsers) in the portal settings and sign in again.</div>':'';
  const off=W&&!W.enabled?'<div class="alert alert-info py-1 mb-2"><i class="bi bi-info-circle"></i> Writing to KTC disputes is off (<code>DisputeWrite:Enabled</code>). OK runs the checks; nothing is sent.</div>':'';
  const lim=W?` <span class="text-muted">(${W.minReasonLength}–${W.maxReasonLength} characters; card numbers are masked before sending)</span>`:'';
  const ta=`<label class="form-label mb-1" for="dsDlgText">Enter a comment <span class="text-danger">*</span>${lim}</label><textarea class="form-control form-control-sm" id="dsDlgText" rows="5" maxlength="${W?W.maxReasonLength:500}"></textarea>`;
  let body;
  if(what==='state'){
   const opts=(W?W.states:[]).filter(s=>s.id!==x.stateId);
   body=role+off+(W&&W.blockedReason?`<div class="text-muted mb-2"><i class="bi bi-lock"></i> ${K.esc(W.blockedReason)}</div>`:'')
    +`<div class="row g-1 align-items-center mb-2"><div class="col-5">Old Record State:</div><div class="col-7 fw-semibold">${K.esc(x.state)}</div></div>
     <div class="row g-1 align-items-center mb-2"><label class="col-5" for="dsDlgState">New Record State:</label><div class="col-7"><select class="form-select form-select-sm" id="dsDlgState"><option value=""></option>${opts.map(s=>`<option value="${s.id}">${K.esc(s.name||'State '+s.id)}</option>`).join('')}</select></div></div>${ta}`;
  }else body=role+off+ta;
  $id('dsDlgTitle').textContent=what==='state'?'Change Dispute State':'Add Comment';
  $id('dsDlgBody').innerHTML=body+'<div id="dsDlgRes" class="mt-2"></div>';
  $id('dsDlgOk').disabled=false;$id('dsDlgOk').onclick=()=>send(what);
  $id('dsDlg').classList.remove('d-none');$id('dsDlg').classList.add('d-flex');
  ($id('dsDlgState')||$id('dsDlgText')).focus();
 }
 function closeDlg(){$id('dsDlg').classList.add('d-none');$id('dsDlg').classList.remove('d-flex');}

 const resultBox=r=>`<div class="alert ${r.done?'alert-success':'alert-warning'} py-1 small mb-1">${K.esc(r.message||(r.done?'Done.':'KTC did not confirm the change.'))}${r.correlationId?` <span class="text-muted">· ref ${K.esc(r.correlationId)}</span>`:''}</div>
  <ul class="list-unstyled small mb-0">${(r.checks||[]).map(c=>`<li><i class="bi ${c.passed?'bi-check-circle text-success':'bi-x-circle text-danger'}"></i> <b>${K.esc(c.name)}</b>: ${K.esc(c.detail)}</li>`).join('')}</ul>
  ${(r.ktcSteps||[]).length?`<details class="small mt-1"${r.done?'':' open'}><summary>What KTC answered</summary><ol class="mb-0 font-monospace" style="font-size:.75rem">${(r.ktcSteps||[]).map(s=>`<li>${K.esc(s)}</li>`).join('')}</ol></details>`:''}`;
 async function send(what){
  const id=cur.id,text=$id('dsDlgText').value,st=$id('dsDlgState')?$id('dsDlgState').value:'';
  if(!text.trim()){$id('dsDlgRes').innerHTML='<div class="alert alert-warning py-1 small mb-0">Enter a comment.</div>';$id('dsDlgText').focus();return;}
  if(what==='state'&&!st){$id('dsDlgRes').innerHTML='<div class="alert alert-warning py-1 small mb-0">Pick the new record state.</div>';$id('dsDlgState').focus();return;}
  $id('dsDlgOk').disabled=true;
  try{const r=await K.api(`/data/disputes/${encodeURIComponent(id)}/${what==='state'?'state':'comment'}`,{json:{stateId:st?Number(st):null,reason:text,confirmDisputeId:null,atmId:cur.atmId||null}});
   if(!r||typeof r!=='object'||!('done' in r))throw new Error('The portal sent no result for this change. Nothing is known to have been saved in KTC: reload the dispute and check its comments.');
   if(r.done){K.toast(r.message,'ok');closeDlg();await render();if($id('dsLastRes'))$id('dsLastRes').innerHTML=resultBox(r);if(cur.onChanged)cur.onChanged();}
   else{$id('dsDlgRes').innerHTML=resultBox(r);if((r.ktcSteps||[]).length&&cur.onChanged)cur.onChanged();}
  }catch(e){$id('dsDlgRes').innerHTML=`<div class="alert alert-danger py-1 small mb-0">${K.esc(e.message)}</div>`;}
  if($id('dsDlgOk'))$id('dsDlgOk').disabled=false;
 }

 K.disputeDetails=function(id,atmId,onChanged,opts){
  if(!id)return;shell();cur={id:String(id),atmId,onChanged,onAtmPage:!!(opts&&opts.onAtmPage)};closeDlg();
  $id('dsTx').innerHTML='<div class="text-muted small">Loading from KTC…</div>';
  $id('dsTitle').textContent='Dispute Details · '+id;$id('dsSrcD').textContent='';
  bootstrap.Tab.getOrCreateInstance(document.querySelector('#dsModal [data-bs-target="#dsInfo"]')).show();modal.show();render();
 };
})();
