// ATM 360: Replenishment tab. Data: GET /data/atm/{id}/replenishment (KTC CurrentCashUnitStatus, replenishment sessions in the
// transactions, and KTC's ReplenishmentsByTimeRange / ReplenishmentOrders / PhysicalCassettes routes as they answer).
(function(){const K=KTC,id=window.ATM_ID,$id=s=>document.getElementById(s);
 const num=v=>v==null?'<span class="text-muted">–</span>':Number(v).toLocaleString();
 const kpi=(l,v,c,d)=>`<div class="kpi ${c}"><div class="l">${l}</div><div class="v">${v}</div><div class="d">${d||'&nbsp;'}</div></div>`;
 const ago=t=>{if(!t)return '';const h=(Date.now()-new Date(t.endsWith('Z')?t:t+'Z'))/36e5;return h<1?'less than an hour ago':h<48?Math.round(h)+' hours ago':Math.round(h/24)+' days ago';};
 const card=(icon,title,body,cls)=>`<div class="${cls||'col-12'}"><div class="cardx h-100"><h6><i class="bi ${icon}"></i> ${title}</h6>${body}</div></div>`;

 function units(r){
  if(!r.cashUnits.length)return card('bi-safe','Cash units now','<div class="text-muted small">KTC returned no cash units for this ATM.</div>','col-xl-6 col-12');
  const tot=f=>r.cashUnits.reduce((s,u)=>s+(u[f]||0),0);
  return card('bi-safe','Cash units now (KTC CurrentCashUnitStatus)',`<div class="table-responsive"><table class="table table-sm small mb-0 counter-table"><thead><tr><th>Device</th><th>Unit</th><th>Type</th><th class="text-end">Denomination</th><th class="text-end">Notes</th><th class="text-end">Amount</th><th>Status</th><th>Updated</th></tr></thead>
   <tbody>${r.cashUnits.map(u=>`<tr${/low|empty|full|fault|miss|inop/i.test(u.status)?' class="table-warning-soft"':''}><td>${K.esc(u.device)}</td><td>${K.esc(u.unit)}</td><td>${K.esc(u.type)}</td><td class="text-end num">${num(u.value)}</td><td class="text-end num">${num(u.count)}</td><td class="text-end num">${num(u.amount)}</td><td>${K.esc(u.status)}</td><td class="text-muted">${u.updatedUtc?K.local(u.updatedUtc):''}</td></tr>`).join('')}</tbody>
   <tfoot><tr><th colspan="4">Total</th><th class="text-end num">${num(tot('count'))}</th><th class="text-end num">${num(tot('amount'))}</th><th colspan="2"></th></tr></tfoot></table></div>`,'col-xl-6 col-12');
 }
 function history(r){
  const body=r.history.length?`<div class="table-responsive"><table class="table table-sm small mb-0 counter-table"><thead><tr><th>Replenished</th><th>Operations</th><th>Status</th><th>Until</th><th class="text-end">Customer tx</th><th class="text-end">Withdrawals</th><th class="text-end">Amount withdrawn</th><th class="text-end">Deposits</th><th class="text-end">Amount deposited</th></tr></thead>
   <tbody>${r.history.map(h=>`<tr><td>${K.local(h.startUtc)}</td><td>${h.operations.map(K.esc).join(', ')}</td><td><span class="sbadge ${h.success?'ok':'warn'}">${h.success?'Completed':'Not all completed'}</span></td><td class="text-muted">${h.nextUtc?K.local(h.nextUtc):'now'}</td><td class="text-end num">${num(h.customerTransactions)}</td><td class="text-end num">${num(h.withdrawals)}</td><td class="text-end num">${num(h.amountWithdrawn)}</td><td class="text-end num">${num(h.deposits)}</td><td class="text-end num">${num(h.amountDeposited)}</td></tr>`).join('')}</tbody></table></div>`
   :`<div class="text-muted small">No replenishment in the last ${r.lookbackDays} days of transactions.</div>`;
  return card('bi-clock-history',`Replenishment history (last ${r.lookbackDays} days of transactions)`,body,'col-xl-6 col-12');
 }
 function ktc(s){
  let body;
  if(s.error)body=`<div class="small text-muted">KTC did not answer: ${K.esc(s.error)}</div>`;
  else if(!s.records.length)body='<div class="small text-muted">KTC answered with no records for the last 30 days.</div>';
  else{const cols=[];s.records.forEach(r=>r.forEach(f=>{if(!cols.includes(f.name)&&cols.length<12)cols.push(f.name);}));
   body=`<div class="table-responsive"><table class="table table-sm small mb-0 counter-table"><thead><tr>${cols.map(c=>`<th>${K.esc(c)}</th>`).join('')}</tr></thead><tbody>${s.records.map(r=>`<tr>${cols.map(c=>{const f=r.find(x=>x.name===c);return `<td>${f?K.esc(f.value):''}</td>`;}).join('')}</tr>`).join('')}</tbody></table></div>`;}
  return card('bi-database',`${K.esc(s.title)} <span class="small text-muted text-none">${K.esc(s.route)}</span>`,body);
 }
 function render(r){
  $id('repMeta').innerHTML=r.lastReplenishmentUtc?`<i class="bi bi-arrow-counterclockwise"></i> Last replenishment ${K.local(r.lastReplenishmentUtc)}`:'';
  $id('repMsg').innerHTML=((r.warnings||[]).length?`<div class="alert alert-warning py-2 small">${r.warnings.map(K.esc).join('<br>')}</div>`:'')
   +((r.diagnostics||[]).length?`<details class="small text-muted mb-2"><summary>What KTC returned</summary><ul class="mb-0 ps-3">${r.diagnostics.map(x=>`<li>${K.esc(x)}</li>`).join('')}</ul></details>`:'');
  const disp=r.cashUnits.filter(u=>u.value!=null),amt=disp.reduce((s,u)=>s+(u.amount||0),0),notes=disp.reduce((s,u)=>s+u.count,0);
  const cur=r.history[0];
  $id('repKpis').innerHTML=kpi('Last replenishment',r.lastReplenishmentUtc?K.esc(ago(r.lastReplenishmentUtc)):'–',r.lastReplenishmentUtc?'info':'warn',r.lastReplenishmentUtc?K.local(r.lastReplenishmentUtc):`none in ${r.lookbackDays} days`)
   +kpi('Cash in cassettes',disp.length?amt.toLocaleString():'–','info',disp.length?`${notes.toLocaleString()} notes in ${disp.length} units`:'no valued cash units')
   +kpi('Withdrawn since',cur?Number(cur.amountWithdrawn).toLocaleString():'–','info',cur?`${cur.withdrawals} withdrawals`:'')
   +kpi(`Replenishments (${r.lookbackDays} d)`,r.history.length,'info');
  $id('repBody').innerHTML=units(r)+history(r)+r.ktcRecords.map(ktc).join('');
 }
 async function load(){
  try{render(await K.api('/data/atm/'+encodeURIComponent(id)+'/replenishment'));}
  catch(e){$id('repMsg').innerHTML=`<div class="alert alert-danger small">Replenishment unavailable: ${K.esc(e.message)}</div>`;$id('repBody').innerHTML='';$id('repKpis').innerHTML='';}
 }
 document.querySelector('[data-reprefresh]').onclick=()=>load();
 load();
})();
