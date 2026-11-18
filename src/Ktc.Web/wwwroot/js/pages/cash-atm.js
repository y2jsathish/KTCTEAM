// Cash forecast of one ATM: GET /data/cash/atm/{id} (live: cash units, notes by denomination since the reset, latest EJ counter report,
// stored daily cash and snapshots). Counts are actual; usage, run-out and loads are estimates.
(function(){const K=KTC,id=window.ATM_ID,$id=s=>document.getElementById(s);let last;
 const num=v=>v==null?'<span class="text-muted">–</span>':Number(v).toLocaleString();
 const kpi=(l,v,c,d)=>`<div class="kpi ${c}"><div class="l">${l}</div><div class="v">${v}</div><div class="d">${d||'&nbsp;'}</div></div>`;
 const CLS={Out:'crit',Critical:'crit',Warning:'warn',OK:'ok',Match:'ok',Differs:'crit'};
 const badge=s=>`<span class="text-nowrap sbadge ${CLS[s]||'unknown'}">${K.esc(s)}</span>`;
 const days=d=>d==null?'–':d>=90?'&gt; 90':Number(d).toFixed(1);
 const day=u=>u?new Date(u.endsWith('Z')?u:u+'Z').toLocaleDateString([], {weekday:'short',day:'numeric',month:'short'}):'';
 const dayOnly=d=>new Date(d).toLocaleDateString([], {day:'numeric',month:'short'});

 function units(f){
  if(!f.units.length)return '<div class="text-muted small">No dispensing cassettes.</div>';
  return `<div class="table-responsive"><table class="table table-sm small mb-1 counter-table"><thead><tr><th>Unit</th><th>Type</th><th class="text-end">Note</th><th class="text-end">Notes now</th><th class="text-end">Amount</th><th class="text-end">Notes / day (est.)</th><th>Usage from</th><th>Confidence</th><th class="text-end">Days left (est.)</th><th>Runs out (est.)</th><th>Status</th></tr></thead>
  <tbody>${f.units.map(u=>`<tr><td>${K.esc(u.unit)}</td><td>${K.esc(u.type)}</td><td class="text-end num">${num(u.denomination)}</td><td class="text-end num">${num(u.notes)}</td><td class="text-end num">${num(u.amount)}</td>
   <td class="text-end num">${u.notesPerDay==null?'–':num(u.notesPerDay)}</td><td>${K.esc(u.rateSource)}${u.rateDays?` <span class="text-muted">(${u.rateDays} d)</span>`:''}</td><td>${K.esc(u.confidence)}</td>
   <td class="text-end num">${days(u.daysLeft)}</td><td>${u.status==='Out'?'now':u.runOutUtc&&u.daysLeft<90?day(u.runOutUtc):''}</td><td>${badge(u.status)}</td></tr>`).join('')}</tbody></table></div>`
  +`<div class="small text-muted">A cassette counts as out at or below ${last.lowPct}% of its capacity (${num(last.lowNotes)} notes). ${f.weekdayPattern?'Run-out dates use the weekday pattern of the stored history.':'No weekday pattern yet (needs 2 weeks of daily history), so usage is spread evenly over the days.'}</div>`;
 }
 function suggest(f){
  const s=f.suggestion;
  if(!s)return '<div class="text-muted small">No cassette is estimated to run out within the forecast horizon, so no visit is suggested.</div>';
  return `<div class="mb-2"><b>Visit by ${K.local(s.visitByUtc)}</b><div class="small text-muted">${K.esc(s.reason)}</div></div>
  <table class="table table-sm small mb-1"><thead><tr><th>Unit</th><th class="text-end">Note</th><th class="text-end">Expected at visit</th><th class="text-end">Load to</th><th class="text-end">Add</th></tr></thead>
  <tbody>${s.loads.map(l=>`<tr><td>${K.esc(l.unit)}</td><td class="text-end num">${num(l.denomination)}</td><td class="text-end num">${num(l.expectedAtVisit)}</td><td class="text-end num">${num(l.targetNotes)}${l.usageKnown?'':' <span class="text-muted" title="No usage estimate: filled to capacity">*</span>'}</td><td class="text-end num">${num(l.addNotes)}</td></tr>`).join('')}</tbody>
  <tfoot><tr><th colspan="3">Amount</th><th class="text-end num">${num(s.targetAmount)}</th><th class="text-end num">${num(s.addAmount)}</th></tr></tfoot></table>
  <div class="small text-muted">Load covers the configured days of use plus safety stock, capped at the cassette capacity. Estimate: check against the CIT schedule.</div>`;
 }
 function recon(r){
  $id('reconStatus').innerHTML=badge(r.status);
  const t=(l,v)=>v?`<span class="me-3">${l}: ${K.local(v)}</span>`:'';
  return `<div class="small text-muted mb-2">${t('Cash units',r.xfsUpdatedUtc)}${t('EJ report',r.ejReportUtc)}${t('EJ last cleared',r.ejLastClearedUtc)}${t('Application counters since',r.appResetUtc)}</div>`
  +(r.rows.length?`<div class="table-responsive"><table class="table table-sm small mb-1 counter-table"><thead><tr><th class="text-end">Note</th><th class="text-end">XFS notes now</th><th class="text-end">EJ notes in cassette</th><th class="text-end">App dispensed since reset</th><th class="text-end">EJ dispensed since cleared</th><th>Result</th><th>Detail</th></tr></thead>
  <tbody>${r.rows.map(x=>`<tr${x.status==='Differs'?' class="table-warning-soft"':''}><td class="text-end num">${num(x.denomination)}</td><td class="text-end num">${num(x.xfsNotes)}</td><td class="text-end num">${num(x.ejNotes)}</td><td class="text-end num">${num(x.appDispensed)}</td><td class="text-end num">${num(x.ejDispensed)}</td><td>${badge(x.status)}</td><td class="text-muted">${K.esc(x.detail)}</td></tr>`).join('')}</tbody>
  <tfoot><tr><th>Total dispensed</th><th class="text-end num" title="XFS dispenser counter (its own reset time)">${num(r.xfsDispensedTotal)}</th><th></th><th class="text-end num">${num(r.appDispensedTotal)}</th><th class="text-end num">${num(r.ejDispensedTotal)}</th><th colspan="2" class="text-muted small">XFS total is the dispenser's own counter and may count from a different reset.</th></tr></tfoot></table></div>`:'<div class="text-muted small">No note values to compare.</div>')
  +(r.notes.length?`<ul class="small text-muted mb-0 ps-3">${r.notes.map(n=>`<li>${K.esc(n)}</li>`).join('')}</ul>`:'');
 }
 function charts(d){
  const P=K.pal(),dd=d.daily;
  K.chart('cDaily',{type:'bar',data:{labels:dd.map(x=>dayOnly(x.day)+(x.partial?' (so far)':'')),datasets:[{label:'Withdrawn',data:dd.map(x=>x.withdrawn),backgroundColor:dd.map(x=>x.partial?K.css('--grid'):P[0]),borderRadius:4,maxBarThickness:28}]},options:{plugins:{legend:{display:false}},scales:{y:{beginAtZero:true}}}});
  const daysL=[...new Set(d.snapshots.map(s=>s.day))].sort(),unitsL=[...new Set(d.snapshots.map(s=>s.unit))];
  $id('snapNote').textContent=daysL.length<2?'One snapshot per day is stored; the chart fills in as days pass.':'';
  K.chart('cSnaps',{type:'line',data:{labels:daysL.map(dayOnly),datasets:unitsL.map((u,i)=>{const den=d.snapshots.find(s=>s.unit===u)?.denomination;return {label:`Unit ${u}${den?' ('+den+')':''}`,data:daysL.map(x=>d.snapshots.find(s=>s.unit===u&&s.day===x)?.notes??null),borderColor:P[i%P.length],backgroundColor:P[i%P.length],tension:.2,spanGaps:true};})},options:{scales:{y:{beginAtZero:true}}}});
 }
 function render(d){
  last=d;const f=d.forecast;
  document.title=`Cash forecast · ${f.atmName}`;$id('title').textContent=`Cash forecast · ${f.atmName||f.atmId}`;$id('sub').textContent=[f.client,'calculated '+K.local(f.generatedUtc)].filter(Boolean).join(' · ');
  $id('atm360').href='/Atm/Detail/'+encodeURIComponent(f.atmId);$id('lowPct').value=d.lowPct;
  $id('warnings').innerHTML=(f.warnings||[]).length?`<div class="alert alert-warning py-2 small">${f.warnings.map(K.esc).join('<br>')}</div>`:'';
  $id('kpis').innerHTML=kpi('Cash now',num(f.cashAmount),'info',`${num(f.notes)} notes · actual`)
   +kpi('Status',K.esc(f.status),CLS[f.status]||'warn',f.daysLeft!=null?`about ${days(f.daysLeft)} day(s) left (estimate)`:'no usage estimate')
   +kpi('Runs out (est.)',f.status==='Out'?'now':f.earliestRunOutUtc&&f.daysLeft<90?day(f.earliestRunOutUtc):'–',CLS[f.status]||'info','earliest cassette')
   +kpi('Last replenishment',f.lastReplenishmentUtc?K.local(f.lastReplenishmentUtc):'–','info','from the transactions')
   +kpi('Confidence',K.esc(f.confidence),f.confidence==='Medium'?'ok':'warn','of the usage estimate');
  $id('units').innerHTML=units(f);$id('suggest').innerHTML=suggest(f);$id('recon').innerHTML=recon(d.reconciliation);
  $id('diag').innerHTML=d.diagnostics.map(x=>`<li>${K.esc(x)}</li>`).join('');
  charts(d);
 }
 if(!id){$id('warnings').innerHTML='<div class="alert alert-warning py-2 small">No ATM selected. Pick one on the <a href="/Cash">Cash Forecasting</a> page.</div>';return;}
 const load=()=>K.api('/data/cash/atm/'+encodeURIComponent(id)+(K.cashPct.qs()?'?'+K.cashPct.qs():'')).then(render).catch(e=>{$id('warnings').innerHTML=`<div class="alert alert-danger py-2 small">${K.esc(e.message)}</div>`;});
 function applyPct(v){
  if(v!==''&&(!isFinite(+v)||+v<0||+v>100)){K.toast('Enter a percent from 0 to 100.','warn');return;}
  K.cashPct.set(v);if(new URLSearchParams(location.search).has('lowPct'))history.replaceState(null,'',location.pathname);
  $id('warnings').innerHTML='<div class="text-muted small">Recalculating…</div>';load();
 }
 $id('lowApply').onclick=()=>applyPct($id('lowPct').value.trim());
 $id('lowPct').onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();applyPct($id('lowPct').value.trim());}};
 $id('lowReset').onclick=()=>applyPct('');
 load();
 document.addEventListener('ktc-theme',()=>last&&charts(last));
})();
