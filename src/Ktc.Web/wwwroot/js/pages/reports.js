(function(){const K=KTC;const loc=d=>new Date(d-d.getTimezoneOffset()*60000).toISOString().slice(0,16);
 from.value=loc(new Date(Date.now()-86400e3));to.value=loc(new Date());
 const p=extra=>K.qs(Object.assign({type:type.value,from:new Date(from.value).toISOString(),to:new Date(to.value).toISOString()},extra));
 document.querySelectorAll('[data-fmt]').forEach(b=>b.onclick=()=>{location.href='/Reports/Run?'+p({format:b.dataset.fmt});});
 document.getElementById('prev').onclick=async()=>{try{const r=await K.api('/Reports/Run?'+p({preview:true}));
  document.getElementById('preview').hidden=false;document.getElementById('pTitle').textContent=r.title+(r.subtitle?' · '+r.subtitle:'');
  document.getElementById('pSum').innerHTML=r.summary.map(s=>`<div class="kpi info"><div class="l">${K.esc(s.item1||s.label)}</div><div class="v" style="font-size:1.3rem">${K.esc(s.item2||s.value)}</div></div>`).join('');
  document.getElementById('pTbl').innerHTML='<thead><tr>'+r.columns.map(c=>`<th>${K.esc(c)}</th>`).join('')+'</tr></thead><tbody>'+r.rows.map(x=>'<tr>'+x.map(c=>`<td>${K.esc(c)}</td>`).join('')+'</tr>').join('')+'</tbody>'+(r.total>r.rows.length?`<caption>Showing ${r.rows.length} of ${r.total} rows – export for the full data.</caption>`:'');
 }catch(e){K.toast(e.message,'error');}};})();
