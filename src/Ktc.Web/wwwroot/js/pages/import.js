(function(){const K=KTC;document.getElementById('f').onsubmit=async e=>{e.preventDefault();const fd=new FormData();fd.append('file',file.files[0]);
 try{const r=await K.api('/data/api/import',{method:'POST',body:fd});
  res.innerHTML=`<div class="alert alert-success"><b>Import complete.</b><ul class="mb-0"><li>${r.modules} modules, ${r.endpoints} endpoints in file</li><li>${r.added} added · ${r.updated} existing</li><li>${r.enabled} read-only endpoints enabled automatically</li><li>${r.disabledMutating} mutating endpoints imported <b>disabled</b></li><li>${r.sensitiveBlocked} credential/key endpoints permanently blocked</li><li>${r.widgets} widgets generated</li></ul></div>`;
 }catch(x){res.innerHTML=`<div class="alert alert-danger">${K.esc(x.message)}</div>`;}};})();
