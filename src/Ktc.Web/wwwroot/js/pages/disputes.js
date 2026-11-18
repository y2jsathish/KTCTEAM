// Disputes, laid out like the Disputes tab of KTC's Windows client: "Dispute states filter" (KTC's states; All = statusfilter 0) and
// "Number of days to show" (Numdays), then Dispute Id, Transaction Id, Created At, Client, Status, Dispute Rules, Last Comment, Closed.
// Data: GET /data/disputes (KTC GET {group}/GroupDisputes, or one ATM's ClientDisputes). Double-click (or Enter on) a row: the Dispute
// Details window (js/dispute-details.js). Card numbers arrive masked from the server; nothing here ever sees a full number.
(function(){const K=KTC;let dt,tile=null,rows=[];const f=document.getElementById('f'),$id=s=>document.getElementById(s);
 const money=(v,c)=>v==null?'':`${Number(v).toLocaleString(undefined,{minimumFractionDigits:2,maximumFractionDigits:2})}${c?' '+c:''}`;
 const kpi=(l,v,c,a)=>`<div class="kpi ${c}" ${a!=null?`data-t="${K.esc(a)}" role="button" tabindex="0" style="cursor:pointer"`:''}><div class="l">${K.esc(l)}</div><div class="v">${v}</div></div>`;

 async function load(){
  const p=Object.fromEntries(new FormData(f).entries());
  const r=await K.api('/data/disputes?'+K.qs(p));
  $id('fState').innerHTML=K.disputeStateOptions(r.states,p.state);
  rows=r.data;
  const amt=r.totalAmount.filter(x=>x.amount!=null).map(x=>money(x.amount,x.currency)).join(' · ')||'—';
  $id('dsTiles').innerHTML=kpi('Shown',r.data.length,'info','')+kpi('Not closed',r.open,'warn','open')+kpi('Closed',r.closed,'info','closed')
   +r.counts.slice(0,6).map(c=>kpi(c.name,c.count,'info','s:'+c.name)).join('')+`<div class="kpi info"><div class="l">Disputed amount</div><div class="v" style="font-size:1rem">${K.esc(amt)}</div></div>`;
  $id('dsWarn').innerHTML=r.warnings.length?`<div class="alert alert-warning py-1 small">${r.warnings.map(K.esc).join('<br>')}</div>`:'';
  $id('dsSrc').textContent='From KTC: '+r.sources.join(', ')+(r.amountKnown?'':' · no amount found in KTC\'s records');
  show();
 }
 function show(){
  const t=tile;const v=!t?rows:t==='open'?rows.filter(x=>!x.isClosed):t==='closed'?rows.filter(x=>x.isClosed):rows.filter(x=>'s:'+x.state===t);
  K.reload(dt,v);
  document.querySelectorAll('#dsTiles [data-t]').forEach(e=>e.style.outline=t&&e.dataset.t===t?'2px solid var(--s1)':'');
 }
 dt=K.table('#tbl',{data:[],order:[[2,'desc']],createdRow:row=>{row.tabIndex=0;row.style.cursor='pointer';},columns:K.disputeColumns(true)});
 const open=tr=>{const d=dt.row(tr).data();if(d)K.disputeDetails(d.id,d.atmId,()=>load().catch(()=>{}));};
 $('#tbl tbody').on('dblclick','tr',function(){open(this);}).on('keydown','tr',function(e){if(e.key==='Enter')open(this);});

 // ---------- set-up (states, rules, message templates) ----------
 const showV=v=>v==null?'':K.esc(v);
 $id('dsSetup').addEventListener('toggle',async e=>{if(!e.target.open||e.target.dataset.done)return;e.target.dataset.done='1';
  try{const s=await K.api('/data/disputes/setup');
   const tbl=(list,empty)=>list.length?`<div class="table-responsive"><table class="table table-sm small mb-2"><thead><tr>${list[0].map(c=>`<th>${K.esc(c.name)}</th>`).join('')}</tr></thead><tbody>${list.map(r=>`<tr>${r.map(c=>`<td>${showV(c.value)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`:`<div class="text-muted">${empty}</div>`;
   $id('dsSetupBody').innerHTML=(s.warnings.length?`<div class="alert alert-warning py-1">${s.warnings.map(K.esc).join('<br>')}</div>`:'')
    +`<h6 class="small fw-semibold">States (GET DisputeState)</h6>${tbl(s.stateRecords,'KTC returned no dispute states.')}<h6 class="small fw-semibold">Rules (GET DisputeRule)</h6>${tbl(s.rules,'KTC returned no dispute rules.')}<h6 class="small fw-semibold">Customer message templates (GET DisputeMessageTemplate)</h6>${tbl(s.messageTemplates,'KTC returned no message templates.')}`;
  }catch(x){$id('dsSetupBody').innerHTML=`<div class="alert alert-danger py-1">${K.esc(x.message)}</div>`;}});

 // ---------- KTC's raw records (masked): shows KTC's real field names when a column comes out empty ----------
 $id('dsRawBtn').onclick=async()=>{const box=$id('dsRaw');
  if(!box.classList.contains('d-none')){box.classList.add('d-none');return;}
  box.classList.remove('d-none');box.innerHTML='<div class="small text-muted">Reading from KTC…</div>';
  const p=Object.fromEntries(new FormData(f).entries());
  try{const r=await K.api('/data/disputes/raw?'+K.qs({days:p.days,state:p.state,atmId:p.atmId}));
   const text=`KTC dispute records (card data masked)\nSource: ${r.source}\nRecords returned: ${r.count}\n\nFields:\n${r.fields.map(x=>`${x.name} = ${x.value}`).join('\n')}\n\n${r.records.join('\n\n')}`;
   box.innerHTML=(r.warnings.length?`<div class="alert alert-warning py-1 small">${r.warnings.map(K.esc).join('<br>')}</div>`:'')
    +`<div class="d-flex align-items-center gap-2 mb-1 small"><b>KTC's raw dispute records</b><span class="text-muted">${K.esc(r.source)} · ${r.count} record${r.count===1?'':'s'} · first ${r.records.length} shown · card data masked</span>
      <button type="button" class="btn btn-sm btn-primary ms-auto" id="dsRawCopy"><i class="bi bi-clipboard"></i> Copy</button></div>
     <div class="row g-2"><div class="col-lg-5"><table class="table table-sm small mb-0"><thead><tr><th>KTC field</th><th>Type: sample</th></tr></thead><tbody>${r.fields.map(x=>`<tr><td class="font-monospace">${K.esc(x.name)}</td><td class="text-break">${K.esc(x.value)}</td></tr>`).join('')}</tbody></table></div>
     <div class="col-lg-7"><pre class="small bg-body-tertiary border rounded p-2 mb-0" style="max-height:28rem;overflow:auto">${K.esc(r.records.join('\n\n')||'KTC returned no records for this filter.')}</pre></div></div>`;
   $id('dsRawCopy').onclick=()=>navigator.clipboard.writeText(text).then(()=>K.toast('Copied. Paste it in the chat so the field names can be mapped.','ok'),()=>K.toast('Copy failed: select the text and copy it.','error'));
  }catch(e){box.innerHTML=`<div class="alert alert-danger py-1 small">${K.esc(e.message)}</div>`;}};

 const pick=e=>{const t=e.target.closest('[data-t]');if(!t)return;tile=t.dataset.t===''||tile===t.dataset.t?null:t.dataset.t;show();};
 $id('dsTiles').addEventListener('click',pick);$id('dsTiles').addEventListener('keydown',e=>{if(e.key==='Enter')pick(e);});
 const reload=()=>{tile=null;load().catch(x=>K.toast(x.message,'error'));};
 $id('fState').onchange=reload;$id('fDays').onchange=reload;
 $id('fCard').addEventListener('input',e=>{e.target.value=e.target.value.replace(/\D/g,'').slice(0,4);});
 f.onsubmit=e=>{e.preventDefault();reload();};
 load().then(()=>{const q=new URLSearchParams(location.search).get('disputeId');const r=q&&rows.find(x=>x.id===q);if(r)K.disputeDetails(r.id,r.atmId,()=>load().catch(()=>{}));}).catch(e=>K.toast(e.message,'error'));
 setInterval(()=>{const m=$id('dsModal');if(!document.hidden&&!(m&&m.classList.contains('show')))load().catch(()=>{});},60000);})();
