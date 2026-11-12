/* KTC Portal shared helpers */
window.KTC = (function () {
  const csrf = document.querySelector('meta[name=csrf]')?.content;
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const css = n => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
  async function api(url, opts = {}) {
    opts.headers = Object.assign({ 'RequestVerificationToken': csrf, 'X-Requested-With': 'fetch' }, opts.headers || {});
    if (opts.json !== undefined) { opts.body = JSON.stringify(opts.json); opts.headers['Content-Type'] = 'application/json'; opts.method = opts.method || 'POST'; }
    const r = await fetch(url, opts);
    if (r.status === 401) { location.href = '/Account/Login?returnUrl=' + encodeURIComponent(location.pathname); throw new Error('unauthenticated'); }
    const ct = r.headers.get('content-type') || ''; const body = ct.includes('json') ? await r.json() : await r.text();
    if (!r.ok) throw new Error((body && body.error) || (typeof body === 'string' && body) || ('HTTP ' + r.status));
    return body;
  }
  const qs = o => new URLSearchParams(Object.entries(o).filter(([, v]) => v !== '' && v != null)).toString();
  function toast(msg, kind = 'info') {
    const el = document.createElement('div'); el.className = `toast align-items-center text-bg-${kind === 'error' ? 'danger' : kind === 'warn' ? 'warning' : kind === 'ok' ? 'success' : 'secondary'} border-0`;
    el.innerHTML = `<div class="d-flex"><div class="toast-body">${esc(msg)}</div><button class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button></div>`;
    document.getElementById('toasts').appendChild(el); const t = new bootstrap.Toast(el, { delay: 6000 }); t.show(); el.addEventListener('hidden.bs.toast', () => el.remove());
  }
  const local = u => u ? new Date(u.endsWith('Z') || /[+-]\d\d:\d\d$/.test(u) ? u : u + 'Z').toLocaleString([], { dateStyle: 'short', timeStyle: 'short' }) : '';
  const ago = u => { if (!u) return 'never'; const m = Math.round((Date.now() - new Date(u.endsWith('Z') ? u : u + 'Z')) / 60000); return m < 1 ? 'just now' : m < 60 ? m + ' min ago' : m < 2880 ? Math.round(m / 60) + ' h ago' : Math.round(m / 1440) + ' d ago'; };
  const health = h => `<span class="sbadge ${String(h).toLowerCase()}">${h === 'Healthy' ? 'Healthy' : h === 'Warning' ? 'Warning' : h === 'Critical' ? 'Critical' : esc(h)}</span>`;
  const sev = s => `<span class="sev ${s}">${s}</span>`;
  const slaBadge = s => ({ Breached: '<span class="sbadge crit">Breached</span>', NearBreach: '<span class="sbadge warn">Near breach</span>', Achieved: '<span class="sbadge ok">Achieved</span>', OnTrack: '<span class="sbadge unknown">On track</span>' }[s] || esc(s));
  const dtDefaults = { pageLength: 25, deferRender: true, autoWidth: false, language: { search: '', searchPlaceholder: 'Filter…', emptyTable: 'No data' } };
  function table(sel, opts) { if ($.fn.DataTable.isDataTable(sel)) $(sel).DataTable().destroy(); return $(sel).DataTable(Object.assign({}, dtDefaults, opts)); }
  function reload(dt, rows) { dt.clear().rows.add(rows).draw(false); }
  function details(title, html) { document.getElementById('detailsTitle').textContent = title; document.getElementById('detailsBody').innerHTML = html; document.querySelector('.noc-shell').classList.add('details-open'); }
  document.getElementById('detailsClose').onclick = () => document.querySelector('.noc-shell').classList.remove('details-open');
  // charts (palette from CSS tokens; identity never by colour alone: legends + labels are present)
  const charts = {};
  function chart(id, cfg) {
    const c = document.getElementById(id); if (!c) return;
    const ink2 = css('--ink2'), grid = css('--grid');
    Chart.defaults.color = ink2; Chart.defaults.font.size = 11; Chart.defaults.borderColor = grid;
    cfg.options = Object.assign({ responsive: true, maintainAspectRatio: false, animation: false }, cfg.options || {});
    cfg.options.plugins = Object.assign({ legend: { display: false } }, cfg.options.plugins || {});
    if (charts[id]) { charts[id].destroy(); }
    charts[id] = new Chart(c, cfg); return charts[id];
  }
  const pal = () => [css('--s1'), css('--s2'), css('--s3'), css('--s4'), css('--s5'), css('--s6'), css('--s7'), css('--s8')];
  const status = () => ({ Healthy: css('--ok'), Warning: css('--warn'), Critical: css('--crit') });
  function legend(id, labels, colors, values) { const el = document.getElementById(id); if (el) el.innerHTML = labels.map((l, i) => `<span><i style="background:${colors[i]}"></i>${esc(l)}${values ? ' <b>' + values[i] + '</b>' : ''}</span>`).join(''); }
  const hooks = { dashboard: [], alert: [], apiHealth: [] };
  let lastTheme = null;
  // theme
  const root = document.documentElement;
  document.getElementById('themeToggle').onclick = () => {
    const t = root.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark'; root.setAttribute('data-bs-theme', t);
    try { localStorage.setItem('ktc-theme', t); } catch (e) { }
    fetch('/data/theme', { method: 'POST', headers: { 'RequestVerificationToken': csrf, 'Content-Type': 'application/x-www-form-urlencoded' }, body: 'theme=' + t });
    document.dispatchEvent(new Event('ktc-theme'));
  };
  document.getElementById('navToggle')?.addEventListener('click', () => document.getElementById('nav').classList.toggle('open'));
  // header summary pills
  async function pills(s) {
    try { s = s || await api('/data/summary'); document.getElementById('pOnline').textContent = `${s.onlineAtms}/${s.totalAtms}`; document.getElementById('pCrit').textContent = s.criticalAtms; document.getElementById('pSla').textContent = s.slaPct;
      document.getElementById('lastUpdated').textContent = 'Data as of ' + local(s.generatedUtc); const b = document.getElementById('alertBadge'); if (s.highPriorityAlerts > 0) { b.textContent = s.highPriorityAlerts; b.classList.remove('d-none'); } else b.classList.add('d-none'); } catch (e) { }
  }
  // session
  let expires = null, refreshing = false;
  async function sess() { try { const s = await api('/data/session'); expires = s.expiresUtc ? new Date(s.expiresUtc.endsWith('Z') ? s.expiresUtc : s.expiresUtc + 'Z') : null; } catch (e) { } }
  setInterval(async () => {
    const cd = document.getElementById('sessCountdown'), st = document.getElementById('sessStatus'); if (!expires) return;
    const left = Math.max(0, Math.round((expires - Date.now()) / 1000));
    cd.textContent = String(Math.floor(left / 60)).padStart(2, '0') + ':' + String(left % 60).padStart(2, '0');
    st.textContent = left > 0 ? 'Session active' : 'Refreshing…';
    if (left < 120 && !refreshing) { refreshing = true; try { const s = await api('/data/session/refresh', { method: 'POST' }); expires = new Date(s.expiresUtc.endsWith('Z') ? s.expiresUtc : s.expiresUtc + 'Z'); } catch (e) { st.textContent = 'Session expired'; } refreshing = false; }
  }, 1000);
  // SignalR
  let conn;
  function realtime() {
    if (!window.signalR) return;
    conn = new signalR.HubConnectionBuilder().withUrl('/hubs/portal').withAutomaticReconnect().build();
    const set = on => { const e = document.getElementById('rtState'); e.className = 'conn ' + (on ? 'on' : 'off'); e.querySelector('span').textContent = on ? 'live' : 'offline'; };
    conn.on('dashboardUpdated', s => { pills(s); hooks.dashboard.forEach(f => f(s)); });
    conn.on('alertRaised', a => { toast(`[${a.severity}] ${a.title}`, a.severity === 'P1' ? 'error' : 'warn'); hooks.alert.forEach(f => f(a)); pills(); });
    conn.on('apiHealth', r => hooks.apiHealth.forEach(f => f(r)));
    conn.onreconnecting(() => set(false)); conn.onreconnected(() => set(true)); conn.onclose(() => set(false));
    conn.start().then(() => set(true)).catch(() => set(false));
  }
  // global search
  const gs = document.getElementById('gsearch'), box = document.getElementById('gsearchResults'); let gt;
  gs.addEventListener('input', () => { clearTimeout(gt); const q = gs.value.trim(); if (q.length < 2) return box.classList.add('d-none');
    gt = setTimeout(async () => { try { const r = await api('/data/search?q=' + encodeURIComponent(q)); let h = ''; let last = '';
      r.forEach(x => { if (x.type !== last) { h += `<div class="gs-t">${esc(x.type)}</div>`; last = x.type; } h += `<a href="${esc(x.url)}"><b>${esc(x.title)}</b><div class="small text-muted">${esc(x.subtitle)}</div></a>`; });
      box.innerHTML = h || '<div class="p-2 text-muted">No matches</div>'; box.classList.remove('d-none'); } catch (e) { box.innerHTML = `<div class="p-2 text-danger">${esc(e.message)}</div>`; box.classList.remove('d-none'); } }, 250); });
  gs.addEventListener('keydown', e => { if (e.key === 'Enter' && gs.value.trim()) location.href = '/Home/Search?q=' + encodeURIComponent(gs.value.trim()); if (e.key === 'Escape') box.classList.add('d-none'); });
  document.addEventListener('click', e => { if (!box.contains(e.target) && e.target !== gs) box.classList.add('d-none'); });
  document.addEventListener('keydown', e => { if (e.key === '/' && !/INPUT|TEXTAREA|SELECT/.test(document.activeElement.tagName)) { e.preventDefault(); gs.focus(); } });
  sess(); pills(); realtime(); setInterval(() => pills(), 60000);
  return { api, qs, esc, css, toast, local, ago, health, sev, slaBadge, table, reload, details, chart, pal, status, legend, hooks, csrf, pills };
})();
