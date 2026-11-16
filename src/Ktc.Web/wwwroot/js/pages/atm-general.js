// ATM 360 Overview: General details card. Data: GET /data/atm/{id}/general (KTC Client record, branch / business / region names).
(function(){const K=KTC,id=window.ATM_ID,$id=s=>document.getElementById(s);
 const ICON={Identity:'bi-pc-display',Network:'bi-hdd-network',Business:'bi-building',Location:'bi-geo-alt','Other KTC fields':'bi-list-ul'};
 function render(g){
  $id('genMeta').textContent=g.notSet.length?`${g.notSet.length} field(s) not set in KTC`:'all fields set in KTC';
  const m=$id('genMap');if(g.latitude!=null){m.href=`https://www.openstreetmap.org/?mlat=${g.latitude}&mlon=${g.longitude}#map=16/${g.latitude}/${g.longitude}`;m.classList.remove('d-none');}else m.classList.add('d-none');
  $id('general').innerHTML=(g.warnings.length?`<div class="col-12"><div class="alert alert-warning py-2 small mb-0">${g.warnings.map(K.esc).join('<br>')}</div></div>`:'')
   +g.groups.map(gr=>`<div class="col-md-6 col-xl-${gr.title==='Location'?4:gr.title==='Other KTC fields'?4:2}"><div class="small fw-semibold text-uppercase text-muted mb-1"><i class="bi ${ICON[gr.title]||'bi-list'}"></i> ${K.esc(gr.title)}</div>
    <table class="table table-sm small mb-0 counter-table"><tbody>${gr.fields.map(f=>`<tr><th class="fw-normal text-muted w-50">${K.esc(f.name)}</th><td class="text-break">${f.value?K.esc(f.value)+(f.id&&f.id!==f.value&&!f.value.includes(f.id)?` <span class="xfs-class">${K.esc(f.id)}</span>`:''):'<span class="text-muted fst-italic" title="KTC has no value for this field">not set</span>'}</td></tr>`).join('')}</tbody></table></div>`).join('');
 }
 K.api('/data/atm/'+encodeURIComponent(id)+'/general').then(render).catch(e=>{$id('general').innerHTML=`<div class="col-12"><div class="alert alert-danger small mb-0">General details unavailable: ${K.esc(e.message)}</div></div>`;});
})();
