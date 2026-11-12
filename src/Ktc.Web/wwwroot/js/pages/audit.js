(function(){const K=KTC;let dt;const f=document.getElementById('f');
 dt=K.table('#tbl',{data:[],order:[[0,'desc']],columns:[{data:'timestampUtc',render:(v,t)=>t==='display'?K.local(v):v},{data:'userName'},{data:'category'},{data:'action',render:K.esc},{data:'target',render:v=>K.esc(v||'')},{data:'details',render:v=>`<span class="small">${K.esc(v||'')}</span>`},{data:'ipAddress'},{data:'success',render:v=>v?'<span class="sbadge ok">OK</span>':'<span class="sbadge crit">Fail</span>'}]});
 async function load(){const r=await K.api('/data/audit?'+K.qs(Object.fromEntries(new FormData(f).entries())));K.reload(dt,r.data);}
 f.onsubmit=e=>{e.preventDefault();load().catch(x=>K.toast(x.message,'error'));};load().catch(e=>K.toast(e.message,'error'));})();
