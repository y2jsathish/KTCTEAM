(function(){const K=KTC;let dt;
 const F=['fClient','fCity','fRegion','fStatus'].map(id=>document.getElementById(id));
 function q(){return K.qs({client:F[0].value,city:F[1].value,region:F[2].value,status:F[3].value});}
 async function load(){const r=await K.api('/data/atms?'+q());K.reload(dt,r.data);document.getElementById('cnt').textContent=r.data.length+' ATMs';}
 async function init(){
  const f=await K.api('/data/filters');[['fClient','clients'],['fCity','cities'],['fRegion','regions']].forEach(([id,k])=>{const s=document.getElementById(id);f[k].forEach(v=>s.insertAdjacentHTML('beforeend',`<option>${K.esc(v)}</option>`));});
  dt=K.table('#tbl',{data:[],order:[[3,'desc']],createdRow:(tr,d)=>tr.classList.add('row-'+d.health.toLowerCase()),columns:[{data:'id'},{data:'name',render:(v,t,r)=>`<a href="/Atm/Detail/${encodeURIComponent(r.id)}"><b>${K.esc(v)}</b></a>`},{data:'deviceStatus',render:K.esc},
   {data:'health',render:(v,t,r)=>t==='display'?K.health(v)+` <small class="text-muted">${r.healthScore}</small>`:v},{data:'lastCommunicationUtc',render:(v,t)=>t==='display'?K.ago(v):v},{data:'location',render:K.esc},{data:'client'},{data:'atmType',render:K.esc},
   {data:'availabilityPct',render:(v,t)=>v==null?(t==='display'?'<span class="text-muted">n/a</span>':-1):(t==='display'?v.toFixed(2)+'%':v)},{data:'openIncidents'},{data:'openTickets'}]});
  $('#tbl tbody').on('click','tr',function(){const d=dt.row(this).data();if(!d)return;
   K.details(d.name,`<div class="mb-2">${K.health(d.health)} <span class="ms-1">${K.esc(d.healthReason)}</span></div><dl class="row mb-2"><dt class="col-5">ATM ID</dt><dd class="col-7">${d.id}</dd><dt class="col-5">Client</dt><dd class="col-7">${K.esc(d.client)}</dd><dt class="col-5">Location</dt><dd class="col-7">${K.esc(d.location)}, ${K.esc(d.city)}</dd><dt class="col-5">Region</dt><dd class="col-7">${K.esc(d.region)}</dd><dt class="col-5">Type</dt><dd class="col-7">${K.esc(d.atmType)}</dd><dt class="col-5">Last comm.</dt><dd class="col-7">${K.local(d.lastCommunicationUtc)}</dd><dt class="col-5">Health score</dt><dd class="col-7">${d.healthScore}/100</dd></dl><a class="btn btn-sm btn-primary w-100" href="/Atm/Detail/${encodeURIComponent(d.id)}">Open ATM 360 view</a>`);});
  F.forEach(e=>e.onchange=()=>load());await load();}
 init().catch(e=>K.toast(e.message,'error'));setInterval(()=>load().catch(()=>{}),60000);K.hooks.dashboard.push(()=>load().catch(()=>{}));})();
