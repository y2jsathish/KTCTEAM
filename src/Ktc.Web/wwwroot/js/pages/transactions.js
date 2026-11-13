(function(){const K=KTC;let dt;const f=document.getElementById('f');
 const kpi=(l,v,c,d)=>`<div class="kpi ${c}"><div class="l">${l}</div><div class="v">${v}</div><div class="d">${d||'&nbsp;'}</div></div>`;
 function hbar(id,labels,vals,color){K.chart(id,{type:'bar',data:{labels,datasets:[{data:vals,backgroundColor:color,borderRadius:4,maxBarThickness:18}]},options:{indexAxis:'y',scales:{x:{beginAtZero:true,ticks:{precision:0}}}}});}
 async function load(){const p=Object.fromEntries(new FormData(f).entries());for(const k of ['from','to'])if(p[k])p[k]=new Date(p[k]).toISOString();
  const r=await K.api('/data/transactions?'+K.qs(p));const fp=r.total?(100*r.failed/r.total):0;
  document.getElementById('kpis').innerHTML=kpi('Total transactions',r.total.toLocaleString(),'info')+kpi('Successful',r.successful.toLocaleString(),'ok',(100-fp).toFixed(1)+'%')+kpi('Failed',r.failed.toLocaleString(),r.failed?(fp>=10?'crit':'warn'):'ok',fp.toFixed(1)+'%')+kpi('Avg response',r.avgResponseMs+' ms','info')+(r.truncated?kpi('Showing','2000 rows','warn','grid truncated'):'')+(r.hint?`<div class="alert alert-info w-100 mb-0 small">${K.esc(r.hint)}</div>`:'');
  const cats=['Host Failure','Communication Failure','Cash Retract','Dispense Failure','Other'],P=K.pal(),cols=[P[0],P[1],P[2],P[6],P[4]];
  K.chart('cCat',{type:'doughnut',data:{labels:cats,datasets:[{data:cats.map(c=>r.byCategory[c]||0),backgroundColor:cols,borderWidth:2,borderColor:K.css('--panel')}]},options:{cutout:'58%'}});K.legend('lgCat',cats,cols,cats.map(c=>r.byCategory[c]||0));
  hbar('cErr',r.byError.map(x=>x.error||'(none)'),r.byError.map(x=>x.count),P[1]);hbar('cAtm',r.topAtms.map(x=>x.atm),r.topAtms.map(x=>x.count),P[7]);
  K.reload(dt,r.data);}
 dt=K.table('#tbl',{data:[],order:[[1,'desc']],columns:[{data:'id'},{data:'timeUtc',render:(v,t)=>t==='display'?K.local(v):v},{data:'atmName'},{data:'client'},{data:'type',render:K.esc},{data:'status',render:v=>v==='Success'?'<span class="sbadge ok">Success</span>':'<span class="sbadge crit">Failed</span>'},{data:'amount',render:(v,t,r)=>v==null?'':v.toLocaleString()+' '+K.esc(r.currency||'')},{data:'responseMs'},{data:'errorCode',render:K.esc},{data:'failureCategory',render:K.esc}]});
 f.onsubmit=e=>{e.preventDefault();load().catch(x=>K.toast(x.message,'error'));};
 (async()=>{const fl=await K.api('/data/filters');fl.clients.forEach(v=>fClient.insertAdjacentHTML('beforeend',`<option>${K.esc(v)}</option>`));fl.regions.forEach(v=>fRegion.insertAdjacentHTML('beforeend',`<option>${K.esc(v)}</option>`));await load();})().catch(e=>K.toast(e.message,'error'));
 setInterval(()=>load().catch(()=>{}),30000);document.addEventListener('ktc-theme',()=>load().catch(()=>{}));})();
