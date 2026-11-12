(function(){const K=KTC;let dt;
 async function load(){const r=await K.api('/data/rca?days='+document.getElementById('days').value),P=K.pal();
  const hb=(id,pts,c)=>K.chart(id,{type:'bar',data:{labels:pts.map(p=>p.label.length>34?p.label.slice(0,33)+'…':p.label),datasets:[{data:pts.map(p=>p.value),backgroundColor:c,borderRadius:4,maxBarThickness:20}]},options:{indexAxis:'y',scales:{x:{beginAtZero:true,ticks:{precision:0}}}}});
  hb('cCodes',r.topErrorCodes,P[1]);hb('cAtms',r.topAtms,P[0]);
  dt=K.table('#tbl',{data:r.recurring,order:[[3,'desc']],columns:[{data:'atmName',render:(v,t,x)=>`<a href="/Atm/Detail/${encodeURIComponent(x.atmId)}">${K.esc(v||x.atmId)}</a>`},{data:'client'},{data:'description',render:K.esc},{data:'occurrences',render:v=>`<b>${v}</b>`},{data:'sharePct',render:v=>v+'%'},{data:'lastSeenUtc',render:K.local},{data:null,orderable:false,render:(v,t,x)=>`<a class="btn btn-sm btn-outline-secondary" href="/Tickets?atmId=${encodeURIComponent(x.atmId)}">Tickets</a>`}]});}
 document.getElementById('days').onchange=()=>load().catch(e=>K.toast(e.message,'error'));load().catch(e=>K.toast(e.message,'error'));document.addEventListener('ktc-theme',load);})();
