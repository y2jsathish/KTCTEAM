(function(){const K=KTC;let dt,dt2;
 const kpi=(l,v,c)=>`<div class="kpi ${c}"><div class="l">${l}</div><div class="v">${v}</div></div>`;
 async function load(){
  const [s,inc]=await Promise.all([K.api('/data/summary'),K.api('/data/incidents')]);
  document.getElementById('kpis').innerHTML=kpi('Availability %',s.availabilityPct.toFixed(2),s.availabilityPct>=99?'ok':s.availabilityPct>=95?'warn':'crit')+kpi('SLA %',s.slaPct,s.slaPct>=95?'ok':s.slaPct>=85?'warn':'crit')+kpi('Critical issues',s.criticalIncidents,s.criticalIncidents?'crit':'ok')+kpi('Critical ATMs',s.criticalAtms,s.criticalAtms?'crit':'ok')+kpi('Open tickets',s.openTickets,'info');
  const P=K.pal(), n=s.clients.map(c=>c.name);
  const bar=(id,key,color)=>K.chart(id,{type:'bar',data:{labels:n,datasets:[{data:s.clients.map(c=>c[key]),backgroundColor:color,borderRadius:4,maxBarThickness:40}]},options:{scales:{y:{min:Math.max(0,Math.floor(Math.min(...s.clients.map(c=>c[key]),100)-5)),max:100}}}});
  bar('cAv','availabilityPct',P[0]);bar('cSla','slaPct',P[2]);
  dt=K.table('#tbl',{data:s.clients,paging:false,searching:false,info:false,columns:[{data:'name'},{data:'atms'},{data:'online'},{data:'availabilityPct',render:v=>v.toFixed(2)},{data:'openIncidents'},{data:'openTickets'},{data:'slaPct',render:v=>v.toFixed(1)},{data:'critical'}]});
  const crit=inc.data.filter(i=>i.severity==='P1'||i.severity==='P2');
  dt2=K.table('#tblCrit',{data:crit,order:[[4,'asc']],columns:[{data:'number'},{data:'atmName',render:(v,t,r)=>`<a href="/Atm/Detail/${encodeURIComponent(r.atmId)}">${K.esc(v)}</a>`},{data:'client'},{data:'severity',render:K.sev},{data:'openedUtc',render:K.local},{data:'description',render:K.esc}]});
 }
 load().catch(e=>K.toast(e.message,'error'));setInterval(()=>load().catch(()=>{}),60000);document.addEventListener('ktc-theme',load);})();
