(function(){
 const K=KTC; let last;
 function kpi(l,v,d,c){return `<div class="kpi ${c||'info'}"><div class="l">${l}</div><div class="v">${v}</div><div class="d">${d||'&nbsp;'}</div></div>`;}
 function render(s){
  last=s; document.getElementById('gen').textContent='Updated '+K.local(s.generatedUtc)+' · refreshes every 60 s';
  document.getElementById('warnings').innerHTML=(s.warnings&&s.warnings.length)?`<div class="alert alert-warning py-2 small"><b>Partial data</b> – some KTC calls failed:<ul class="mb-0">${s.warnings.slice(0,5).map(w=>'<li>'+K.esc(w)+'</li>').join('')}</ul>${s.warnings.some(w=>/Permission required/.test(w))?'<div class="mt-1">Some data needs extra KTC roles for the account shown. Ask your KTC administrator to add them.</div>':''}${s.warnings.some(w=>/401/.test(w))?'<div class="mt-1">KTC rejected the token. <a href="/diag/ktc-token">Run the KTC token check</a> and send the report.</div>':''}</div>`:'';
  const fr=s.totalTransactions||0, fp=s.transactionsToday?Math.round(1000*s.failedTransactions/s.transactionsToday)/10:0;
  document.getElementById('kpis').innerHTML=[
   kpi('Total ATMs',s.totalAtms,`${s.warningAtms} warning`,'info'),kpi('Online ATMs',s.onlineAtms,'in contact','ok'),
   kpi('Offline ATMs',s.offlineAtms,'no contact / comm fault',s.offlineAtms?'crit':'ok'),kpi('Total tickets',s.totalTickets,'last 30 days','info'),
   kpi('Open tickets',s.openTickets,'not resolved/closed',s.openTickets?'warn':'ok'),kpi('Critical incidents',s.criticalIncidents,'open P1 / P2',s.criticalIncidents?'crit':'ok'),
   kpi('Transactions today',s.transactionsToday.toLocaleString(),'portal local day','info'),kpi('Failed transactions',s.failedTransactions.toLocaleString(),fp+'% failure rate',fp>=10?'crit':fp>=5?'warn':'ok'),
   kpi('High priority alerts',s.highPriorityAlerts,'unacknowledged P1/P2 (24 h)',s.highPriorityAlerts?'crit':'ok')].join('');
  const P=K.pal(), ST=K.status();
  document.getElementById('availTxt').textContent=s.availabilityPct.toFixed(2)+'%';
  const av=s.availabilityPct, avc=av>=99?ST.Healthy:av>=95?ST.Warning:ST.Critical;
  K.chart('cAvail',{type:'doughnut',data:{labels:['Available','Unavailable'],datasets:[{data:[av,100-av],backgroundColor:[avc,K.css('--grid')],borderWidth:0}]},options:{rotation:-90,circumference:180,cutout:'72%',plugins:{tooltip:{callbacks:{label:c=>c.label+': '+c.parsed.toFixed(2)+'%'}}}}});
  const h=s.atmHealth; K.chart('cHealth',{type:'doughnut',data:{labels:Object.keys(h),datasets:[{data:Object.values(h),backgroundColor:[ST.Healthy,ST.Warning,ST.Critical],borderWidth:2,borderColor:K.css('--panel')}]},options:{cutout:'60%'}});
  K.legend('lgHealth',Object.keys(h),[ST.Healthy,ST.Warning,ST.Critical],Object.values(h));
  const ts=s.ticketsByStatus; K.chart('cTkStatus',{type:'doughnut',data:{labels:Object.keys(ts),datasets:[{data:Object.values(ts),backgroundColor:P.slice(0,5),borderWidth:2,borderColor:K.css('--panel')}]},options:{cutout:'58%'}});
  K.legend('lgTk',Object.keys(ts),P.slice(0,5),Object.values(ts));
  const sv=s.ticketsBySeverity; K.chart('cTkSev',{type:'bar',data:{labels:Object.keys(sv),datasets:[{data:Object.values(sv),backgroundColor:P[0],borderRadius:4,maxBarThickness:36}]},options:{scales:{y:{beginAtZero:true,ticks:{precision:0}}}}});
  const line=(id,pts,color,fill)=>K.chart(id,{type:'line',data:{labels:pts.map(p=>p.label),datasets:[{data:pts.map(p=>p.value),borderColor:color,backgroundColor:color+'33',fill:fill,tension:.3,borderWidth:2,pointRadius:2}]},options:{scales:{y:{beginAtZero:true,ticks:{precision:0}},x:{ticks:{maxTicksLimit:8}}}}});
  line('cIncTrend',s.incidentTrend,P[1],true);
  K.chart('cTxVol',{type:'bar',data:{labels:s.transactionVolume.map(p=>p.label),datasets:[{data:s.transactionVolume.map(p=>p.value),backgroundColor:P[0],borderRadius:3,maxBarThickness:22}]},options:{scales:{y:{beginAtZero:true},x:{ticks:{maxTicksLimit:12}}}}});
  line('cTxFail',s.transactionFailures,P[7],true);
 }
 async function critical(){
  try{const r=await K.api('/data/atms?status=Critical');document.getElementById('critList').innerHTML=r.data.length?r.data.slice(0,30).map(a=>`<div class="d-flex justify-content-between border-bottom py-1"><a href="/Atm/Detail/${encodeURIComponent(a.id)}">${K.esc(a.name)}</a><span class="text-muted">${K.esc(a.healthReason)}</span></div>`).join(''):'<span class="text-muted">No critical ATMs</span>';}catch(e){}
 }
 // Cash: actual daily withdrawals (stored history) and the stored run-out estimates (Cash Forecasting menu).
 let cashLast;
 function cashRender(f){
  cashLast=f;const P=K.pal(),d=f.daily||[],day=x=>new Date(x).toLocaleDateString([], {day:'numeric',month:'short'});
  K.chart('cCashDaily',{type:'bar',data:{labels:d.map(x=>day(x.day)+(x.partial?' (so far)':'')),datasets:[{label:'Withdrawn',data:d.map(x=>x.withdrawn),backgroundColor:d.map(x=>x.partial?K.css('--grid'):P[0]),borderRadius:4,maxBarThickness:24}]},options:{plugins:{legend:{display:false}},scales:{y:{beginAtZero:true}}}});
  document.getElementById('cashDailyNote').textContent=d.length?`Successful customer withdrawals, ${d.length} stored day(s).`:'No daily cash history stored yet.';
  const n=s=>f.atms.filter(a=>a.status===s).length, cash=f.atms.reduce((t,a)=>t+(a.cashAmount||0),0);
  const soon=f.atms.filter(a=>['Out','Critical','Warning'].includes(a.status)).slice(0,8);
  document.getElementById('cashOutlook').innerHTML=`<div class="d-flex flex-wrap gap-3 mb-2"><div><div class="text-muted">Cash in ATMs</div><b>${Math.round(cash).toLocaleString()}</b></div><div><div class="text-muted">Out now</div><b>${n('Out')}</b></div><div><div class="text-muted">Within 1 day</div><b>${n('Critical')}</b></div><div><div class="text-muted">Within 3 days</div><b>${n('Warning')}</b></div></div>`
   +(soon.length?soon.map(a=>`<div class="d-flex justify-content-between border-bottom py-1"><a href="/Cash/Atm/${encodeURIComponent(a.atmId)}${K.cashPct.qs()?'?'+K.cashPct.qs():''}">${K.esc(a.atmName)}</a><span class="text-muted">${a.status==='Out'?'out now':a.daysLeft!=null?'about '+a.daysLeft+' day(s)':''}</span></div>`).join(''):'<span class="text-muted">No ATM is estimated to run out within 3 days.</span>')
   +`<div class="text-muted mt-1">Low cash at ${f.lowPct}% of capacity (${Number(f.lowNotes).toLocaleString()} notes) · calculated ${K.local(f.generatedUtc)}.</div>`;
 }
 let cashAt=0;
 async function cash(){if(Date.now()-cashAt<300000&&cashLast)return;cashAt=Date.now();try{cashRender(await K.api('/data/cash/forecast'+(K.cashPct.qs()?'?'+K.cashPct.qs():'')));}catch(e){document.getElementById('cashOutlook').textContent='Cash forecast not available: '+e.message;}}
 async function load(force){try{render(await K.api('/data/summary'+(force?'?refresh=true':'')));critical();cash();}catch(e){K.toast(e.message,'error');}}
 document.getElementById('refreshBtn').onclick=()=>load(true);
 K.hooks.dashboard.push(()=>load(false)); document.addEventListener('ktc-theme',()=>{last&&render(last);cashLast&&cashRender(cashLast);});
 load(false); setInterval(()=>load(false),60000);
})();
