(function(){const K=KTC;let dt;
 function render(rows){const red=rows.filter(r=>r.color==='red').length,yel=rows.filter(r=>r.color==='yellow').length,calls=rows.reduce((a,r)=>a+r.calls,0),fails=rows.reduce((a,r)=>a+r.failures,0);
  document.getElementById('kpis').innerHTML=`<div class="kpi info"><div class="l">Endpoints observed</div><div class="v">${rows.length}</div></div><div class="kpi ${red?'crit':'ok'}"><div class="l">Red</div><div class="v">${red}</div></div><div class="kpi ${yel?'warn':'ok'}"><div class="l">Yellow</div><div class="v">${yel}</div></div><div class="kpi info"><div class="l">Overall success</div><div class="v">${calls?(100*(calls-fails)/calls).toFixed(1):'–'}%</div></div>`;
  K.reload(dt,rows);}
 dt=K.table('#tbl',{data:[],order:[[3,'desc']],columns:[{data:'color',render:(v,t)=>t==='display'?`<span class="health-dot ${v}" title="${v}"></span>`:v},{data:'endpoint'},{data:'method'},{data:'calls'},{data:'successRatePct',render:v=>v+'%'},{data:'failures'},{data:'timeouts'},{data:'avgMs'},{data:'p95Ms'},{data:'lastMs'},{data:'consecutiveFailures'},{data:'lastCallUtc',render:K.ago},{data:'lastError',render:v=>K.esc(v||'')}]});
 async function load(){render(await K.api('/data/apihealth'));}
 K.hooks.apiHealth.push(render);load().catch(e=>K.toast(e.message,'error'));setInterval(()=>load().catch(()=>{}),15000);})();
