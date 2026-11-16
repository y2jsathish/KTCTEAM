// ATM 360: EJ Counters tab. Data: GET /data/atm/{id}/ej-counters?days= (the cash counter report the ATM prints to its EJ on restart:
// MACHINE NO, DATE-TIME, per cassette type CASSETTE / +REJECTED / +DISPENSED / -DEPOSITED / =TOTAL, LAST CLEARED). Loaded when the tab opens.
(function(){const K=KTC,id=window.ATM_ID,$id=s=>document.getElementById(s);
 const num=v=>v==null?'<span class="text-muted">–</span>':Number(v).toLocaleString();
 const pad=v=>v==null?'<span class="text-muted">–</span>':String(v).padStart(5,'0');
 const kpi=(l,v,c,d)=>`<div class="kpi ${c}"><div class="l">${l}</div><div class="v">${v}</div><div class="d">${d||'&nbsp;'}</div></div>`;
 const when=r=>r.reportTimeText?K.esc(r.reportTimeText):K.local(r.ejTimeUtc);
 const ROWS=[['cassette',' CASSETTE'],['rejected','+REJECTED'],['dispensed','+DISPENSED'],['deposited','-DEPOSITED'],['total','=TOTAL']];
 let loaded=false;

 // The latest report laid out like the printout: one column per cassette type, one row per counter, plus amount in the cassette.
 function latest(r){
  const t=r.types,extra=[...new Set(t.flatMap(x=>Object.keys(x.other||{})))];
  return `<div class="col-12"><div class="cardx"><div class="d-flex align-items-center mb-2"><h6 class="m-0"><i class="bi bi-receipt"></i> Latest counter report</h6>
    <span class="small text-muted ms-2">${r.machineNo?'Machine '+K.esc(r.machineNo)+' · ':''}DATE-TIME ${when(r)}${r.lastClearedText?' · last cleared '+K.esc(r.lastClearedText):''}</span></div>
   <div class="table-responsive"><table class="table table-sm small mb-0 counter-table"><thead><tr><th>Counter</th>${t.map(x=>`<th class="text-end">${K.esc(x.type)}<div class="text-muted fw-normal">${x.denomination!=null?num(x.denomination):'?'}</div></th>`).join('')}<th class="text-end">All types</th></tr></thead>
   <tbody>${ROWS.map(([k,l])=>`<tr${k==='total'?' class="fw-semibold"':''}><td class="font-monospace">${l}</td>${t.map(x=>`<td class="text-end num">${pad(x[k])}</td>`).join('')}<td class="text-end num">${num(t.reduce((s,x)=>s+(x[k]||0),0))}</td></tr>`).join('')}
    ${extra.map(k=>`<tr><td class="font-monospace">${K.esc(k)}</td>${t.map(x=>`<td class="text-end num">${pad((x.other||{})[k])}</td>`).join('')}<td></td></tr>`).join('')}
    <tr><td>Amount in cassette</td>${t.map(x=>`<td class="text-end num">${x.denomination!=null&&x.cassette!=null?num(x.denomination*x.cassette):'–'}</td>`).join('')}<td class="text-end num">${num(r.amountInCassettes)}</td></tr>
    <tr><td>Adds up</td>${t.map(x=>`<td class="text-end">${x.balanced==null?'–':x.balanced?'<span class="sbadge ok">Yes</span>':'<span class="sbadge warn" title="cassette + rejected + dispensed - deposited is not the total">No</span>'}</td>`).join('')}<td></td></tr></tbody></table></div></div></div>`;
 }
 // Every report in the range, newest first: per type the notes left in the cassette and dispensed.
 function history(reps){
  const types=[...new Set(reps.flatMap(r=>r.types.map(t=>t.type+'|'+(t.denomination??''))))];
  const lab=k=>{const [t,d]=k.split('|');return `${K.esc(t)}${d?' ('+Number(d).toLocaleString()+')':''}`;};
  return `<div class="col-12"><div class="cardx"><h6><i class="bi bi-clock-history"></i> Counter reports in the range (${reps.length})</h6>
   <div class="table-responsive"><table class="table table-sm small mb-0 counter-table"><thead><tr><th rowspan="2">DATE-TIME</th><th rowspan="2">Last cleared</th><th rowspan="2">Machine</th>${types.map(k=>`<th class="text-center" colspan="3">${lab(k)}</th>`).join('')}<th class="text-end" rowspan="2">Notes dispensed</th><th class="text-end" rowspan="2">Amount in cassettes</th></tr>
   <tr>${types.map(()=>'<th class="text-end">Cassette</th><th class="text-end">Rejected</th><th class="text-end">Dispensed</th>').join('')}</tr></thead>
   <tbody>${reps.map(r=>`<tr><td>${when(r)}</td><td class="text-muted">${r.lastClearedText?K.esc(r.lastClearedText):''}</td><td>${K.esc(r.machineNo||'')}</td>${types.map(k=>{const t=r.types.find(x=>x.type+'|'+(x.denomination??'')===k);return t?`<td class="text-end num">${num(t.cassette)}</td><td class="text-end num">${num(t.rejected)}</td><td class="text-end num">${num(t.dispensed)}</td>`:'<td></td><td></td><td></td>';}).join('')}<td class="text-end num">${num(r.notesDispensed)}</td><td class="text-end num">${num(r.amountInCassettes)}</td></tr>`).join('')}</tbody></table></div></div></div>`;
 }
 function render(c){
  $id('ejcMeta').innerHTML=`<i class="bi bi-journal-text"></i> ${num(c.ejRows)} EJ rows read, ${K.local(c.fromUtc)} – ${K.local(c.toUtc)}`;
  $id('ejcMsg').innerHTML=((c.warnings||[]).length?`<div class="alert alert-warning py-2 small">${c.warnings.map(K.esc).join('<br>')}</div>`:'')
   +((c.diagnostics||[]).length?`<details class="small text-muted mb-2"><summary>What KTC returned</summary><ul class="mb-0 ps-3">${c.diagnostics.map(x=>`<li>${K.esc(x)}</li>`).join('')}</ul></details>`:'');
  const r=c.reports[0];
  $id('ejcKpis').innerHTML=r?kpi('Last report',when(r),'info',r.machineNo?'machine '+K.esc(r.machineNo):'')+kpi('Last cleared',r.lastClearedText?K.esc(r.lastClearedText):'–','info')
   +kpi('Notes dispensed',num(r.notesDispensed),'info','since last cleared')+kpi('Reports in range',c.reports.length,'info',r.types.some(t=>t.balanced===false)?'latest does not add up':'')+'':'';
  $id('ejcBody').innerHTML=c.reports.length?latest(r)+history(c.reports):'<div class="col-12"><div class="alert alert-secondary small mb-0">No counter report in the EJ of this range.</div></div>';
 }
 async function load(){
  $id('ejcBody').innerHTML='<div class="col-12 text-muted small">Reading the EJ…</div>';
  try{render(await K.api('/data/atm/'+encodeURIComponent(id)+'/ej-counters?days='+$id('ejcDays').value));loaded=true;}
  catch(e){$id('ejcMsg').innerHTML=`<div class="alert alert-danger small">EJ counters unavailable: ${K.esc(e.message)}</div>`;$id('ejcBody').innerHTML='';$id('ejcKpis').innerHTML='';}
 }
 // The EJ is large: read it when the tab is first opened, then on Refresh or a new range.
 document.querySelector('[data-tab="ej-counters"]').addEventListener('shown.bs.tab',()=>{if(!loaded)load();});
 $id('ejcDays').onchange=()=>load();
 document.querySelector('[data-ejcrefresh]').onclick=()=>load();
})();
