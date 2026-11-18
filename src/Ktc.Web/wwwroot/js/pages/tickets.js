// Ticket Workbench, laid out like KTC's own ticket list. Data: GET /data/tickets (KTC POST api/v1/Ticket/Search), with KTC's
// status filter and "number of days of closed tickets to show". Double-click (or Enter on) a row: Ticket Details popup from
// GET /data/tickets/{id}/details (ticket, ticket type, error, error code type, SLA, child tickets, comments). Read-only.
(function(){const K=KTC;let dt,cat='';const f=document.getElementById('f'),$id=s=>document.getElementById(s);
 const pad=n=>String(n).padStart(2,'0');
 const ktcDate=u=>{if(!u)return '';const d=new Date(/Z$|[+-]\d\d:\d\d$/.test(u)?u:u+'Z');return `${pad(d.getDate())} ${d.toLocaleString('en-GB',{month:'short'})} ${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;};
 const dur=s=>{if(s==null||s<0)return '';const d=Math.floor(s/86400),h=Math.floor(s%86400/3600),m=Math.floor(s%3600/60),x=s%60;return (d?d+'.':'')+`${pad(h)}:${pad(m)}:${pad(x)}`;};
 const dcol=k=>({data:k,render:(v,t)=>t==='display'?ktcDate(v):(v||'')});
 const stCls=s=>/clos|resolv|cancel/i.test(s)?'ok':/dispatch|own|assign|progress/i.test(s)?'info':'warn';
 const kpi=(l,v,c,a)=>`<div class="kpi ${c}" data-s="${a}" role="button" tabindex="0" style="cursor:pointer;${cat===a?'outline:2px solid var(--s1)':''}"><div class="l">${l}</div><div class="v">${v}</div></div>`;

 async function load(){
  const p=Object.fromEntries(new FormData(f).entries());if(cat)p.status=cat;
  const r=await K.api('/data/tickets?'+K.qs(p));
  const sel=$id('fStatus'),keep=sel.value;
  sel.innerHTML='<option value="">All</option>'+r.statuses.map(s=>`<option value="${K.esc(s.name)}">${K.esc(s.name)} (${s.count})</option>`).join('');
  if([...sel.options].some(o=>o.value===keep))sel.value=keep;
  $id('statusTiles').innerHTML=['Open','Assigned','InProgress','Resolved','Closed'].map(s=>kpi(s==='InProgress'?'In progress':s,r.counts[s]||0,s==='Open'?'warn':'info',s)).join('')
   +kpi('All',Object.values(r.counts).reduce((a,b)=>a+b,0),'info','')+`<div class="kpi info"><div class="l">No SLA in KTC</div><div class="v">${r.sla.noKtcSla}</div></div>`;
  $id('breachBanner').innerHTML=r.breachSoon.length?`<div class="alert alert-warning py-2"><i class="bi bi-alarm"></i> <b>${r.breachSoon.length} ticket(s) will breach SLA within 1 hour:</b> ${r.breachSoon.slice(0,6).map(t=>`<a href="#" class="tk-link" data-id="${K.esc(t.id)}">#${K.esc(t.id)}</a> (${t.minutes} min, ${K.esc(t.atmName)})`).join(' · ')}</div>`:'';
  K.reload(dt,r.data);
 }
 dt=K.table('#tbl',{data:[],order:[[3,'desc']],createdRow:row=>{row.tabIndex=0;row.style.cursor='pointer';},columns:[
  {data:'id'},{data:'typeName',render:K.esc},
  {data:'atmName',render:(v,t,r)=>t==='display'?`<a href="/Atm/Detail/${encodeURIComponent(r.atmId)}" onclick="event.stopPropagation()">${K.esc(v)}</a>`:v},
  dcol('createdUtc'),dcol('openedUtc'),dcol('closedUtc'),{data:'durationSeconds',render:(v,t)=>t==='display'?dur(v):v},
  {data:'ktcStatus',render:(v,t)=>t==='display'?`<span class="sbadge ${stCls(v)} text-nowrap">${K.esc(v)}</span>`:v},
  {data:'errorId',render:K.esc},{data:'errorCode',render:K.esc},{data:'errorText',render:K.esc},
  {data:'owner',render:v=>K.esc(v||'Unowned')},{data:'lastChangeBy',render:v=>K.esc(v||'')},dcol('lastChangeUtc'),
  {data:'lastComment',render:(v,t)=>t==='display'?`<span class="d-inline-block text-truncate" style="max-width:22rem" title="${K.esc(v)}">${K.esc(v)}</span>`:v},
  {data:'slaSummary',render:(v,t,r)=>t==='display'?(r.hasKtcSla?K.slaBadge(r.sla)+' ':'')+K.esc(v):v}]});
 const open=tr=>{const d=dt.row(tr).data();if(d)details(d.id,d.atmId);};
 $('#tbl tbody').on('dblclick','tr',function(){open(this);}).on('keydown','tr',function(e){if(e.key==='Enter')open(this);});

 // ---------- Ticket Details ----------
 const val=(list,...names)=>{if(!list)return null;for(const n of names){const x=list.find(f=>f.name.toLowerCase()===n.toLowerCase());if(x&&x.value!=='')return x.value;}return null;};
 const isDate=v=>typeof v==='string'&&/^\d{4}-\d\d-\d\dT\d\d:\d\d/.test(v);
 const show=(v,empty)=>v==null||v===''?`<span class="text-muted">${K.esc(empty||'—')}</span>`:K.esc(isDate(v)?(v.startsWith('0001')?'':ktcDate(v)):v==='true'?'True':v==='false'?'False':v);
 const grid=(title,rows)=>`<fieldset class="border rounded p-2 mb-2 h-100"><legend class="float-none w-auto px-1 small fw-semibold mb-0">${title}</legend>
   <dl class="row small mb-0">${rows.map(([l,v,e])=>`<dt class="col-5 fw-normal text-muted">${K.esc(l)}</dt><dd class="col-7 mb-1">${show(v,e)}</dd>`).join('')}</dl></fieldset>`;
 const kv=list=>list&&list.length?`<table class="table table-sm small mb-0"><tbody>${list.map(x=>`<tr><th class="fw-normal text-muted w-25">${K.esc(x.name)}</th><td class="text-break">${show(x.value)}</td></tr>`).join('')}</tbody></table>`:'<div class="text-muted small">Nothing returned.</div>';
 async function details(id,atmId){
  ['tkInfo','tkSla','tkNotif','tkKids','tkAll'].forEach(x=>$id(x).innerHTML='<div class="text-muted small">Loading from KTC…</div>');
  $id('tkTitle').textContent='Ticket Details · '+id;$id('tkSrc').textContent='';$id('tkAtm').href='/Atm/Detail/'+encodeURIComponent(atmId);
  bootstrap.Tab.getOrCreateInstance(document.querySelector('[data-bs-target="#tkInfo"]')).show();
  bootstrap.Modal.getOrCreateInstance($id('tkModal')).show();
  let d;try{d=await K.api('/data/tickets/'+encodeURIComponent(id)+'/details');}
  catch(e){$id('tkInfo').innerHTML=`<div class="alert alert-danger small">${K.esc(e.message)}</div>`;return;}
  const t=d.ticket,F=d.ticketFields,T=d.ticketType,E=d.error,C=d.errorCodeType;
  const info=grid('Ticket Information',[['Ticket ID',t.id],['Status',t.ktcStatus+(t.statusByte!=null?` (${t.statusByte})`:'')],['Escalation Level',val(F,'EscalationLevel','Escalation')],['Bank Ref. Num.',val(F,'BankReferenceNumber','BankRefNum','BankRef')],
   ['Ticket Created',t.createdUtc],['Ticket Opened',t.openedUtc],['Closure Mode',val(F,'ClosureMode','ClosureModeName')],['Planned Ticket',val(F,'PlannedTicket','IsPlanned','Planned')],
   ['Owner',t.owner,'Unowned'],['Owned Time',val(F,'TimeOwned','OwnedTime'),'Never Owned'],['Dispatched To',val(F,'DispatchedTo','DispatchName','DispatchedToName')],['Dispatched Time',val(F,'TimeDispatched','DispatchedTime'),'Never Dispatched'],
   ['Closed Time',t.closedUtc],['Last Update',t.updatedUtc||t.lastChangeUtc],['Reopen Count',val(F,'ReopenCount','TimesReopened')],['Duration',dur(Math.round(((t.closedUtc?new Date(t.closedUtc+(t.closedUtc.endsWith('Z')?'':'Z')):new Date())-new Date((t.openedUtc||t.createdUtc)+((t.openedUtc||t.createdUtc).endsWith('Z')?'':'Z')))/1000))]]);
  const type=grid('Ticket Type Information',T?[['Type Name',val(T,'Name','TypeName')||t.typeName],['Applies to business',val(T,'AppliesToBusiness','BusinessName','Business')],['Sends Email',val(T,'SendsEmail','SendEmail','EmailMode')],['Performs Action',val(T,'PerformsAction','PerformAction')],
   ['Minimum Points',val(T,'MinimumPoints','MinPoints')],['Category',val(T,'Category','CategoryName')],['Conflict Category',val(T,'ConflictCategory','ConflictCategoryName')],['No tickets when planned active',val(T,'NoTicketsWhenPlannedActive','NoTicketsWhenPlanned')]]
   :[['Type Name',t.typeName,'KTC returned no ticket type for this ticket']]);
  const err=grid('Error Code Information',[['Error Code',t.errorCode],['Error ID',t.errorId],['Client Name',t.atmName],['Occurrence Time',val(E,'OccurrenceTime','OccuranceTime','TimeOccurred','TimeAdded')],
   ['Received Time',val(E,'ReceivedTime','TimeReceived','TimeAdded')],['Error Code Closed',val(E,'TimeClosed','ClosedTime')],['Counts as Downtime',val(E,'CountsAsDowntime','IsDowntime','Downtime')]]);
  const ect=grid('Error Code Type Information',[['Error Text',val(C,'ErrorText','Text','Name')||t.errorText],['Error Description',val(C,'Description','ErrorDescription')],['Error Level',val(C,'Level','ErrorLevel')],['Category',val(C,'Category','CategoryName')],['Help Text',val(C,'HelpText','Help')]]);
  const com=`<h6 class="small fw-semibold mt-2">Comments <span class="text-muted fw-normal">(card numbers masked)</span></h6>${d.comments.length?`<div class="table-responsive"><table class="table table-sm small mb-0"><thead><tr><th>User</th><th>Time</th><th>Comment</th></tr></thead><tbody>${d.comments.map(c=>`<tr><td class="text-nowrap">${K.esc(c.user||'')}</td><td class="text-nowrap">${K.esc(ktcDate(c.utc))}</td><td class="text-break">${K.esc(c.text)}</td></tr>`).join('')}</tbody></table></div>`:'<div class="text-muted small">No comments.</div>'}`;
  const W=await K.api('/data/tickets/'+encodeURIComponent(id)+'/write').catch(()=>null);
  const write=W?`<div class="border rounded p-2 mt-2" id="tkWrite"><h6 class="small fw-semibold mb-1"><i class="bi bi-chat-left-text"></i> Add to this ticket</h6>
   ${W.enabled?'':'<div class="alert alert-info py-1 small mb-2"><i class="bi bi-info-circle"></i> Writing to KTC tickets is off (<code>TicketWrite:Enabled</code>). You can fill this in and run the checks; nothing is sent.</div>'}
   <label class="form-label small mb-1" for="tkComment">Comment <span class="text-muted">(${W.minCommentLength}–${W.maxCommentLength} characters; card numbers are masked before sending)</span></label>
   <textarea class="form-control form-control-sm" id="tkComment" rows="2" maxlength="${W.maxCommentLength}"></textarea>
   <div class="d-flex flex-wrap gap-2 align-items-end mt-2">
    <button class="btn btn-sm btn-primary" id="tkAddBtn"><i class="bi bi-chat-left-dots"></i> Add comment</button>
    ${W.canClose?`<div><label class="form-label small mb-1" for="tkConfirm">To close, type the ticket number</label><input class="form-control form-control-sm" id="tkConfirm" autocomplete="off" style="max-width:12rem"></div>
     <button class="btn btn-sm btn-danger" id="tkCloseBtn"><i class="bi bi-check2-square"></i> Close ticket with this comment</button>`
     :`<span class="small text-muted"><i class="bi bi-lock"></i> Close: ${K.esc(W.closeBlockedReason||'not available')}</span>`}
   </div><div id="tkWriteRes" class="mt-2"></div></div>`:'';
  $id('tkInfo').innerHTML=(d.warnings.length?`<div class="alert alert-warning py-1 small">${d.warnings.map(K.esc).join('<br>')}</div>`:'')
   +`<div class="row g-2"><div class="col-lg-6">${info}</div><div class="col-lg-6">${type}</div><div class="col-lg-6">${err}</div><div class="col-lg-6">${ect}</div></div>${com}${write}`;
  if(W){$id('tkAddBtn').onclick=()=>writeTicket(id,atmId,'comment');if($id('tkCloseBtn'))$id('tkCloseBtn').onclick=()=>writeTicket(id,atmId,'close');}
  $id('tkSla').innerHTML=d.sla.length?d.sla.map((s,i)=>`<h6 class="small fw-semibold">SLA ${i+1}</h6>${kv(s)}`).join(''):`<div class="small">No SLA</div><div class="small text-muted">KTC (GET Ticket/${K.esc(id)}/SLA) returned no SLA for this ticket.</div>`;
  $id('tkNotif').innerHTML=d.notifications.length?d.notifications.map(kv).join('<hr>'):'<div class="small text-muted">No queued notifications on this ticket record. KTC\'s API has no read call for queued notifications, so only what the ticket itself carries is shown.</div>';
  $id('tkKids').innerHTML=d.children.length?`<table class="table table-sm small"><thead><tr><th>Ticket ID</th><th>Type</th><th>Status</th><th>Created</th><th>Closed</th></tr></thead><tbody>${d.children.map(c=>`<tr><td>${K.esc(c.id)}</td><td>${K.esc(c.typeName)}</td><td>${K.esc(c.ktcStatus)}</td><td>${K.esc(ktcDate(c.createdUtc))}</td><td>${K.esc(ktcDate(c.closedUtc))}</td></tr>`).join('')}</tbody></table>`:'<div class="small text-muted">No child tickets.</div>';
  $id('tkAll').innerHTML=`<h6 class="small fw-semibold">Ticket</h6>${kv(F)}${T?`<h6 class="small fw-semibold mt-2">Ticket type</h6>${kv(T)}`:''}${E?`<h6 class="small fw-semibold mt-2">Error</h6>${kv(E)}`:''}${C?`<h6 class="small fw-semibold mt-2">Error code type</h6>${kv(C)}`:''}`;
  $id('tkSrc').textContent='From KTC: '+d.sources.join(', ');
 }

 // Add comment / close: the server checks everything, sends only when TicketWrite:Enabled is on, and audits each attempt.
 async function writeTicket(id,atmId,what){
  const btns=['tkAddBtn','tkCloseBtn'].map($id).filter(Boolean);btns.forEach(b=>b.disabled=true);
  const body={comment:$id('tkComment').value,confirmTicketId:$id('tkConfirm')?$id('tkConfirm').value:null};
  try{const r=await K.api(`/data/tickets/${encodeURIComponent(id)}/${what}`,{json:body});
   if(!r||typeof r!=='object'||!('done' in r))throw new Error('The portal sent no result for this change. Nothing is known to have been saved in KTC: reload the ticket and check its comments.');
   const box=`<div class="alert ${r.done?'alert-success':'alert-warning'} py-1 small mb-1">${K.esc(r.message||(r.done?'Done.':'KTC did not confirm the change.'))}${r.correlationId?` <span class="text-muted">· ref ${K.esc(r.correlationId)}</span>`:''}</div>
    <ul class="list-unstyled small mb-0">${(r.checks||[]).map(c=>`<li><i class="bi ${c.passed?'bi-check-circle text-success':'bi-x-circle text-danger'}"></i> <b>${K.esc(c.name)}</b>: ${K.esc(c.detail)}</li>`).join('')}</ul>
    ${(r.ktcSteps||[]).length?`<details class="small mt-1"${r.done?'':' open'}><summary>What KTC answered</summary><ol class="mb-0 font-monospace" style="font-size:.75rem">${(r.ktcSteps||[]).map(x=>`<li>${K.esc(x)}</li>`).join('')}</ol></details>`:''}`;
   if(r.done)K.toast(r.message,'ok');
   if((r.ktcSteps||[]).length){await details(id,atmId);load().catch(()=>{});}
   if($id('tkWriteRes'))$id('tkWriteRes').innerHTML=box;
  }catch(e){if($id('tkWriteRes'))$id('tkWriteRes').innerHTML=`<div class="alert alert-danger py-1 small">${K.esc(e.message)}</div>`;}
  btns.forEach(b=>{if(document.body.contains(b))b.disabled=false;});
 }

 $id('statusTiles').addEventListener('click',e=>{const t=e.target.closest('[data-s]');if(t){cat=t.dataset.s;$id('fStatus').value='';load();}});
 $id('statusTiles').addEventListener('keydown',e=>{const t=e.target.closest('[data-s]');if(t&&e.key==='Enter'){cat=t.dataset.s;$id('fStatus').value='';load();}});
 $id('fStatus').onchange=()=>{cat='';load().catch(x=>K.toast(x.message,'error'));};
 $id('breachBanner').addEventListener('click',e=>{const a=e.target.closest('.tk-link');if(a){e.preventDefault();f.ticketId.value=a.dataset.id;load();}});
 f.onsubmit=e=>{e.preventDefault();load().catch(x=>K.toast(x.message,'error'));};
 load().then(()=>{const q=new URLSearchParams(location.search).get('ticketId');const r=q&&dt.rows().data().toArray().find(x=>x.id===q);if(r)details(r.id,r.atmId);}).catch(e=>K.toast(e.message,'error'));
 setInterval(()=>{if(!document.hidden&&!$id('tkModal').classList.contains('show'))load().catch(()=>{});},60000);})();
