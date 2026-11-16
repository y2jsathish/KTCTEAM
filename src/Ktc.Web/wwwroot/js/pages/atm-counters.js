// ATM 360: Application Counters and XFS Counters tabs. Data: GET /data/atm/{id}/counters (KTC counters, CurrentStatus, cash units and transactions); export: /Atm/CountersExport/{id}.
(function(){const K=KTC,id=window.ATM_ID,$id=s=>document.getElementById(s);
 const cls={OK:'ok',Warning:'warn'};
 const badge=s=>`<span class="sbadge ${cls[s]||'unknown'}">${K.esc(s)}</span>`;
 const n=v=>v==null||v<0?'<span class="text-muted" title="Not available from KTC">n/a</span>':Number(v).toLocaleString();
 const sum=(rows,f)=>rows.reduce((s,x)=>s+(x[f]>0?x[f]:0),0);
 const kpi=(l,v,c,d)=>`<div class="kpi ${c}"><div class="l">${l}</div><div class="v">${v}</div><div class="d">${d||'&nbsp;'}</div></div>`;
 const CATS=['Transactions','Cash','Exceptions','Operations'],ICON={Transactions:'bi-arrow-left-right',Cash:'bi-cash-stack',Exceptions:'bi-exclamation-triangle',Operations:'bi-tools'};
 // XFS device classes in card order, with the names shown on the device picker. Dispenser, acceptor and cheque scanner are ticked by default.
 const CLASSES=[['CDM','Cash dispenser'],['CIM','Cash acceptor'],['IPM','Cheque scanner'],['IDC','Card reader'],['PTR','Printer'],['PIN','PIN pad'],['SIU','Sensors']];
 const ORDER=CLASSES.map(c=>c[0]),DEFAULT=['CDM','CIM','IPM'],KEY='ktc.xfsDevices';
 const rank=(arr,v)=>{const i=arr.indexOf(v);return i<0?99:i;};
 let last=null,picked=(()=>{try{const v=JSON.parse(localStorage.getItem(KEY)||'null');if(Array.isArray(v))return v.filter(x=>ORDER.includes(x));}catch(e){}return DEFAULT.slice();})();
 function save(){try{localStorage.setItem(KEY,JSON.stringify(picked));}catch(e){}}

 // kind: 'app' or 'xfs' - each tab shows the reset point / report time and the warnings that concern it.
 function meta(c,kind){return [kind==='app'&&c.lastResetUtc?`<i class="bi bi-arrow-counterclockwise"></i> Since reset = since ${c.resetSource?K.esc(c.resetSource):K.local(c.lastResetUtc)}`:'',kind==='app'?'Today = since 00:00 UTC':'',
   kind==='app'&&c.notesSource?`<i class="bi bi-cash-stack"></i> Notes from ${K.esc(c.notesSource)}`:'',
   kind==='xfs'&&c.collectedUtc?`<i class="bi bi-clock"></i> Last reported ${K.local(c.collectedUtc)}`:''].filter(Boolean).join(' <span class="mx-1">·</span> ');}
 function warn(c,kind){const mine=(c.warnings||[]).filter(w=>kind==='app'?!/^XFS counters/.test(w):!/^Application counters/.test(w));
  const d=(c.diagnostics||[]).length?`<details class="small text-muted mb-2"><summary>What KTC returned</summary><ul class="mb-0 ps-3">${c.diagnostics.map(x=>`<li>${K.esc(x)}</li>`).join('')}</ul></details>`:'';
  return (mine.length?`<div class="alert alert-warning py-2 small">${mine.map(K.esc).join('<br>')}</div>`:'')+d;}

 function renderApp(c){
  $id('appMeta').innerHTML=meta(c,'app');$id('appMsg').innerHTML=warn(c,'app');
  if(!c.application.length){$id('appCounters').innerHTML='<div class="col-12"><div class="alert alert-secondary small mb-0">No application counters for this ATM (see the reason above).</div></div>';return;}
  const groups={};c.application.forEach(x=>(groups[x.category]=groups[x.category]||[]).push(x));
  let html=Object.keys(groups).sort((a,b)=>rank(CATS,a)-rank(CATS,b)).map(g=>{const rows=groups[g];
   return `<div class="col-lg-${Object.keys(groups).length>3?3:4} col-md-6"><div class="cardx h-100"><h6><i class="bi ${ICON[g]||'bi-list'}"></i> ${K.esc(g==='Cash'?'Cash (notes)':g)}</h6>
    <table class="table table-sm small mb-0 counter-table"><thead><tr><th>Counter</th><th class="text-end">Today</th><th class="text-end">Since reset</th></tr></thead>
    <tbody>${rows.map(x=>`<tr><td>${K.esc(x.name)}</td><td class="text-end num">${n(x.today)}</td><td class="text-end num">${n(x.sinceReset)}</td></tr>`).join('')}</tbody>
    ${g==='Cash'?'':`<tfoot><tr><th>Total</th><th class="text-end num">${n(sum(rows,'today'))}</th><th class="text-end num">${n(sum(rows,'sinceReset'))}</th></tr></tfoot>`}</table></div></div>`;}).join('');
  const bt=c.notesByType||[];
  if(bt.length)html+=`<div class="col-12"><div class="cardx"><h6><i class="bi bi-cash-stack"></i> Notes by transaction type (since reset)</h6>
    <table class="table table-sm small mb-0 counter-table"><thead><tr><th>Transaction type</th><th class="text-end">Transactions</th><th class="text-end">Notes dispensed</th><th class="text-end">Notes accepted</th><th class="text-end">Notes per transaction</th></tr></thead>
    <tbody>${bt.map(x=>`<tr><td>${K.esc(x.type)}</td><td class="text-end num">${n(x.transactions)}</td><td class="text-end num">${n(x.notesDispensed)}</td><td class="text-end num">${n(x.notesAccepted)}</td><td class="text-end num">${x.transactions?((x.notesDispensed+x.notesAccepted)/x.transactions).toFixed(1):'–'}</td></tr>`).join('')}</tbody>
    <tfoot><tr><th>Total</th><th class="text-end num">${n(sum(bt,'transactions'))}</th><th class="text-end num">${n(sum(bt,'notesDispensed'))}</th><th class="text-end num">${n(sum(bt,'notesAccepted'))}</th><th></th></tr></tfoot></table></div></div>`;
  $id('appCounters').innerHTML=html;
 }

 // Device picker: one checkbox per XFS device class, with the number of devices this ATM reports for it. Remembered in this browser.
 function renderPick(c){
  const have={};(c?c.xfs:[]).forEach(x=>{(have[x.serviceClass]=have[x.serviceClass]||new Set()).add(x.device.toLowerCase());});
  $id('xfsPick').innerHTML='<span class="small text-muted me-1">Show devices:</span>'+CLASSES.map(([k,l])=>{const cnt=have[k]?have[k].size:0;
   return `<label class="form-check form-check-inline small mb-0${cnt?'':' text-muted'}" title="${cnt?cnt+' device(s) on this ATM':'Not reported by this ATM'}"><input class="form-check-input" type="checkbox" data-xcls="${k}"${picked.includes(k)?' checked':''}> ${l} <span class="xfs-class">${k}</span>${cnt?'':' <span class="small">(none)</span>'}</label>`;}).join('')
   +'<button class="btn btn-link btn-sm p-0 ms-1" data-xall>All</button> · <button class="btn btn-link btn-sm p-0" data-xdef>Default</button>';
  $id('xfsPick').querySelectorAll('[data-xcls]').forEach(b=>b.onchange=()=>{picked=[...$id('xfsPick').querySelectorAll('[data-xcls]:checked')].map(x=>x.dataset.xcls);save();if(last)renderXfs(last);});
  $id('xfsPick').querySelector('[data-xall]').onclick=()=>{picked=ORDER.slice();save();if(last)renderXfs(last);};
  $id('xfsPick').querySelector('[data-xdef]').onclick=()=>{picked=DEFAULT.slice();save();if(last)renderXfs(last);};
 }

 function renderXfs(c){
  $id('xfsMeta').innerHTML=meta(c,'xfs');$id('xfsMsg').innerHTML=warn(c,'xfs');renderPick(c);
  const shown=c.xfs.filter(x=>picked.includes(x.serviceClass)||!ORDER.includes(x.serviceClass)&&picked.length===ORDER.length);
  const flagged=shown.filter(x=>x.status!=='OK');const fb=$id('xfsFlagged');fb.textContent=flagged.length;fb.classList.toggle('d-none',!flagged.length);
  if(!c.xfs.length){$id('xfsKpis').innerHTML='';$id('xfsCounters').innerHTML='<div class="col-12"><div class="alert alert-secondary small mb-0">No XFS counters for this ATM (see the reason above).</div></div>';return;}
  if(!shown.length){$id('xfsKpis').innerHTML='';$id('xfsCounters').innerHTML=`<div class="col-12"><div class="alert alert-secondary small mb-0">${picked.length?'This ATM reports no counters for the ticked devices.':'No devices ticked.'} Tick a device above to show its counters.</div></div>`;return;}
  // One card per XFS device (service class + device name), in the style of the Hardware Status cards.
  const devs=[];const by={};shown.forEach(x=>{const k=x.serviceClass+'|'+x.device.toLowerCase();if(!by[k]){by[k]={device:x.device,cls:x.serviceClass,logical:x.logicalName,rows:[]};devs.push(by[k]);}by[k].logical=by[k].logical||x.logicalName;by[k].rows.push(x);});
  devs.forEach(d=>d.status=d.rows.some(x=>x.status!=='OK')?'Warning':'OK');
  devs.sort((a,b)=>(a.status==='OK')-(b.status==='OK')||rank(ORDER,a.cls)-rank(ORDER,b.cls));
  const warnDevs=devs.filter(d=>d.status!=='OK').length;
  $id('xfsKpis').innerHTML=kpi('Devices',devs.length,'info',[...new Set(devs.map(d=>d.cls))].join(' · '))+kpi('Counters',shown.length,'info',`${shown.filter(x=>x.threshold!=null).length} with a threshold`)
   +kpi('Warning',flagged.length,flagged.length?'warn':'ok',flagged.length?`on ${warnDevs} device(s)`:'all within thresholds')+kpi('OK',shown.length-flagged.length,'ok');
  $id('xfsCounters').innerHTML=devs.map(d=>`<div class="col-md-6 col-xl-4"><div class="cardx h-100 xfs-card ${d.status==='OK'?'ok':'warn'}">
    <div class="d-flex align-items-center mb-1"><h6 class="m-0">${K.esc(d.device)} <span class="xfs-class">${K.esc(d.cls)}</span></h6><span class="ms-auto">${badge(d.status)}</span></div>
    <div class="small text-muted mb-2">${d.logical?K.esc(d.logical)+' · ':''}${c.collectedUtc?`Last update ${K.local(c.collectedUtc)}`:'&nbsp;'}</div>
    <table class="table table-sm small mb-0 counter-table"><thead><tr><th>Counter</th><th class="text-end">Value</th><th class="text-end">Threshold</th><th>Status</th></tr></thead>
    <tbody>${d.rows.map(x=>`<tr${x.status!=='OK'?' class="table-warning-soft"':''}><td>${K.esc(x.counter)}</td><td class="text-end num">${n(x.value)}</td><td class="text-end num text-muted">${x.threshold==null?'–':n(x.threshold)}</td><td>${badge(x.status)}</td></tr>`).join('')}</tbody></table>
   </div></div>`).join('');
 }

 async function load(){
  try{const c=await K.api('/data/atm/'+encodeURIComponent(id)+'/counters');last=c;renderApp(c);renderXfs(c);}
  catch(e){const m=`<div class="alert alert-danger small">Counters unavailable: ${K.esc(e.message)}</div>`;$id('appMsg').innerHTML=m;$id('xfsMsg').innerHTML=m;$id('appCounters').innerHTML='';$id('xfsCounters').innerHTML='';$id('xfsKpis').innerHTML='';}
 }
 // The export carries the ticked devices, so the file holds the same XFS counters as the tab.
 document.querySelectorAll('[data-cx]').forEach(b=>b.onclick=()=>{location.href='/Atm/CountersExport/'+encodeURIComponent(id)+'?format='+b.dataset.cx+(picked.length<ORDER.length?'&devices='+encodeURIComponent(picked.join(',')||'none'):'');});
 document.querySelectorAll('[data-crefresh]').forEach(b=>b.onclick=()=>load());

 // Keep the open tab in the URL hash so a reload or a shared link returns to it.
 const want=(location.hash||'').slice(1),btn=/^[a-z-]+$/.test(want)?document.querySelector(`#atmTabs [data-tab="${want}"]`):null;
 if(btn)bootstrap.Tab.getOrCreateInstance(btn).show();
 document.querySelectorAll('#atmTabs [data-bs-toggle="tab"]').forEach(b=>b.addEventListener('shown.bs.tab',()=>history.replaceState(null,'','#'+b.dataset.tab)));

 renderPick(null);load();setInterval(()=>{if(!document.hidden)load();},60000);
})();
