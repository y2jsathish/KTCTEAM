// ATM 360, Actions tab: what happened after sending.
// Activity: GET /data/actions/atm/{id}/history (KTC GET Client/{id}/Actions) with status, progress, retries, times, who / ticket /
// reason and KTC's result; refreshed every 5 s while anything is still running.
// Files: GET /data/actions/action/{id}/files and /file?name= (text only, PAN-masked, track data removed; download is the masked copy).
// Guided reboot: POST /data/actions/reboot, then GET /data/actions/reboot/{id} for the timeline (out of service, reboot, offline,
// back online, devices, in service); the server runs the steps, so closing the page does not stop it.
(function(){const K=KTC,id=window.ATM_ID,$id=s=>document.getElementById(s);let loaded=false,timer=null,catalog=null,hist=null,watch=null;
 const t=u=>{if(!u)return '';const d=new Date(/Z$|[+-]\d\d:\d\d$/.test(u)?u:u+'Z');return d.toLocaleString([],{dateStyle:'short',timeStyle:'medium'});};
 const dur=(a,b)=>{if(!a||!b)return '';const s=Math.max(0,Math.round((new Date(b.endsWith('Z')?b:b+'Z')-new Date(a.endsWith('Z')?a:a+'Z'))/1000));return s<90?s+' s':Math.round(s/60)+' min';};
 const badge=a=>a.failed?'crit':a.final?(/cancel/i.test(a.status)?'unknown':'ok'):'warn';
 const STEPS=[['WaitingForIdle','Wait for idle'],['TakingOutOfService','Out of service'],['ReCheckingIdle','Idle re-check'],['Rebooting','Reboot'],
  ['WaitingForOffline','Going offline'],['WaitingForOnline','Coming back'],['CheckingDevices','Device check'],['PuttingInService','In service'],['Done','Done']];
 const hms=u=>u?new Date(/Z$|[+-]\d\d:\d\d$/.test(u)?u:u+'Z').toLocaleTimeString():'';
 const stateCls={Running:'warn',Completed:'ok',Failed:'crit',Cancelled:'unknown',NeedsAttention:'crit'};
 const stateText={Running:'Running',Completed:'Completed',Failed:'Failed',Cancelled:'Cancelled',NeedsAttention:'Needs attention'};

 // ---------- activity ----------
 function row(a){
  const prog=a.final?'':`<div class="progress mt-1" style="height:6px" role="progressbar" aria-valuenow="${a.progressPercent||0}" aria-valuemin="0" aria-valuemax="100"><div class="progress-bar progress-bar-striped progress-bar-animated" style="width:${Math.max(5,a.progressPercent||0)}%"></div></div>`;
  return `<tr><td class="text-nowrap">${K.esc(t(a.addedUtc))}<div class="text-muted">#${K.esc(a.id)}</div></td>
   <td><b>${K.esc(a.commandLabel)}</b><div><code>${K.esc(a.commandName)}</code></div>${a.parameters?`<div class="text-muted text-break">${K.esc(a.parameters)}</div>`:''}</td>
   <td style="min-width:9rem"><span class="sbadge ${badge(a)}">${K.esc(a.status)}</span>${a.progressPercent!=null&&!a.final?` <span class="text-muted">${K.esc(a.progressPercent)}%</span>`:''}${prog}
    ${a.retryCount?`<div class="text-muted">Retries: ${K.esc(a.retryCount)}</div>`:''}</td>
   <td class="text-nowrap">${a.startUtc?`Started ${K.esc(t(a.startUtc))}`:'<span class="text-muted">Not started</span>'}${a.endUtc?`<div>Ended ${K.esc(t(a.endUtc))} <span class="text-muted">(${dur(a.startUtc||a.addedUtc,a.endUtc)})</span></div>`:''}</td>
   <td>${a.user?K.esc(a.user):'<span class="text-muted">–</span>'}${a.ticket?`<div>Ticket <b>${K.esc(a.ticket)}</b></div>`:''}${a.reason?`<div class="text-muted">${K.esc(a.reason)}</div>`:''}</td>
   <td>${a.result?`<div class="text-break" style="max-width:28rem;white-space:pre-wrap">${K.esc(a.result)}</div>`:'<span class="text-muted">–</span>'}
    ${a.hasFiles?`<button class="btn btn-sm btn-outline-primary mt-1" data-files="${K.esc(a.id)}" data-name="${K.esc(a.commandLabel)}"><i class="bi bi-folder2-open"></i> Files</button>`:''}</td></tr>`;
 }
 function renderHistory(h){
  hist=h;
  $id('actHistMeta').textContent=`${h.actions.length} action${h.actions.length===1?'':'s'} · read ${new Date().toLocaleTimeString()}${h.demo?' · demo data':''}`;
  $id('actHistMsg').innerHTML=(h.warnings||[]).length?`<div class="alert alert-warning py-2 small mb-0">${h.warnings.map(K.esc).join('<br>')}</div>`:'';
  $id('actReboots').innerHTML=(h.reboots||[]).slice(0,3).map(timeline).join('');
  $id('actHistory').innerHTML=h.actions.length?`<div class="table-responsive"><table class="table table-sm small align-top mb-0"><thead><tr><th>Added</th><th>Command</th><th>Status</th><th>Times</th><th>Requested by</th><th>Result</th></tr></thead>
   <tbody>${h.actions.map(row).join('')}</tbody></table></div>`:'<div class="text-muted small">No actions on this ATM in this period.</div>';
  schedule();
 }
 function schedule(){
  clearTimeout(timer);
  const busy=hist&&(hist.actions.some(a=>!a.final)||(hist.reboots||[]).some(r=>r.state==='Running'));
  const visible=$id('tab-actions').classList.contains('active')&&!document.hidden;
  if(busy&&visible)timer=setTimeout(()=>loadHistory(true),5000);
 }
 async function loadHistory(quiet){
  loaded=true;if(!quiet)$id('actHistory').innerHTML='<div class="text-muted small">Loading…</div>';
  try{renderHistory(await K.api(`/data/actions/atm/${encodeURIComponent(id)}/history?days=${$id('actDays').value}`));}
  catch(e){$id('actHistMsg').innerHTML=`<div class="alert alert-danger py-2 small mb-0">Actions unavailable: ${K.esc(e.message)}</div>`;if(!quiet)$id('actHistory').innerHTML='';}
 }

 // ---------- files ----------
 let filesFor=null;
 async function openFiles(actionId,name){
  filesFor=actionId;$id('actFilesTitle').innerHTML=`<i class="bi bi-folder2-open"></i> Files from ${K.esc(name)} <span class="text-muted small">action #${K.esc(actionId)}</span>`;
  $id('actFileList').innerHTML='<div class="text-muted small">Reading the file list from KTC…</div>';$id('actFileView').innerHTML='';
  bootstrap.Modal.getOrCreateInstance($id('actFiles')).show();
  try{const fs=await K.api(`/data/actions/action/${encodeURIComponent(actionId)}/files`);
   $id('actFileList').innerHTML=fs.length?`<table class="table table-sm small mb-0"><thead><tr><th>File</th><th class="text-end">Size</th><th>Modified</th><th></th></tr></thead><tbody>${fs.map(f=>`<tr>
    <td><i class="bi ${f.viewable?'bi-file-earmark-text':'bi-file-earmark-lock'}"></i> ${K.esc(f.name)}</td><td class="text-end num">${f.size!=null?K.esc(size(f.size)):''}</td><td>${K.esc(t(f.modifiedUtc))}</td>
    <td class="text-end">${f.viewable?`<button class="btn btn-sm btn-outline-primary" data-view="${K.esc(f.name)}"><i class="bi bi-eye"></i> View</button>
     <a class="btn btn-sm btn-outline-secondary" href="/data/actions/action/${encodeURIComponent(actionId)}/file?name=${encodeURIComponent(f.name)}&download=true"><i class="bi bi-download"></i> Download (masked)</a>`
     :`<span class="text-muted">${K.esc(f.blockedReason||'Not opened here')}</span>`}</td></tr>`).join('')}</tbody></table>`
    :'<div class="text-muted small">KTC returned no files for this action (it may still be uploading).</div>';
  }catch(e){$id('actFileList').innerHTML=`<div class="alert alert-danger py-2 small">${K.esc(e.message)}</div>`;}
 }
 const size=n=>n<1024?n+' B':n<1048576?(n/1024).toFixed(1)+' KB':(n/1048576).toFixed(1)+' MB';
 async function viewFile(name){
  $id('actFileView').innerHTML='<div class="text-muted small">Opening…</div>';
  try{const f=await K.api(`/data/actions/action/${encodeURIComponent(filesFor)}/file?name=${encodeURIComponent(name)}`);
   $id('actFileView').innerHTML=f.text==null?`<div class="alert alert-warning py-2 small">${K.esc(f.blockedReason)}</div>`
    :`<div class="d-flex flex-wrap gap-2 small mb-1"><b>${K.esc(f.name)}</b><span class="text-muted">${K.esc(size(f.size))}</span>
      <span class="sbadge ${f.maskedCount?'warn':'ok'}">${f.maskedCount?`${K.esc(f.maskedCount)} card number${f.maskedCount===1?'':'s'} / track data masked`:'No card data found'}</span>
      ${f.truncated?'<span class="text-muted">Preview shows the first lines; download for the whole (masked) file.</span>':''}<span class="text-muted ms-auto" title="SHA-256 of the file as received from KTC">sha256 ${K.esc(f.sha256.slice(0,16))}…</span></div>
     <pre class="border rounded p-2 small mb-0" style="max-height:55vh;overflow:auto;white-space:pre-wrap">${K.esc(f.text)}</pre>`;
  }catch(e){$id('actFileView').innerHTML=`<div class="alert alert-danger py-2 small">${K.esc(e.message)}</div>`;}
 }

 // ---------- guided reboot ----------
 function timeline(r){
  const at=STEPS.findIndex(s=>s[0]===r.step),done=r.state==='Completed';
  const stamp={TakingOutOfService:r.outOfServiceUtc,Rebooting:r.rebootSentUtc,WaitingForOffline:r.offlineUtc,WaitingForOnline:r.onlineUtc,PuttingInService:r.inServiceUtc,Done:done?r.updatedUtc:null};
  const steps=STEPS.map(([k,l],i)=>{const st=done||i<at?'done':i===at?(r.state==='Running'?'now':r.state==='Cancelled'?'skip':'bad'):'todo';
   const ic={done:'bi-check-circle-fill text-success',now:'bi-arrow-repeat text-primary',bad:'bi-x-circle-fill text-danger',skip:'bi-dash-circle text-muted',todo:'bi-circle text-muted'}[st];
   return `<div class="text-center px-1" style="min-width:5.5rem;flex:1"><i class="bi ${ic}${st==='now'?' spin':''}" style="font-size:1.2rem"></i><div class="small${st==='todo'?' text-muted':''}">${K.esc(l)}</div>${stamp[k]?`<div class="text-muted" style="font-size:.7rem">${K.esc(hms(stamp[k]))}</div>`:''}</div>`;}).join('');
  const canCancel=r.state==='Running'&&['Starting','WaitingForIdle','TakingOutOfService','ReCheckingIdle'].includes(r.step);
  return `<div class="border rounded p-2 mb-2" data-run="${K.esc(r.id)}">
   <div class="d-flex flex-wrap align-items-center gap-2 small"><i class="bi bi-arrow-repeat text-danger"></i><b>Guided reboot</b><span class="sbadge ${stateCls[r.state]||'unknown'}">${K.esc(stateText[r.state]||r.state)}</span>
    <span>${r.ticket?`Ticket <b>${K.esc(r.ticket)}</b> · `:''}${K.esc(r.reason)}</span><span class="text-muted">by ${K.esc(r.user)} · started ${K.esc(t(r.createdUtc))}${r.finished?` · took ${dur(r.createdUtc,r.updatedUtc)}`:''}</span>
    ${canCancel?`<button class="btn btn-sm btn-outline-secondary ms-auto" data-cancel="${K.esc(r.id)}"><i class="bi bi-x"></i> Cancel</button>`:''}</div>
   <div class="d-flex flex-wrap my-2">${steps}</div>
   <details${r.state!=='Completed'?' open':''}><summary class="small text-muted">Audit trail (${r.events.length})</summary><ul class="list-unstyled small mb-0 mt-1">${r.events.map(e=>`<li><span class="text-muted text-nowrap">${K.esc(t(e.utc))}</span> <i class="bi ${e.ok?'bi-check text-success':'bi-exclamation-triangle text-danger'}"></i> ${K.esc(e.message)}${e.actionId?` <span class="text-muted">(KTC action #${K.esc(e.actionId)})</span>`:''}</li>`).join('')}</ul></details></div>`;
 }
 function openReboot(){
  $id('rbResult').innerHTML='';$id('rbAtm').value='';$id('rbStart').disabled=false;
  if(!$id('rbTicket').value)$id('rbTicket').value=$id('actTicket').value;
  bootstrap.Modal.getOrCreateInstance($id('actReboot')).show();
 }
 async function startReboot(){
  const body={atmId:id,ticketNumber:$id('rbTicket').value.trim(),reason:$id('rbReason').value.trim(),confirmAtmId:$id('rbAtm').value.trim()};
  $id('rbStart').disabled=true;$id('rbResult').innerHTML='<div class="text-muted small">Running the checks…</div>';
  try{const {run,result}=await K.api('/data/actions/reboot',{json:body});
   $id('rbResult').innerHTML=`<div class="alert ${run?'alert-success':'alert-warning'} py-2 small mb-2">${K.esc(result.message)} <span class="text-muted">· ref ${K.esc(result.correlationId)}</span></div>
    <ul class="list-unstyled small mb-0">${result.checks.map(c=>`<li><i class="bi ${c.passed?'bi-check-circle text-success':'bi-x-circle text-danger'}"></i> <b>${K.esc(c.name)}</b>: ${K.esc(c.detail)}</li>`).join('')}</ul>`;
   if(run){K.toast('Guided reboot started','ok');$id('rbReason').value='';setTimeout(()=>bootstrap.Modal.getInstance($id('actReboot'))?.hide(),900);follow(run.id);loadHistory(true);}
   else $id('rbStart').disabled=false;
  }catch(e){$id('rbResult').innerHTML=`<div class="alert alert-danger py-2 small">${K.esc(e.message)}</div>`;$id('rbStart').disabled=false;}
 }
 // Follows one run every 3 s (faster than the list) so each step shows as it happens.
 function follow(runId){
  clearTimeout(watch);
  const tick=async()=>{try{const r=await K.api(`/data/actions/reboot/${encodeURIComponent(runId)}`);
    if(hist){hist.reboots=[r,...(hist.reboots||[]).filter(x=>x.id!==r.id)];$id('actReboots').innerHTML=hist.reboots.slice(0,3).map(timeline).join('');}
    if(r.state==='Running')watch=setTimeout(tick,3000);else{loadHistory(true);K.toast(`Guided reboot: ${stateText[r.state]||r.state}`,r.state==='Completed'?'ok':'error');}
   }catch(e){watch=setTimeout(tick,6000);}};
  tick();
 }
 async function cancel(runId){
  try{await K.api(`/data/actions/reboot/${encodeURIComponent(runId)}/cancel`,{method:'POST'});K.toast('Cancel requested. If the ATM is already out of service it is put back in service.');follow(runId);}
  catch(e){K.toast(e.message,'error');}
 }

 // ---------- wiring ----------
 document.addEventListener('ktc:actions-catalog',e=>{catalog=e.detail;});
 document.addEventListener('ktc:action-sent',()=>setTimeout(()=>loadHistory(true),800));
 $id('tab-actions').addEventListener('click',e=>{
  const f=e.target.closest('[data-files]');if(f){openFiles(f.dataset.files,f.dataset.name);return;}
  const c=e.target.closest('[data-cancel]');if(c){cancel(c.dataset.cancel);return;}
  if(e.target.closest('[data-reboot]'))openReboot();
 });
 $id('actFileList').addEventListener('click',e=>{const v=e.target.closest('[data-view]');if(v)viewFile(v.dataset.view);});
 $id('rbStart').onclick=startReboot;
 $id('actDays').onchange=()=>loadHistory(false);
 document.querySelector('[data-acthistrefresh]').onclick=()=>loadHistory(false);
 document.addEventListener('visibilitychange',schedule);
 const tab=document.querySelector('[data-tab="actions"]');
 tab.addEventListener('shown.bs.tab',()=>{if(!loaded)loadHistory(false);else schedule();
  const run=hist&&(hist.reboots||[]).find(r=>r.state==='Running');if(run)follow(run.id);});
 tab.addEventListener('hidden.bs.tab',()=>{clearTimeout(timer);clearTimeout(watch);});
 if($id('tab-actions').classList.contains('active'))loadHistory(false);
})();
