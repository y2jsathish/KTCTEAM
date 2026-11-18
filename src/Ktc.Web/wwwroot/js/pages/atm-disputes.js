// ATM page, Disputes tab: like the Disputes tab KTC's Windows client shows among an ATM's tabs. GET /data/disputes?atmId= reads this
// ATM's GET {clientid}/ClientDisputes?statusfilter=&Numdays= (statusfilter = the picked state, 0 = all). Double-click: Dispute Details.
(function(){const K=KTC,$id=s=>document.getElementById(s),atm=window.ATM_ID;if(!$id('tab-disputes')||!atm)return;let dt=null,loaded=false;
 async function load(){
  $id('adMsg').innerHTML='';$id('adMeta').textContent='Loading from KTC…';
  try{const st=$id('adState').value||'0';
   const r=await K.api('/data/disputes?'+K.qs({atmId:atm,state:st,days:$id('adDays').value||14}));
   $id('adState').innerHTML=K.disputeStateOptions(r.states,st);
   if(!dt)dt=K.table('#adTbl',{data:[],order:[[2,'desc']],createdRow:row=>{row.tabIndex=0;row.style.cursor='pointer';},columns:K.disputeColumns(false)});
   K.reload(dt,r.data);loaded=true;
   $id('adMeta').textContent=`${r.data.length} dispute${r.data.length===1?'':'s'} · from KTC: ${r.sources[0]||''}`;
   $id('adAll').href='/Disputes?'+K.qs({atmId:atm});
   if(r.warnings.length)$id('adMsg').innerHTML=`<div class="alert alert-warning py-1 small">${r.warnings.map(K.esc).join('<br>')}</div>`;
  }catch(e){$id('adMeta').textContent='';$id('adMsg').innerHTML=`<div class="alert alert-danger py-1 small">${K.esc(e.message)}</div>`;}
 }
 const open=tr=>{const d=dt&&dt.row(tr).data();if(d)K.disputeDetails(d.id,d.atmId||atm,()=>load(),{onAtmPage:true});};
 $('#adTbl').on('dblclick','tbody tr',function(){open(this);}).on('keydown','tbody tr',function(e){if(e.key==='Enter')open(this);});
 $id('adState').onchange=load;$id('adDays').onchange=load;$id('adRefresh').onclick=load;
 document.querySelector('[data-tab="disputes"]').addEventListener('shown.bs.tab',()=>{if(!loaded)load();});
 if($id('tab-disputes').classList.contains('active'))load();
})();
