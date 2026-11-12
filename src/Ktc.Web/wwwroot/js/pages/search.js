(function(){const K=KTC,q=document.getElementById('q'),out=document.getElementById('out');
 async function go(){const t=q.value.trim();if(t.length<2){out.innerHTML='';return;}out.innerHTML='<div class="text-muted">Searching…</div>';
  try{const r=await K.api('/data/search?q='+encodeURIComponent(t));const g={};r.forEach(x=>(g[x.type]=g[x.type]||[]).push(x));
   out.innerHTML=Object.keys(g).map(k=>`<div class="cardx mb-3"><h6>${k}s (${g[k].length})</h6>${g[k].map(x=>`<a class="d-block py-1 border-bottom text-decoration-none" href="${K.esc(x.url)}"><b>${K.esc(x.title)}</b> <span class="text-muted small">${K.esc(x.subtitle)}</span></a>`).join('')}</div>`).join('')||'<div class="text-muted">No results.</div>';
  }catch(e){out.innerHTML=`<div class="alert alert-danger">${K.esc(e.message)}</div>`;}}
 q.addEventListener('keydown',e=>{if(e.key==='Enter')go();});if(q.value)go();})();
