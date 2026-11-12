(function(){const K=KTC;
 document.querySelectorAll('#rules .save').forEach(b=>b.onclick=async()=>{const tr=b.closest('tr'),g=n=>tr.querySelector(`[data-f=${n}]`);
  try{await K.api('/Alerts/SaveRule',{json:{id:+tr.dataset.id,threshold:+g('threshold').value,windowMinutes:+g('window').value,cooldownMinutes:+g('cool').value,severity:+g('sev').value,channels:(g('email').checked?2:0)|(g('teams').checked?4:0)|1,recipients:g('rec').value,enabled:g('on').checked}});K.toast('Saved','ok');}catch(e){K.toast(e.message,'error');}});
 document.getElementById('test').onclick=async()=>{try{const r=await K.api('/Alerts/TestNotification',{method:'POST'});K.toast('Result: '+r.status,'info');}catch(e){K.toast(e.message,'error');}};})();
