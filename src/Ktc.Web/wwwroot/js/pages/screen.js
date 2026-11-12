(function(){const K=KTC;
 function path(o,p){const parts=p.replace(/\[\]/g,'.[]').split('.').filter(Boolean);let cur=[o];for(const part of parts){const nx=[];for(const c of cur){if(part==='[]'){if(Array.isArray(c))nx.push(...c);}else if(c&&typeof c==='object'){const k=Object.keys(c).find(k=>k.toLowerCase()===part.toLowerCase());if(k!==undefined)nx.push(c[k]);}}cur=nx;}return cur;}
 const agg=(v,a)=>{const n=v.map(Number).filter(x=>!isNaN(x));switch((a||'count').toLowerCase()){case'sum':return n.reduce((x,y)=>x+y,0);case'avg':return n.length?n.reduce((x,y)=>x+y,0)/n.length:0;case'last':return v[v.length-1];default:return v.length;}};
 function items(j){if(Array.isArray(j))return j;if(j&&typeof j==='object'){for(const k of['items','data','results','value'])if(Array.isArray(j[k]))return j[k];return[j];}return[];}
 async function fill(card){const body=card.querySelector('.w-body'),mid=card.dataset.mid,w=card.dataset.widget,fields=JSON.parse(card.dataset.fields||'[]');
  try{const r=await K.api('/data/api/screen/'+encodeURIComponent(window.SCREEN)+'?mappingId='+mid);if(r.success===false){body.innerHTML=`<span class="text-muted">${K.esc(r.error||'No data')}</span>`;return;}
   const j=JSON.parse(r.body||'null');
   if(fields.length){body.innerHTML='<div class="row g-2">'+fields.map(f=>{const v=agg(path(j,f.path),f.agg);const val=typeof v==='number'?(Math.round(v*100)/100).toLocaleString():K.esc(v);return `<div class="col"><div class="kpi info"><div class="l">${K.esc(f.label)}</div><div class="v" style="font-size:1.4rem">${val}</div><div class="d">${f.widget}</div></div></div>`;}).join('')+'</div>';return;}
   const rows=items(j).slice(0,500);if(!rows.length){body.innerHTML='<span class="text-muted">Empty result</span>';return;}
   if(w==='KpiWidget'||w==='Counter'||w==='StatusCard'){body.innerHTML=`<div class="kpi info"><div class="l">Items</div><div class="v">${items(j).length}</div></div>`;return;}
   const cols=[...new Set(rows.filter(x=>x&&typeof x==='object').flatMap(x=>Object.keys(x)))].slice(0,12);
   body.innerHTML=`<div class="table-responsive" style="max-height:420px"><table class="table table-sm"><thead><tr>${cols.map(c=>`<th>${K.esc(c)}</th>`).join('')}</tr></thead><tbody>${rows.map(x=>'<tr>'+cols.map(c=>`<td>${K.esc(typeof x[c]==='object'?JSON.stringify(x[c]):x[c])}</td>`).join('')+'</tr>').join('')}</tbody></table></div><div class="small text-muted">${r.fromCache?'cached · ':''}${r.elapsedMs} ms</div>`;
  }catch(e){body.innerHTML=`<span class="text-danger">${K.esc(e.message)}</span>`;}}
 document.querySelectorAll('[data-mid]').forEach(c=>{fill(c);const s=+c.dataset.refresh;if(s>=15)setInterval(()=>fill(c),s*1000);});})();
