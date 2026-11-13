(function(){const K=KTC,f=document.getElementById('f');let data,cur=null;
 const iso=v=>v?new Date(v).toISOString():'';
 const now=new Date(),ago=new Date(now-6*3600e3),loc=d=>new Date(d-d.getTimezoneOffset()*60000).toISOString().slice(0,16);
 from.value=loc(ago);to.value=loc(now);
 const params=()=>K.qs({atmId:atm.value.trim(),from:iso(from.value),to:iso(to.value),card:card.value.trim(),seq:seq.value.trim(),text:text.value.trim()});
 const tlHtml=t=>t.map(s=>`<li class="${s.level}"><b>${K.esc(s.label)}</b> <span class="text-muted small">${new Date(s.timeUtc.endsWith('Z')?s.timeUtc:s.timeUtc+'Z').toLocaleTimeString()}</span><div class="small">${K.esc(s.detail)}</div></li>`).join('');
 function show(no){cur=no;const ents=data.entries.filter(e=>no==null||e.transactionNo===no);
  document.getElementById('term').textContent=ents.map(e=>`${new Date(e.timeUtc.endsWith('Z')?e.timeUtc:e.timeUtc+'Z').toLocaleTimeString()}  #${e.sequence}  ${e.text}`).join('\n');
  const tx=data.transactions.find(t=>t.no===no);document.getElementById('tl').innerHTML=tx?tlHtml(tx.timeline):'';
  document.getElementById('rTitle').textContent=no==null?`EJ entries (${ents.length})`:`Transaction #${no} (${ents.length} entries)`;
  document.querySelectorAll('#txList .tx').forEach(e=>e.classList.toggle('active',+e.dataset.no===no));}
 f.onsubmit=async e=>{e.preventDefault();const m=document.getElementById('msg');m.innerHTML='';
  if(!atm.value.trim()){m.innerHTML='<div class="alert alert-warning">ATM ID or name is required – the KTC EJ API is queried per ATM.</div>';return;}
  document.getElementById('results').hidden=true;m.innerHTML='<div class="text-muted text-center">Searching journal…</div>';
  try{data=await K.api('/data/ej?'+params());m.innerHTML=data.truncated?'<div class="alert alert-info py-2">Showing first 5000 entries – narrow the filters.</div>':'';
   if(!data.entries.length){m.innerHTML+=(card.value.trim()||seq.value.trim()||text.value.trim())?'<div class="alert alert-secondary">No journal entries match.</div>':'<div class="alert alert-secondary">KTC has no EJ data for this ATM in the selected range. Not every ATM uploads EJ; try a wider range (up to 7 days) or another ATM.</div>';return;}
   document.getElementById('results').hidden=false;
   document.getElementById('txList').innerHTML=data.transactions.map(t=>{const bad=t.timeline.some(s=>s.level==='danger');return `<div class="tx border rounded p-2 mb-2" style="cursor:pointer" data-no="${t.no}"><div class="d-flex justify-content-between"><b>#${t.no}</b><span class="sbadge ${bad?'crit':'ok'}">${bad?'Issue':'OK'}</span></div><div class="small text-muted">${K.local(t.start)} · ${t.count} lines</div></div>`;}).join('');
   document.querySelectorAll('#txList .tx').forEach(e=>e.onclick=()=>show(+e.dataset.no));show(data.transactions[0]?.no??null);
  }catch(x){m.innerHTML=`<div class="alert alert-danger">${K.esc(x.message)}</div>`;}};
 document.querySelectorAll('[data-x]').forEach(b=>b.onclick=()=>{location.href='/Ej/Export?'+params()+'&format='+b.dataset.x;});})();
