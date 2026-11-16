// Transaction details popup: KTC.txDetails({id, atmId, timeUtc}) opens a modal with the transaction record, its EJ lines, the session
// (session record, other transactions in the session, session fields), the STX transaction fields and the dispense details.
// Data: GET /data/transactions/{id}/details. KTC.txRowClicks(dataTable) opens it from a DataTables row click.
(function(){const K=window.KTC;if(!K)return;let modal=null,last=null;
 const n=v=>v==null?'–':Number(v).toLocaleString();
 function shell(){
  if(modal)return modal;
  document.body.insertAdjacentHTML('beforeend',`<div class="modal fade" id="txModal" tabindex="-1" aria-labelledby="txTitle" aria-hidden="true"><div class="modal-dialog modal-xl modal-dialog-scrollable"><div class="modal-content">
   <div class="modal-header"><h5 class="modal-title" id="txTitle">Transaction</h5><span class="ms-2" id="txBadge"></span><button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button></div>
   <div class="modal-body"><ul class="nav nav-tabs mb-3" role="tablist">
     ${[['details','info-circle','Details'],['ej','journal-text','EJ'],['session','people','Session'],['fields','list-columns','Fields'],['dispense','cash-stack','Dispense']].map(([k,i,l],x)=>`<li class="nav-item" role="presentation"><button class="nav-link${x?'':' active'}" data-bs-toggle="tab" data-bs-target="#tx-${k}" type="button" role="tab"><i class="bi bi-${i}"></i> ${l} <span class="badge text-bg-secondary ms-1 d-none" id="txc-${k}"></span></button></li>`).join('')}
    </ul><div class="tab-content">${['details','ej','session','fields','dispense'].map((k,x)=>`<div class="tab-pane fade${x?'':' show active'}" id="tx-${k}" role="tabpanel"></div>`).join('')}</div></div>
   <div class="modal-footer small text-muted justify-content-start" id="txFoot"></div></div></div></div>`);
  modal=new bootstrap.Modal(document.getElementById('txModal'));return modal;}
 const note=t=>`<div class="alert alert-secondary small mb-2"><i class="bi bi-info-circle"></i> ${K.esc(t)}</div>`;
 const kv=rows=>rows.length?`<table class="table table-sm small mb-0 counter-table"><tbody>${rows.map(f=>`<tr><th class="w-25 fw-normal text-muted">${K.esc(f.name)}${f.id&&f.id!==f.value?` <span class="xfs-class">${K.esc(f.id)}</span>`:''}</th><td class="text-break">${K.esc(f.value)}</td></tr>`).join('')}</tbody></table>`:'';
 const count=(k,v)=>{const b=document.getElementById('txc-'+k);b.textContent=v;b.classList.toggle('d-none',!v);};
 function render(d){
  const t=d.transaction,m=d.missing||{};
  document.getElementById('txTitle').textContent=`Transaction ${t.id} · ${t.type||'?'}`;
  document.getElementById('txBadge').innerHTML=t.success?'<span class="sbadge ok">Success</span>':'<span class="sbadge crit">Failed</span>';
  const sum=[['ATM',`${t.atmName||''} (${t.atmId})`],['Time',K.local(t.timeUtc)],['Type',t.type],['Status',t.status+(t.reason?' · '+t.reason:'')],['Amount',t.amount==null?'':t.amount.toLocaleString()+' '+(t.currency||'')],['Session',t.sessionId],['Response',t.responseMs?t.responseMs+' ms':''],['Error',t.errorCode]].filter(x=>x[1]);
  document.getElementById('tx-details').innerHTML=`<div class="row g-3"><div class="col-lg-5"><div class="cardx h-100"><h6>Summary</h6>${kv(sum.map(([name,value])=>({name,value})))}</div></div>
    <div class="col-lg-7"><div class="cardx h-100"><h6>KTC transaction record</h6>${kv(d.record)||note('KTC returned no record for this transaction id.')}</div></div></div>`;
  document.getElementById('tx-ej').innerHTML=(d.ej.length?`<div class="small text-muted mb-1">${K.esc(d.ejSource||'')} · card numbers masked</div><div class="ej-term">${K.esc(d.ej.join('\n'))}</div>`:note(m.ej||'No EJ lines.'));count('ej',d.ej.length);
  document.getElementById('tx-session').innerHTML=(m.session?note(m.session):'')+`<div class="row g-3">
    <div class="col-lg-5"><div class="cardx h-100"><h6>Session ${K.esc(t.sessionId||'')}</h6>${kv(d.session)||note('No session record.')}${d.sessionFields.length?`<h6 class="mt-3">Session fields</h6>${kv(d.sessionFields)}`:''}</div></div>
    <div class="col-lg-7"><div class="cardx h-100"><h6>Transactions in this session</h6>${d.sessionTransactions.length?`<table class="table table-sm small mb-0 counter-table"><thead><tr><th>Time</th><th>ID</th><th>Type</th><th>Status</th><th class="text-end">Amount</th></tr></thead><tbody>${d.sessionTransactions.map(s=>`<tr${s.id===t.id?' class="table-active"':''}><td>${K.local(s.timeUtc)}</td><td>${K.esc(s.id)}</td><td>${K.esc(s.type)}</td><td>${s.success?'<span class="sbadge ok">Success</span>':'<span class="sbadge crit">Failed</span>'}</td><td class="text-end num">${s.amount==null?'':s.amount.toLocaleString()}</td></tr>`).join('')}</tbody></table>`:note('KTC listed no transactions for this session.')}</div></div></div>`;
  count('session',d.sessionTransactions.length);
  document.getElementById('tx-fields').innerHTML=d.fields.length?`<div class="cardx">${kv(d.fields)}</div>`:note(m.fields||'No fields.');count('fields',d.fields.length);
  const notes=d.dispense.filter(x=>x.kind);const tot=k=>notes.filter(x=>x.kind===k&&x.denomination!=='All').reduce((s,x)=>s+x.count,0)||notes.filter(x=>x.kind===k).reduce((s,x)=>s+x.count,0);
  document.getElementById('tx-dispense').innerHTML=d.dispense.length?`<div class="kpis mb-2"><div class="kpi info"><div class="l">Notes dispensed</div><div class="v">${n(tot('Dispensed'))}</div><div class="d">&nbsp;</div></div><div class="kpi info"><div class="l">Notes accepted</div><div class="v">${n(tot('Accepted'))}</div><div class="d">&nbsp;</div></div></div>
    <div class="cardx"><table class="table table-sm small mb-0 counter-table"><thead><tr><th>Device</th><th>Counter</th><th>Denomination</th><th class="text-end">This transaction</th><th class="text-end">Running total</th><th></th></tr></thead><tbody>${d.dispense.map(x=>`<tr><td>${K.esc(x.device)}</td><td>${K.esc(x.counter)}</td><td>${K.esc(x.denomination)}</td><td class="text-end num">${n(x.count)}</td><td class="text-end num text-muted">${n(x.runningValue)}</td><td>${x.kind?`<span class="sbadge ok">${K.esc(x.kind)}</span>`:''}</td></tr>`).join('')}</tbody></table></div>`:note(m.dispense||'No dispense details.');
  count('dispense',notes.length);
  document.getElementById('txFoot').innerHTML=`<details><summary>What KTC returned</summary><ul class="mb-0 ps-3">${(d.diagnostics||[]).map(x=>`<li>${K.esc(x)}</li>`).join('')}</ul></details>`;
 }
 async function open(row){
  if(!row||!row.id)return;shell();last=row;
  document.getElementById('txTitle').textContent='Transaction '+row.id;document.getElementById('txBadge').innerHTML='';document.getElementById('txFoot').innerHTML='';
  ['details','ej','session','fields','dispense'].forEach(k=>{document.getElementById('tx-'+k).innerHTML='<div class="text-muted small">Loading from KTC…</div>';count(k,0);});
  bootstrap.Tab.getOrCreateInstance(document.querySelector('#txModal [data-bs-target="#tx-details"]')).show();modal.show();
  try{const d=await K.api('/data/transactions/'+encodeURIComponent(row.id)+'/details?'+K.qs({atmId:row.atmId||'',time:row.timeUtc||''}));if(last===row)render(d);}
  catch(e){if(last===row)document.getElementById('tx-details').innerHTML=`<div class="alert alert-danger small">${K.esc(e.message)}</div>`;}
 }
 K.txDetails=open;
 // Open the popup from a DataTables row click (rows carry id, atmId, timeUtc).
 K.txRowClicks=function(sel,getDt){$(sel+' tbody').off('click.tx').on('click.tx','tr',function(){const dt=getDt();const r=dt&&dt.row(this).data();if(r)open(r);}).css('cursor','pointer');$(sel).addClass('tx-clickable').attr('title','Click a transaction for its details');};
})();
