// Cash Forecasting: GET /data/cash/forecast (stored estate forecast), POST /data/cash/recalculate (operators).
// Cash now and the daily history are actual KTC data; usage, run-out, visit and load are estimates.
(function(){const K=KTC,$id=s=>document.getElementById(s);let data,dt;
 const num=v=>v==null?'<span class="text-muted">–</span>':Number(v).toLocaleString();
 const kpi=(l,v,c,d)=>`<div class="kpi ${c}"><div class="l">${l}</div><div class="v">${v}</div><div class="d">${d||'&nbsp;'}</div></div>`;
 const CLS={Out:'crit',Critical:'crit',Warning:'warn',OK:'ok'};
 const badge=(s,why)=>`<span class="text-nowrap sbadge ${CLS[s]||'unknown'}"${why?` title="${K.esc(why)}"`:''}>${K.esc(s)}</span>`;
 const days=d=>d==null?'<span class="text-muted">–</span>':d>=90?'&gt; 90':Number(d).toFixed(1);
 const day=u=>u?new Date(u.endsWith('Z')?u:u+'Z').toLocaleDateString([], {weekday:'short',day:'numeric',month:'short'}):'';
 const conf=c=>`<span class="small ${c==='Medium'?'':'text-muted'}" title="How much to trust the usage estimate">${K.esc(c)}</span>`;
 const src=f=>{const s=[...new Set(f.units.filter(u=>u.notesPerDay!=null).map(u=>u.rateSource))];return s.length?s.map(K.esc).join(', '):'<span class="text-muted">none</span>';};
 const atmUrl=id=>`/Cash/Atm/${encodeURIComponent(id)}${K.cashPct.qs()?'?'+K.cashPct.qs():''}`;
 let dupNames=new Set();
 const link=f=>`<a href="${atmUrl(f.atmId)}">${K.esc(f.atmName||f.atmId)}</a>${dupNames.has((f.atmName||'').toLowerCase())?` <span class="text-muted small" title="Another ATM has the same name; this is KTC client id ${K.esc(f.atmId)}">#${K.esc(f.atmId)}</span>`:''}`;

 function unitsTable(f){
  if(!f.units.length)return '<div class="text-muted small">No dispensing cassettes.</div>';
  return `<table class="table table-sm small"><thead><tr><th>Unit</th><th class="text-end">Note</th><th class="text-end">Notes</th><th class="text-end">Loaded</th><th class="text-end" title="Low-cash level of this cassette">Low at</th><th class="text-end">Per day (est.)</th><th class="text-end">Days left</th><th>Status</th></tr></thead><tbody>${f.units.map(u=>`<tr><td>${K.esc(u.unit)}</td><td class="text-end num">${num(u.denomination)}</td><td class="text-end num">${num(u.notes)}</td><td class="text-end num" title="${K.esc(u.loadedSource||'unknown')}">${num(u.loaded)}</td><td class="text-end num" title="${K.esc(u.lowRule||'')}">${num(u.lowNotes)}</td><td class="text-end num" title="${K.esc(u.rateSource)}">${u.notesPerDay==null?'–':num(u.notesPerDay)}</td><td class="text-end num">${days(u.daysLeft)}</td><td>${badge(u.status)}</td></tr>`).join('')}</tbody></table>`;
 }
 const loadedAmt=f=>{const d=f.denominations||[];return d.length&&d.every(x=>x.loaded!=null)?d.reduce((t,x)=>t+x.loadedAmount,0):null;};
 const remPct=f=>{const l=loadedAmt(f);return l?Math.min(100,f.cashAmount*100/l):null;};
 function details(f){
  const s=f.suggestion;
  K.details(f.atmName||f.atmId,`<div class="small text-muted mb-2">${K.esc(f.client)} · cash now ${num(f.cashAmount)} (${num(f.notes)} notes)${f.lastReplenishmentUtc?' · last replenished '+K.local(f.lastReplenishmentUtc):''}</div>${unitsTable(f)}`
   +(f.statusReason?`<div class="small mb-2"><b>Why ${K.esc(f.status)}:</b> ${K.esc(f.statusReason)}</div>`:'')+(s?`<div class="small"><b>Suggested visit by ${K.local(s.visitByUtc)}</b> (estimate). ${K.esc(s.reason)} Load to add about ${num(s.addAmount)}.</div>`:'<div class="small text-muted">No replenishment needed in the forecast horizon.</div>')
   +((f.warnings||[]).length?`<div class="alert alert-warning py-1 small mt-2">${f.warnings.map(K.esc).join('<br>')}</div>`:'')
   +`<a class="btn btn-sm btn-primary mt-2" href="${atmUrl(f.atmId)}">Open cash forecast and reconciliation</a>`);
 }
 function render(){
  const fc=$id('fClient').value,fs=$id('fStatus').value;
  const rows=data.atms.filter(a=>(!fc||a.client===fc)&&(!fs||a.status===fs));
  const by=s=>rows.filter(a=>a.status===s).length, cash=rows.reduce((t,a)=>t+(a.cashAmount||0),0);
  $id('kpis').innerHTML=kpi('Cash in ATMs',Math.round(cash).toLocaleString(),'info',`${rows.length} ATMs · actual`)
   +kpi('Out of cash now',by('Out'),by('Out')?'crit':'ok',`a cassette at or below ${data.lowPct}% of its own loaded count`)
   +kpi('Run out within 1 day',by('Critical'),by('Critical')?'crit':'ok','estimate')
   +kpi('Run out within 3 days',by('Warning'),by('Warning')?'warn':'ok','estimate')
   +kpi('No usage data',by('No usage data'),by('No usage data')?'warn':'ok','no estimate possible')
   +kpi('History stored',data.historyDays+' day(s)',data.historyDays>=28?'ok':data.historyDays>=7?'info':'warn','daily snapshots');
  const soon=rows.filter(a=>['Out','Critical','Warning'].includes(a.status));
  $id('soon').innerHTML=soon.length?`<table class="table table-sm mb-0"><thead><tr><th>ATM</th><th>Status</th><th>Runs out (est.)</th><th>Visit by (est.)</th></tr></thead><tbody>${soon.map(a=>`<tr><td>${link(a)}</td><td>${badge(a.status,a.statusReason)}</td><td>${a.status==='Out'?'now':day(a.earliestRunOutUtc)}</td><td>${a.suggestion?K.local(a.suggestion.visitByUtc):''}</td></tr>`).join('')}</tbody></table>`:'<div class="text-muted">No ATM is estimated to run out within 3 days.</div>';
  dt=K.table('#tbl',{data:rows,order:[[4,'asc']],columns:[
   {data:'atmName',render:(v,t,f)=>t==='display'?link(f):(v||f.atmId)},{data:'client',render:K.esc},
   {data:'cashAmount',className:'text-end num',render:(v,t)=>t==='display'?num(v):v},
   {data:'earliestRunOutUtc',render:(v,t,f)=>t==='display'?(f.status==='Out'?'now':f.daysLeft==null?'':f.daysLeft>=90?'<span class="text-muted">beyond 90 days</span>':day(v)):(v||'')},
   {data:'daysLeft',className:'text-end num',render:(v,t)=>t==='display'?days(v):(v==null?9999:v)},
   {data:'status',render:(v,t,f)=>t==='display'?badge(v,f.statusReason):v},
   {data:'suggestion',render:(v,t)=>v?(t==='display'?K.local(v.visitByUtc):v.visitByUtc):(t==='display'?'<span class="text-muted">not needed</span>':'')},
   {data:'suggestion',className:'text-end num',render:(v,t)=>v?(t==='display'?num(v.addAmount):v.addAmount):(t==='display'?'':0)},
   {data:null,orderable:false,render:(v,t,f)=>src(f)},{data:'confidence',render:(v,t)=>t==='display'?conf(v):v},
   {data:null,className:'text-end num',render:(v,t,f)=>{const l=loadedAmt(f);return t==='display'?num(l):(l??-1);}},
   {data:null,className:'text-end num',render:(v,t,f)=>{const p=remPct(f);return t==='display'?(p==null?'<span class="text-muted">–</span>':p.toFixed(0)+'%'):(p??-1);}}]});
  $('#tbl tbody').off('click').on('click','tr',function(e){if(e.target.closest('a'))return;const f=dt.row(this).data();if(f)details(f);});
 }
 function chart(){
  const P=K.pal(),d=data.daily||[];
  $id('dailyNote').textContent=d.length?`Successful customer withdrawals per portal-local day, stored by the daily snapshot (${d.length} day(s)).${d.some(x=>x.partial)?' Today is not over yet.':''}`:'No daily history stored yet.';
  K.chart('cDaily',{type:'bar',data:{labels:d.map(x=>day(x.day)+(x.partial?' (so far)':'')),datasets:[{label:'Withdrawn (actual)',data:d.map(x=>x.withdrawn),backgroundColor:d.map(x=>x.partial?K.css('--grid'):P[0]),borderRadius:4,maxBarThickness:28}]},
   options:{plugins:{legend:{display:false},tooltip:{callbacks:{afterLabel:c=>`${d[c.dataIndex].withdrawals} withdrawals`}}},scales:{y:{beginAtZero:true}}}});
 }
 function show(f){
  data=f;
  const seen={};dupNames=new Set();f.atms.forEach(a=>{const n=(a.atmName||'').toLowerCase();if(n&&seen[n])dupNames.add(n);seen[n]=1;});
  $id('gen').textContent=`Calculated ${K.local(f.generatedUtc)} (${f.trigger}) · low cash at ${f.lowPct}% of each cassette's own loaded count (of the capacity, ${Number(f.lowNotes).toLocaleString()} notes, when the loaded count is unknown)`;
  $id('lowPct').value=f.lowPct;
  $id('warnings').innerHTML=(f.warnings||[]).length?`<div class="alert alert-warning py-2 small">${f.warnings.map(K.esc).join('<br>')}</div>`:'';
  const cl=[...new Set(f.atms.map(a=>a.client).filter(Boolean))].sort(),sel=$id('fClient'),cur=sel.value;
  sel.innerHTML='<option value="">All clients</option>'+cl.map(c=>`<option${c===cur?' selected':''}>${K.esc(c)}</option>`).join('');
  render();chart();
 }
 const q=()=>K.cashPct.qs()?'?'+K.cashPct.qs():'';
 async function load(){show(await K.api('/data/cash/forecast'+q()));}
 function applyPct(v){
  if(v!==''&&(!isFinite(+v)||+v<0||+v>100)){K.toast('Enter a percent from 0 to 100.','warn');return;}
  K.cashPct.set(v);if(new URLSearchParams(location.search).has('lowPct'))history.replaceState(null,'',location.pathname);
  load().catch(e=>K.toast(e.message,'error'));
 }
 $id('lowApply').onclick=()=>applyPct($id('lowPct').value.trim());
 $id('lowPct').onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();applyPct($id('lowPct').value.trim());}};
 $id('lowReset').onclick=()=>applyPct('');
 $id('fClient').onchange=render;$id('fStatus').onchange=render;
 const rb=$id('recalc');if(rb)rb.onclick=async()=>{rb.disabled=true;rb.innerHTML='<span class="spinner-border spinner-border-sm"></span> Recalculating…';
  try{show(await K.api('/data/cash/recalculate'+q(),{method:'POST'}));K.toast('Cash forecast recalculated','ok');}catch(e){K.toast(e.message,'error');}
  finally{rb.disabled=false;rb.innerHTML='<i class="bi bi-arrow-clockwise"></i> Recalculate';}};
 document.addEventListener('ktc-theme',()=>data&&chart());
 load().catch(e=>{$id('gen').textContent='';K.toast(e.message,'error');});
})();
