(function(){const K=KTC;let dt,cur,rows={};
 const exM=new bootstrap.Modal(document.getElementById('execModal'));
 const params=u=>[...new Set([...u.matchAll(/\{(\w+)\}/g)].map(m=>m[1]))];
 dt=K.table('#tbl',{data:[],pageLength:25,order:[],columns:[{data:'module'},{data:'httpMethod',render:v=>`<span class="badge text-bg-${v==='GET'?'success':v==='POST'?'primary':v==='DELETE'?'danger':'warning'}">${v}</span>`},{data:'relativeUrl',render:(v,t,r)=>`<code>${K.esc(v)}</code>${r.isSensitive?' <span class="sbadge crit">blocked</span>':''}<div class="small text-muted">${K.esc(r.description||'')}</div>`},{data:'kind'},
  {data:'mappings',render:m=>m.map(x=>`<div class="small">${K.esc(x.screenName)} · ${x.widget} · ${x.mode}${x.refreshInterval?' · '+x.refreshInterval+'s':''}</div>`).join('')},
  {data:'enabled',render:(v,t,r)=>r.isSensitive?'—':window.IS_ADMIN?`<div class="form-check form-switch"><input class="form-check-input tg" type="checkbox" data-id="${r.id}" ${v?'checked':''}></div>`:(v?'Yes':'No')},
  {data:null,orderable:false,render:(v,t,r)=>window.CAN_EXEC&&r.enabled&&!r.isSensitive?`<button class="btn btn-sm btn-outline-primary run" data-id="${r.id}"><i class="bi bi-play-fill"></i></button>`:''}]});
 async function load(){const m=document.getElementById('mod').value;const r=await K.api('/data/api/endpoints'+(m?'?moduleId='+m:''));rows={};r.data.forEach(x=>rows[x.id]=x);K.reload(dt,r.data);}
 (async()=>{const mods=await K.api('/data/api/modules');const s=document.getElementById('mod');mods.forEach(m=>s.insertAdjacentHTML('beforeend',`<option value="${m.id}">${K.esc(m.displayName)} (${m.enabled}/${m.endpoints})</option>`));
  document.getElementById('modInfo').textContent=mods.length+' modules · '+mods.reduce((a,m)=>a+m.endpoints,0)+' endpoints · '+mods.reduce((a,m)=>a+m.enabled,0)+' enabled';await load();})().catch(e=>K.toast(e.message,'error'));
 document.getElementById('mod').onchange=load;
 $('#tbl tbody').on('change','.tg',async function(){try{await K.api('/data/api/toggle/'+this.dataset.id,{method:'POST'});await load();}catch(e){K.toast(e.message,'error');load();}});
 $('#tbl tbody').on('click','.run',function(){cur=rows[this.dataset.id];document.getElementById('exTitle').textContent=cur.endpointName;
  document.getElementById('exParams').innerHTML=params(cur.relativeUrl).map(p=>`<div class="col-md-3"><label class="form-label small">${p}</label><input class="form-control form-control-sm" data-p="${p}"></div>`).join('');
  const hasBody=cur.httpMethod!=='GET'&&cur.httpMethod!=='DELETE';document.getElementById('exBodyWrap').hidden=!hasBody;document.getElementById('exBody').value=hasBody?(cur.requestSchema||''):'';
  document.getElementById('exOut').textContent='';document.getElementById('exStatus').textContent='';
  document.getElementById('exMaps').innerHTML=cur.mappings.map(m=>`<div class="d-flex justify-content-between small border-bottom py-1"><span>${K.esc(m.screenName)} · ${m.widget}</span>${window.IS_ADMIN?`<button class="btn btn-sm btn-link text-danger p-0 dm" data-id="${m.id}">remove</button>`:''}</div>`).join('')||'<span class="text-muted small">No mappings</span>';
  if(window.IS_ADMIN)document.getElementById('exMaps').insertAdjacentHTML('beforeend',`<div class="row g-1 mt-2"><div class="col"><input class="form-control form-control-sm" id="mScreen" placeholder="Screen name"></div><div class="col"><select class="form-select form-select-sm" id="mWidget">${['Table','StatusCard','KpiWidget','Counter','GaugeChart','TrendChart','PerformanceGraph','BarChart','PieChart'].map((w,i)=>`<option value="${i+1}">${w}</option>`).join('')}</select></div><div class="col"><select class="form-select form-select-sm" id="mMode"><option value="3">Manual</option><option value="1">Realtime</option><option value="2">Scheduled</option></select></div><div class="col-auto"><button class="btn btn-sm btn-outline-primary" id="mAdd">Add mapping</button></div></div>`);
  exM.show();});
 const pv=()=>Object.fromEntries([...document.querySelectorAll('#exParams [data-p]')].map(i=>[i.dataset.p,i.value]));
 const link=fmt=>'/data/api/export/'+cur.id+'?format='+fmt+'&'+K.qs(Object.fromEntries(Object.entries(pv()).map(([k,v])=>['p['+k+']',v])));
 document.getElementById('exX').onclick=()=>location.href=link(2);document.getElementById('exC').onclick=()=>location.href=link(3);document.getElementById('exP').onclick=()=>location.href=link(1);
 document.getElementById('exRun').onclick=async()=>{const st=document.getElementById('exStatus'),out=document.getElementById('exOut');st.textContent='Executing…';
  try{const r=await K.api('/data/api/execute',{json:{endpointId:cur.id,parameters:pv(),body:document.getElementById('exBody').value||null,useCache:document.getElementById('exCache').checked}});
   st.innerHTML=(r.success?'<span class="sbadge ok">OK</span> ':'<span class="sbadge crit">Failed</span> ')+`HTTP ${r.statusCode} · ${r.elapsedMs} ms${r.fromCache?' · from cache':''}${r.error?' · '+K.esc(r.error):''}`;
   let b=r.body||'';try{b=JSON.stringify(JSON.parse(b),null,2);}catch(e){}out.textContent=b.length>200000?b.slice(0,200000)+'\n… truncated (use export)':b;}catch(e){st.innerHTML='<span class="sbadge crit">Error</span> '+K.esc(e.message);}};
 document.getElementById('exMaps').addEventListener('click',async e=>{
  if(e.target.id==='mAdd'){try{await K.api('/data/api/mapping',{json:{endpointId:cur.id,screenName:mScreen.value,widgetType:+mWidget.value,refreshInterval:60,executionMode:+mMode.value}});K.toast('Mapping added','ok');exM.hide();load();}catch(x){K.toast(x.message,'error');}}
  if(e.target.classList.contains('dm')){await K.api('/data/api/mapping/'+e.target.dataset.id,{method:'DELETE'});exM.hide();load();}});})();
