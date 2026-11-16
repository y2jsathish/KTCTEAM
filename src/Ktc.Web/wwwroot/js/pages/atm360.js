(function(){const K=KTC,id=window.ATM_ID;let d,tables={};
 const kpi=(l,v,c,s)=>`<div class="kpi ${c}"><div class="l">${l}</div><div class="v">${v}</div><div class="d">${s||'&nbsp;'}</div></div>`;
 function render(r){d=r;const a=r.atm;
  document.getElementById('title').textContent=a.name;document.getElementById('hb').innerHTML=K.health(a.health);document.getElementById('sub').textContent=`${a.client} · ${a.city} · ${a.healthReason}`;
  document.getElementById('fav').innerHTML=`<i class="bi bi-star${r.favorite?'-fill':''}"></i> ${r.favorite?'Favorited':'Favorite'}`;
  document.getElementById('warnings').innerHTML=r.warnings.length?`<div class="alert alert-warning py-2 small">${r.warnings.map(K.esc).join('<br>')}</div>`:'';
  const av=r.availability.pct;
  document.getElementById('kpis').innerHTML=kpi('Health score',a.healthScore+'/100',a.health==='Healthy'?'ok':a.health==='Warning'?'warn':'crit',a.healthReason)+kpi('Availability (30 d)',av==null?'n/a':av.toFixed(2)+'%',av==null?'info':av>=99?'ok':av>=95?'warn':'crit')+kpi('Open incidents',a.openIncidents,a.openIncidents?'warn':'ok')+kpi('Open tickets',a.openTickets,a.openTickets?'warn':'ok')+kpi('Last communication',K.ago(a.lastCommunicationUtc),a.offline?'crit':'ok',K.local(a.lastCommunicationUtc));
  document.getElementById('device').innerHTML=`<dl class="row mb-0"><dt class="col-4">ATM ID</dt><dd class="col-8">${a.id}</dd><dt class="col-4">Name</dt><dd class="col-8">${K.esc(a.name)}</dd><dt class="col-4">Device status</dt><dd class="col-8">${K.esc(a.deviceStatus)}</dd><dt class="col-4">Client</dt><dd class="col-8">${K.esc(a.client)}</dd><dt class="col-4">Location</dt><dd class="col-8">${K.esc(a.location)}</dd><dt class="col-4">City / region</dt><dd class="col-8">${K.esc(a.city)} / ${K.esc(a.region)}</dd><dt class="col-4">Branch</dt><dd class="col-8">${K.esc(a.branch)}</dd><dt class="col-4">Type</dt><dd class="col-8">${K.esc(a.atmType)}</dd></dl>`;
  K.chart('cReasons',{type:'bar',data:{labels:r.availability.reasons.map(x=>x.reason),datasets:[{label:'Hours',data:r.availability.reasons.map(x=>x.hours),backgroundColor:K.pal()[1],borderRadius:4,maxBarThickness:18}]},options:{indexAxis:'y',scales:{x:{beginAtZero:true}}}});
  const t=(sel,rows,cols,o)=>{tables[sel]=K.table(sel,Object.assign({data:rows,pageLength:8,lengthChange:false,searching:false,columns:cols},o||{}));};
  t('#tInc',r.incidents,[{data:'number'},{data:'severity',render:K.sev},{data:'openedUtc',render:K.local},{data:'status'},{data:'description',render:K.esc}],{order:[[2,'desc']]});
  t('#tTk',r.tickets,[{data:'id'},{data:'status'},{data:'severity',render:K.sev},{data:'createdUtc',render:K.local},{data:'sla',render:K.slaBadge}],{order:[[3,'desc']]});
  t('#tTx',r.transactions,[{data:'timeUtc',render:K.local},{data:'type',render:K.esc},{data:'status',render:v=>v==='Success'?'<span class="sbadge ok">Success</span>':'<span class="sbadge crit">Failed</span>'},{data:'responseMs'},{data:'errorCode',render:K.esc}],{order:[[0,'desc']]});K.txRowClicks('#tTx',()=>tables['#tTx']);
  document.getElementById('timeline').innerHTML=r.timeline.length?r.timeline.map(s=>`<li class="${s.level}"><b>${K.esc(s.label)}</b> <span class="text-muted small">${new Date(s.timeUtc.endsWith('Z')?s.timeUtc:s.timeUtc+'Z').toLocaleTimeString()}</span><div class="small">${K.esc(s.detail)}</div></li>`).join(''):'<li class="text-muted">No EJ data in the last 6 hours.</li>';
  document.getElementById('ejRaw').textContent=r.ej.slice(-40).map(e=>e.text).join('\n');
 }
 async function load(){try{render(await K.api('/data/atm/'+encodeURIComponent(id)));}catch(e){document.getElementById('warnings').innerHTML=`<div class="alert alert-danger">${K.esc(e.message)}</div>`;}}
 document.getElementById('fav').onclick=async()=>{const r=await K.api('/data/atm/'+encodeURIComponent(id)+'/favorite?name='+encodeURIComponent(d.atm.name),{method:'POST'});d.favorite=r.favorite;render(d);};
 const hcls={OK:'ok',Warning:'warn',Fatal:'crit',Unknown:'unknown'};
 async function loadHw(){const box=document.getElementById('hw'),all=document.getElementById('hwAll'),base='/Hardware?atmId='+encodeURIComponent(id);all.href=base;
  const mods=document.getElementById('hwModules'),kp=document.getElementById('hwKpis'),fl=document.getElementById('hwFlagged');
  try{const r=await K.api('/data/hardware?'+K.qs({atmId:id}));
   box.innerHTML=r.modules.length?r.modules.map(m=>`<a class="hw-chip text-decoration-none" href="${base}&component=${encodeURIComponent(m.id)}" title="${K.esc(m.name)}: ${K.esc(m.status)} - click for details"><span class="sbadge ${hcls[m.status]||'unknown'}">${K.esc(m.status)}</span> ${K.esc(m.name)}</a>`).join(''):'<span class="text-muted small">KTC returned no component status for this ATM.</span>';
   // Hardware Status tab: overall status, counts and one card per device with its properties and errors (devices not OK first).
   const cnt=s=>r.modules.filter(m=>m.status===s).length,bad=r.modules.filter(m=>m.status!=='OK');
   document.getElementById('hwMeta').innerHTML=r.updatedUtc?`<i class="bi bi-clock"></i> Last reported ${K.local(r.updatedUtc)}`:'';
   document.getElementById('hwMsg').innerHTML=(r.warnings||[]).length?`<div class="alert alert-warning py-2 small">${r.warnings.map(K.esc).join('<br>')}</div>`:'';
   fl.textContent=bad.length;fl.classList.toggle('d-none',!bad.length);
   const k=(l,v,c,d)=>`<div class="kpi ${c}"><div class="l">${l}</div><div class="v">${v}</div><div class="d">${d||'&nbsp;'}</div></div>`;
   kp.innerHTML=k('Overall',K.esc(r.status),hcls[r.status]||'info')+k('Devices',r.modules.length,'info')+k('OK',cnt('OK'),'ok')+k('Warning',cnt('Warning'),cnt('Warning')?'warn':'ok')+k('Fatal',cnt('Fatal'),cnt('Fatal')?'crit':'ok');
   const order={Fatal:0,Warning:1,Unknown:2,OK:3};
   mods.innerHTML=r.modules.slice().sort((a,b)=>(order[a.status]??2)-(order[b.status]??2)||a.name.localeCompare(b.name)).map(m=>`<div class="col-md-6 col-xl-4"><div class="cardx h-100 xfs-card ${m.status==='OK'?'ok':'warn'}">
     <div class="d-flex align-items-center mb-1"><h6 class="m-0">${K.esc(m.name)}</h6><span class="ms-auto sbadge ${hcls[m.status]||'unknown'}">${K.esc(m.status)}</span></div>
     <div class="small text-muted mb-2">${m.updatedUtc?'Last update '+K.local(m.updatedUtc):'&nbsp;'} · <a href="${base}&component=${encodeURIComponent(m.id)}">details</a></div>
     ${(m.errors||[]).length?`<div class="small text-danger mb-2">${m.errors.slice(0,3).map(K.esc).join('<br>')}</div>`:''}
     <table class="table table-sm small mb-0 counter-table"><tbody>${(m.properties||[]).slice(0,8).map(p=>`<tr${/warn|fatal|crit/i.test(p.status||'')?' class="table-warning-soft"':''}><td>${K.esc(p.name)}</td><td class="text-end">${K.esc(p.value)}</td></tr>`).join('')||'<tr><td class="text-muted">No properties reported.</td></tr>'}</tbody></table>
     ${(m.properties||[]).length>8?`<div class="small text-muted mt-1">+${m.properties.length-8} more on the details page</div>`:''}</div></div>`).join('');
  }catch(e){box.innerHTML=`<span class="text-muted small">Hardware status unavailable: ${K.esc(e.message)}</span>`;mods.innerHTML='';kp.innerHTML='';}}
 document.querySelector('[data-hwrefresh]').onclick=()=>loadHw();
 load();loadHw();setInterval(load,60000);setInterval(loadHw,60000);K.hooks.dashboard.push(load);document.addEventListener('ktc-theme',()=>d&&render(d));})();
