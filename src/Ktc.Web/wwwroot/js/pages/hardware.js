(function(){const K=KTC,f=document.getElementById('f'),atm=document.getElementById('atm'),msg=document.getElementById('msg');let cur=null,focus=window.HW_COMPONENT||null;
 const cls={OK:'ok',Warning:'warn',Fatal:'crit',Unknown:'unknown'};
 const badge=s=>`<span class="sbadge ${cls[s]||'unknown'}">${K.esc(s)}</span>`;
 const when=v=>v?K.local(v):'';
 const kpi=(l,v,c,d)=>`<div class="kpi ${c}"><div class="l">${l}</div><div class="v">${v}</div><div class="d">${d||'&nbsp;'}</div></div>`;
 function card(m){
  const props=m.properties.map(p=>`<tr><td>${K.esc(p.name)}</td><td>${K.esc(p.value)}</td><td>${p.status==='Unknown'?'<span class="text-muted">info</span>':badge(p.status)}</td><td class="text-muted small">${when(p.updatedUtc)}</td></tr>`).join('');
  const errs=m.errors.map(e=>`<li>${K.esc(e)}</li>`).join('');
  const hit=focus&&(String(m.id)===String(focus)||m.name===focus);
  return `<div class="col-md-6 col-xl-4"><div class="cardx h-100${hit?' hw-focus':''}" id="comp-${K.esc(String(m.id))}" data-comp="${K.esc(String(m.id))}" style="border-left:4px solid var(--${({OK:'ok',Warning:'warn',Fatal:'crit'})[m.status]||'line'})">
   <div class="d-flex align-items-center mb-2"><h6 class="m-0">${K.esc(m.name)}</h6><span class="ms-auto">${badge(m.status)}</span></div>
   ${m.updatedUtc?`<div class="small text-muted mb-2">Last update ${when(m.updatedUtc)}</div>`:''}
   ${errs?`<div class="small mb-2"><b>Open errors</b><ul class="mb-0 ps-3">${errs}</ul></div>`:''}
   ${props?`<div style="${hit?'':'max-height:260px;'}overflow:auto"><table class="table table-sm small mb-0"><thead><tr><th>Property</th><th>Value</th><th>Status</th><th>Updated</th></tr></thead><tbody>${props}</tbody></table></div>`:''}
  </div></div>`;}
 async function load(){
  const id=atm.value.trim();if(!id)return;msg.innerHTML='';
  document.getElementById('refresh').disabled=true;
  try{
   const r=await K.api('/data/hardware?'+K.qs({atmId:id}));cur=id;
   const n=s=>r.modules.filter(m=>m.status===s).length;
   document.getElementById('kpis').innerHTML=kpi('ATM',K.esc(r.atmName),cls[r.status]==='crit'?'crit':cls[r.status]==='warn'?'warn':cls[r.status]==='ok'?'ok':'info',`ID ${K.esc(r.atmId)} · overall ${K.esc(r.status)}`)
    +kpi('Components',r.modules.length,'info')+kpi('Fatal',n('Fatal'),n('Fatal')?'crit':'ok')+kpi('Warning',n('Warning'),n('Warning')?'warn':'ok')+kpi('OK',n('OK'),'ok',n('Unknown')?`${n('Unknown')} unknown`:'');
   document.getElementById('modules').innerHTML=r.modules.map(card).join('');
   document.getElementById('updated').textContent=r.updatedUtc?'KTC data from '+K.local(r.updatedUtc):'';
   if(r.warnings&&r.warnings.length)msg.innerHTML=`<div class="alert alert-warning small">${r.warnings.map(K.esc).join('<br>')}</div>`;
   if(!r.modules.length)msg.innerHTML+='<div class="alert alert-secondary">KTC returned no component status for this ATM.</div>';
   if(focus){const el=document.querySelector('.hw-focus');if(el)el.scrollIntoView({block:'center',behavior:'smooth'});else msg.innerHTML+=`<div class="alert alert-secondary small">Component ${K.esc(focus)} is not in KTC's current status for this ATM.</div>`;focus=null;}
   history.replaceState(null,'','/Hardware?atmId='+encodeURIComponent(id));
  }catch(e){document.getElementById('kpis').innerHTML='';document.getElementById('modules').innerHTML='';msg.innerHTML=`<div class="alert alert-danger">${K.esc(e.message)}</div>`;}
  finally{document.getElementById('refresh').disabled=!cur;}}
 f.onsubmit=e=>{e.preventDefault();load();};
 document.getElementById('refresh').onclick=()=>{if(cur){atm.value=cur;load();}};
 K.api('/data/atms').then(r=>{document.getElementById('atmList').innerHTML=(r.data||[]).map(a=>`<option value="${K.esc(a.id)}">${K.esc(a.name)}</option>`).join('');}).catch(()=>{});
 if(atm.value.trim())load();
 setInterval(()=>{if(cur&&!document.hidden)load();},60000);
})();
