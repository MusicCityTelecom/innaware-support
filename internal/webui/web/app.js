'use strict';
const $ = (id) => document.getElementById(id);
const state = {
  me: null, sessions: [], history: [], admins: [], audit: [],
  ws: null, session: null, activeTab: 'sessions', lastMove: 0,
  historyOffset: 0, historyLimit: 50, historyTotal: 0
};

async function api(path, options = {}) {
  const init = { credentials: 'same-origin', ...options };
  if (init.body && typeof init.body !== 'string') {
    init.headers = { ...(init.headers || {}), 'Content-Type': 'application/json' };
    init.body = JSON.stringify(init.body);
  }
  const response = await fetch(path, init);
  const contentType = response.headers.get('content-type') || '';
  const data = contentType.includes('application/json') ? await response.json().catch(() => ({})) : {};
  if (!response.ok) {
    if (response.status === 401 && state.me) {
      state.me = null;
      setAuthUI(false);
    }
    throw new Error(data.error || `Request failed (${response.status})`);
  }
  return data;
}

function show(id, visible) {
  const el = $(id);
  if (el) el.classList.toggle('hidden', !visible);
}
function isAdmin() { return state.me?.role === 'admin'; }
function setAuthUI(loggedIn) {
  show('loginView', !loggedIn);
  show('consoleView', loggedIn);
  show('logoutButton', loggedIn);
  show('accountButton', loggedIn);
  document.querySelectorAll('.admin-only').forEach(el => el.classList.toggle('hidden', !loggedIn || !isAdmin()));
  $('whoami').textContent = loggedIn ? `${state.me.display_name || state.me.username} Â· ${state.me.role}` : '';
  if (!loggedIn) {
    switchTab('sessions', false);
  }
}

async function bootstrap() {
  try {
    state.me = await api('/api/me');
    setAuthUI(true);
    await Promise.all([loadMetrics(), loadSessions(), loadTechnicianFilter()]);
  } catch {
    state.me = null;
    setAuthUI(false);
  }
  try {
    const status = await api('/api/download-status');
    if (status.available) {
      $('downloadText').textContent = 'Download the customer support app. Your technician will provide the temporary session code.';
      $('downloadButton').classList.remove('disabled');
      $('downloadButton').setAttribute('aria-disabled', 'false');
    } else {
      $('downloadText').textContent = 'The Windows support app is being prepared on this server. Your technician can provide the current build directly.';
    }
  } catch {
    $('downloadText').textContent = 'Windows support app status is temporarily unavailable.';
  }
}

$('loginForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  $('loginError').textContent = '';
  try {
    state.me = await api('/api/login', {
      method: 'POST',
      body: { username: $('username').value, password: $('password').value }
    });
    $('password').value = '';
    setAuthUI(true);
    await Promise.all([loadMetrics(), loadSessions(), loadTechnicianFilter()]);
  } catch (e) {
    $('loginError').textContent = e.message;
  }
});
$('logoutButton').addEventListener('click', async () => {
  try { await api('/api/logout', { method: 'POST' }); } catch {}
  location.reload();
});
$('accountButton').addEventListener('click', () => {
  $('accountError').textContent = '';
  $('accountForm').reset();
  $('accountDialog').showModal();
});
$('closeAccountDialog').addEventListener('click', () => $('accountDialog').close());
$('accountForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  $('accountError').textContent = '';
  if ($('newPassword').value !== $('confirmPassword').value) {
    $('accountError').textContent = 'New passwords do not match.';
    return;
  }
  try {
    await api('/api/account/password', {
      method: 'POST',
      body: { current_password: $('currentPassword').value, new_password: $('newPassword').value }
    });
    alert('Password changed. Please sign in again.');
    location.reload();
  } catch (e) {
    $('accountError').textContent = e.message;
  }
});

document.querySelectorAll('.tab').forEach(btn => btn.addEventListener('click', () => switchTab(btn.dataset.tab)));
async function switchTab(name, load = true) {
  if ((name === 'team' || name === 'audit') && !isAdmin()) name = 'sessions';
  state.activeTab = name;
  document.querySelectorAll('.tab').forEach(btn => btn.classList.toggle('active', btn.dataset.tab === name));
  for (const tab of ['sessions','history','team','audit']) show(`${tab}Tab`, tab === name);
  if (!load || !state.me) return;
  if (name === 'sessions') await Promise.all([loadMetrics(), loadSessions()]);
  if (name === 'history') await loadHistory(true);
  if (name === 'team') await loadAdmins();
  if (name === 'audit') await loadAudit();
}

async function loadMetrics() {
  if (!state.me) return;
  try {
    const m = await api('/api/dashboard/metrics');
    $('waitingMetric').textContent = m.waiting ?? 0;
    $('connectedMetric').textContent = m.connected ?? 0;
    $('todayMetric').textContent = m.created_today ?? 0;
    $('weekMetric').textContent = m.ended_seven_days ?? 0;
    $('avgMetric').textContent = m.average_minutes > 0 ? `${Math.round(m.average_minutes)}m` : 'â';
  } catch (e) { console.error(e); }
}

function sessionQuery(params = {}) {
  const q = new URLSearchParams();
  Object.entries(params).forEach(([k,v]) => {
    if (v !== undefined && v !== null && String(v) !== '') q.set(k, String(v));
  });
  return q.toString();
}

async function loadSessions() {
  if (!state.me) return;
  try {
    const data = await api('/api/sessions?' + sessionQuery({status:'active',limit:100}));
    state.sessions = data.sessions || [];
    renderSessions($('sessionsList'), state.sessions, true);
  } catch (e) { console.error(e); }
}
$('refreshSessionsButton').addEventListener('click', async () => Promise.all([loadMetrics(), loadSessions()]));

function renderSessions(container, sessions, activeOnly = false) {
  container.textContent = '';
  if (!sessions.length) {
    container.innerHTML = `<div class="session-row"><div class="session-label"><strong>${activeOnly ? 'No active sessions' : 'No sessions found'}</strong><span>${activeOnly ? 'Create a support session when a customer needs help.' : 'Adjust the history filters and try again.'}</span></div></div>`;
    return;
  }
  for (const s of sessions) {
    const row = document.createElement('div');
    row.className = 'session-row';
    row.tabIndex = 0;
    const when = new Date(s.created_at).toLocaleString();
    const tech = s.technician_name || 'Unknown technician';
    row.innerHTML = `<div class="session-label"><strong>${escapeHTML(s.customer_label || 'Unlabeled support session')}</strong><span>${escapeHTML(s.machine_name || 'Waiting for customer')}</span></div><span class="status ${escapeHTML(s.status)}">${escapeHTML(s.status)}</span><span class="session-meta">${escapeHTML(tech)}</span><span class="session-meta">${escapeHTML(when)}</span><span class="session-meta">â¢â¢${escapeHTML(s.code_hint)}</span>`;
    row.addEventListener('click', () => openViewer(s.id));
    row.addEventListener('keydown', e => { if (e.key === 'Enter') openViewer(s.id); });
    container.appendChild(row);
  }
}

$('historyFilters').addEventListener('submit', async e => {
  e.preventDefault();
  state.historyOffset = 0;
  await loadHistory();
});
$('historyPrev').addEventListener('click', async () => {
  state.historyOffset = Math.max(0, state.historyOffset - state.historyLimit);
  await loadHistory();
});
$('historyNext').addEventListener('click', async () => {
  if (state.historyOffset + state.historyLimit < state.historyTotal) {
    state.historyOffset += state.historyLimit;
    await loadHistory();
  }
});
function historyParams(includePaging = true) {
  const p = {
    q: $('historySearch').value.trim(),
    status: $('historyStatus').value,
    technician_id: $('historyTechnician').value,
    from: $('historyFrom').value,
    to: $('historyTo').value
  };
  if (includePaging) {
    p.limit = state.historyLimit;
    p.offset = state.historyOffset;
  }
  return p;
}
async function loadHistory(reset = false) {
  if (reset) state.historyOffset = 0;
  try {
    const data = await api('/api/sessions?' + sessionQuery(historyParams(true)));
    state.history = data.sessions || [];
    state.historyTotal = data.total || 0;
    renderSessions($('historyList'), state.history, false);
    $('historyCount').textContent = `${state.historyTotal} session${state.historyTotal === 1 ? '' : 's'}`;
    const first = state.historyTotal ? state.historyOffset + 1 : 0;
    const last = Math.min(state.historyTotal, state.historyOffset + state.historyLimit);
    $('historyRange').textContent = state.historyTotal ? `${first}â${last}` : '';
    $('historyPage').textContent = `Page ${Math.floor(state.historyOffset / state.historyLimit) + 1}`;
    $('historyPrev').disabled = state.historyOffset === 0;
    $('historyNext').disabled = state.historyOffset + state.historyLimit >= state.historyTotal;
  } catch (e) {
    $('historyList').innerHTML = `<div class="session-row"><div class="session-label"><strong>Could not load history</strong><span>${escapeHTML(e.message)}</span></div></div>`;
  }
}
$('exportHistoryButton').addEventListener('click', () => {
  location.href = '/api/sessions/export?' + sessionQuery(historyParams(false));
});

async function loadTechnicianFilter() {
  try {
    const data = await api('/api/technicians');
    const select = $('historyTechnician');
    const selected = select.value;
    select.innerHTML = '<option value="">All technicians</option>';
    for (const a of data.technicians || []) {
      const option = document.createElement('option');
      option.value = a.id;
      option.textContent = (a.display_name || a.username) + (a.active ? '' : ' (disabled)');
      select.appendChild(option);
    }
    select.value = selected;
  } catch {}
}

$('newSessionButton').addEventListener('click', () => $('sessionDialog').showModal());
$('closeSessionDialog').addEventListener('click', () => $('sessionDialog').close());
$('closeCodeDialog').addEventListener('click', () => $('codeDialog').close());
$('sessionForm').addEventListener('submit', async event => {
  event.preventDefault();
  $('sessionError').textContent = '';
  try {
    const data = await api('/api/sessions', {
      method:'POST',
      body:{
        customer_label:$('customerLabel').value,
        requested_control:$('requestControl').checked,
        requested_elevation:$('requestElevation').checked
      }
    });
    $('sessionDialog').close();
    $('sessionCode').textContent = data.code.replace(/(\d{4})(\d{4})/, '$1 $2');
    $('codeDialog').dataset.rawCode = data.code;
    $('codeDialog').showModal();
    $('customerLabel').value='';
    $('requestControl').checked=true;
    $('requestElevation').checked=false;
    await Promise.all([loadMetrics(), loadSessions()]);
  } catch (e) { $('sessionError').textContent=e.message; }
});
$('copyCodeButton').addEventListener('click', async () => {
  const code = $('codeDialog').dataset.rawCode || $('sessionCode').textContent.replace(/\D/g,'');
  try {
    await navigator.clipboard.writeText(code);
    $('copyCodeButton').textContent='Copied';
    setTimeout(() => $('copyCodeButton').textContent='Copy code',1200);
  } catch {}
});

async function openViewer(id) {
  try {
    const data = await api(`/api/sessions/${encodeURIComponent(id)}`);
    state.session = data.session;
    state.session.events = data.events || [];
    state.session.notes = data.notes || [];
    show('consoleView', false);
    show('viewerView', true);
    show('publicView', false);
    show('loginView', false);
    $('viewerLabel').textContent = state.session.customer_label || 'Remote support session';
    $('viewerMachine').textContent = state.session.machine_name || 'No customer connected';
    setViewerStatus(state.session.status);
    resetViewerCanvas();
    renderSessionDetail();
    const active = ['waiting','approved','connected'].includes(state.session.status);
    $('endSessionButton').classList.toggle('hidden', !active);
    if (active) connectViewerWS(id);
    else {
      $('screenPlaceholder').querySelector('strong').textContent = 'Session complete';
      $('screenPlaceholder').querySelector('span').textContent = 'Remote control is no longer available. Review the notes and timeline for this support session.';
    }
  } catch (e) {
    alert(e.message);
  }
}
function resetViewerCanvas() {
  const canvas = $('remoteCanvas');
  canvas.style.display = 'none';
  const ph = $('screenPlaceholder');
  ph.style.display = 'flex';
  ph.querySelector('strong').textContent = 'Waiting for customer';
  ph.querySelector('span').textContent = 'The remote screen will appear after the customer enters the session code and approves access.';
}
function closeViewer(){
  if(state.ws){state.ws.close();state.ws=null;}
  state.session=null;
  show('viewerView',false);
  show('consoleView',true);
  show('publicView',true);
  switchTab(state.activeTab || 'sessions');
}
$('backButton').addEventListener('click',closeViewer);
$('endSessionButton').addEventListener('click',async()=>{
  if(!state.session)return;
  if(!confirm('End this support session? The customer connection will be revoked.'))return;
  try{await api(`/api/sessions/${encodeURIComponent(state.session.id)}/end`,{method:'POST'});}
  finally{closeViewer();}
});
function setViewerStatus(status){
  $('viewerStatus').textContent=status;
  $('viewerStatus').className=`status-dot ${status==='connected'?'connected':''}`;
}
function renderSessionDetail() {
  const s = state.session;
  if (!s) return;
  const duration = sessionDuration(s);
  $('sessionFacts').innerHTML = [
    ['Status', `<span class="status ${escapeHTML(s.status)}">${escapeHTML(s.status)}</span>`],
    ['Technician', escapeHTML(s.technician_name || 'Unknown')],
    ['Computer', escapeHTML(s.machine_name || 'Not connected')],
    ['Created', escapeHTML(formatDate(s.created_at))],
    ['Connected', escapeHTML(formatDate(s.connected_at))],
    ['Ended', escapeHTML(formatDate(s.ended_at))],
    ['Duration', escapeHTML(duration)],
    ['Control', s.requested_control ? 'Requested' : 'View only'],
    ['Elevation', s.requested_elevation ? 'May be needed' : 'No']
  ].map(([k,v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('');
  renderNotes();
  renderTimeline();
}
function renderNotes() {
  const notes = state.session?.notes || [];
  $('sessionNotes').innerHTML = notes.length ? notes.map(n => `<div class="note"><div class="note-meta"><strong>${escapeHTML(n.admin_username)}</strong><span>${escapeHTML(formatDate(n.created_at))}</span></div><div>${escapeHTML(n.body).replace(/\n/g,'<br>')}</div></div>`).join('') : '<div class="muted small">No technician notes yet.</div>';
}
function renderTimeline() {
  const events = state.session?.events || [];
  $('sessionTimeline').innerHTML = events.length ? events.map(e => `<div class="timeline-item"><div class="timeline-meta"><span>${escapeHTML(formatDate(e.created_at))}</span><span>${escapeHTMK(e.actor)}</span></div><strong>${escapeHTML(prettyEvent(e.event))}</strong>${e.details ? `<span>${escapeHTML(e.details)}</span>` : ''}</div>`).join('') : '<div class="muted small">No timeline events recorded.</div>';
}
$('noteForm').addEventListener('submit', async e => {
  e.preventDefault();
  if (!state.session) return;
  const body = $('noteBody').value.trim();
  if (!body) return;
  try {
    const data = await api(`/api/sessions/${encodeURIComponent(state.session.id)}/notes`, {method:'POST',body:{body}});
    state.session.notes = [...(state.session.notes || []), data.note];
    $('noteBody').value='';
    renderNotes();
  } catch(e) { alert(e.message); }
});

function connectViewerWS(id){
  if(state.ws)state.ws.close();
  const proto=location.protocol==='https:'?'wss':'ws';
  const ws=new WebSocket(`${proto}://${location.host}/ws/tech?session=${encodeURIComponent(id)}`);
  ws.binaryType='arraybuffer';
  state.ws=ws;
  ws.onopen=()=>setViewerStatus(state.session?.status==='connected'?'connected':'waiting');
  ws.onclose=()=>{if(state.ws===ws)setViewerStatus('disconnected');};
  ws.onerror=()=>setViewerStatus('connection error');
  ws.onmessage=async(event)=>{
    if(typeof event.data==='string'){
      try{
        const msg=JSON.parse(event.data);
        if(msg.type==='agent_status'){
          setViewerStatus(msg.status);
          if(msg.status==='connected') $('screenPlaceholder').querySelector('strong').textContent='Customer connected';
        }
        if(msg.type==='hello') $('viewerMachine').textContent=msg.machine_name||state.session.machine_name||'Customer connected';
      }catch{}
      return;
    }
    const blob=new Blob([event.data],{type:'image/jpeg'});
    const bmp=await createImageBitmap(blob);
    const canvas=$('remoteCanvas');
    canvas.width=bmp.width;canvas.height=bmp.height;
    canvas.getContext('2d').drawImage(bmp,0,0);bmp.close();
    canvas.style.display='block';$('screenPlaceholder').style.display='none';setViewerStatus('connected');
  };
}

function sendInput(input){
  if(!state.ws||state.ws.readyState!==WebSocket.OPEN||!state.session?.requested_control)return;
  state.ws.send(JSON.stringify({type:'input',input}));
}
const canvas=$('remoteCanvas');
function point(e){const r=canvas.getBoundingClientRect();return{x:Math.min(1,Math.max(0,(e.clientX-r.left)/r.width)),y:Math.min(1,Math.max(0,(e.clientY-r.top)/r.height))};}
canvas.addEventListener('mousemove',(e)=>{const n=performance.now();if(n-state.lastMove<33)return;state.lastMove=n;const p=point(e);sendInput({kind:'mouse_move',...p});});
canvas.addEventListener('mousedown',(e)=>{canvas.focus();const p=point(e);sendInput({kind:'mouse_button',button:e.button,down:true,...p});e.preventDefault();});
canvas.addEventListener('mouseup',(e)=>{const p=point(e);sendInput({kind:'mouse_button',button:e.button,down:false,...p});e.preventDefault();});
canvas.addEventListener('contextmenu',(e)=>e.preventDefault());
canvas.addEventListener('wheel',(e)=>{sendInput({kind:'mouse_wheel',delta:Math.sign(e.deltaY)*-120});e.preventDefault();},{passive:false});
canvas.addEventListener('keydown',(e)=>{if(['F5','F11','F12'].includes(e.key))return;sendInput({kind:'key',code:e.code,down:true});e.preventDefault();});
canvas.addEventListener('keyup',(e)=>{sendInput({kind:'key',code:e.code,down:false});e.preventDefault();});

$('addAdminButton').addEventListener('click', () => openAdminDialog());
$('closeAdminDialog').addEventListener('click', () => $('adminDialog').close());
function openAdminDialog(admin=null) {
  $('adminForm').reset();
  $('adminError').textContent='';
  $('adminId').value=admin?.id || '';
  $('adminDialogTitle').textContent=admin?'Edit support user':'Add support user';
  $('adminUsernameLabel').classList.toggle('hidden',!!admin);
  $('adminPasswordLabel').classList.toggle('hidden',!!admin);
  $('adminActiveLabel').classList.toggle('hidden',!admin);
  if(admin){
    $('adminUsername').value=admin.username;
    $('adminDisplayName').value=admin.display_name;
    $('adminRole').value=admin.role;
    $('adminActive').checked=admin.active;
  } else {
    $('adminRole').value='technician';
    $('adminActive').checked=true;
  }
  $('adminDialog').showModal();
}
$('adminForm').addEventListener('submit', async e=>{
  e.preventDefault();
  $('adminError').textContent='';
  const id=$('adminId').value;
  try{
    if(id){
      await api(`/api/admins/${id}`,{method:'PATCH',body:{display_name:$('adminDisplayName').value,role:$('adminRole').value,active:$('adminActive').checked}});
    }else{
      await api('/api/admins',{method:'POST',body:{username:$('adminUsername').value,display_name:$('adminDisplayName').value,role:$('adminRole').value,password:$('adminPassword').value}});
    }
    $('adminDialog').close();
    await Promise.all([loadAdmins(),loadTechnicianFilter()]);
  }catch(e){$('adminError').textContent=e.message;}
});
$('closeResetPasswordDialog').addEventListener('click',()=> $('resetPasswordDialog').close());
$('resetPasswordForm').addEventListener('submit',async e=>{
  e.preventDefault();$('resetPasswordError').textContent='';
  try{
    await api(`/api/admins/${$('resetAdminId').value}/password`,{method:'POST',body:{password:$('resetPassword').value}});
    $('resetPasswordDialog').close();$('resetPasswordForm').reset();
    alert('Password reset. Existing browser sessions for that account have been invalidated.');
  }catch(e){$('resetPasswordError').textContent=e.message;}
});
async function loadAdmins(){
  try{
    const data=await api('/api/admins');
    state.admins=data.admins||[];
    renderAdmins(data.summary||{});
  }catch(e){$('adminsList').innerHTML=`<div class="admin-row"><div class="admin-name"><strong>Could not load team</strong><span>${escapeHTML(e.message)}</span></div></div>`;}
}
function renderAdmins(summary){
  $('teamSummary').innerHTML=`<span>${summary.active||0} active</span><span>${summary.admins||0} administrators</span><span>${summary.technicians||0} technicians</span><span>${summary.inactive||0} disabled</span>`;
  const list=$('adminsList');list.textContent='';
  for(const a of state.admins){
    const row=document.createElement('div');row.className='admin-row';
    row.innerHTML=`<div class="admin-name"><strong>${escapeHTML(a.display_name)}</strong><span>@${escapeHTML(a.username)}</span></div><span class="role-badge ${escapeHTML(a.role)}">${escapeHTML(a.role)}</span><span class="active-badge ${a.active?'active':'inactive'}">${a.active?'active':'disabled'}</span><span class="last-login session-meta">${a.last_login_at?'Last login '+escapeHTML(formatDate(a.last_login_at)):'Never signed in'}</span><div class="row-actions"><button class="ghost edit-admin" type="button">Edit</button><button class="ghost reset-admin" type="button">Password</button></div>`;
    row.querySelector('.edit-admin').addEventListener('click',()=>openAdminDialog(a));
    row.querySelector('.reset-admin').addEventListener('click',()=>{
      $('resetAdminId').value=a.id;$('resetAdminLabel').textContent=`Reset password for ${a.display_name} (@${a.username})`;$('resetPassword').value='';$('resetPasswordError').textContent='';$('resetPasswordDialog').showModal();
    });
    list.appendChild(row);
  }
}
$('refreshAuditButton').addEventListener('click',loadAudit);
async function loadAudit(){
  try{
    const data=await api('/api/admin-audit?limit=250');state.audit=data.events||[];renderAudit();
  }catch(e){$('auditList').innerHTML=`<div class="audit-row"><div>${escapeHTML(e.message)}</div></div>`;}
}
function renderAudit(){
  const list=$('auditList');list.textContent='';
  if(!state.audit.length){list.innerHTML='<div class="audit-row"><div>No audit events yet.</div></div>';return;}
  for(const e of state.audit){
    const row=document.createElement('div');row.className='audit-row';
    row.innerHTML=`<span class="audit-time">${escapeHTML(formatDate(e.created_at))}</span><span class="audit-actor">${escapeHTML(e.actor_username||'system')}</span><strong>${escapeHTML(prettyEvent(e.event))}</strong><span class="audit-details">${escapeHTML(e.details||'')}</span><span class="audit-ip">${escapeHTML(e.ip_address||'')}</span>`;
    list.appendChild(row);
  }
}

function formatDate(v){if(!v)return 'â';const d=new Date(v);return Number.isNaN(d.getTime())?'â':d.toLocaleString();}
function sessionDuration(s){
  if(!s.connected_at)return 'â';
  const end=s.ended_at?new Date(s.ended_at):new Date();
  const start=new Date(s.connected_at);
  const sec=Math.max(0,Math.round((end-start)/1000));
  if(sec<60)return `${sec}s`;
  const min=Math.floor(sec/60), rem=sec%60;
  return min<60?`${min}m ${rem}s`:`${Math.floor(min/60)}h ${min%60}m`;
}
function prettyEvent(v){return String(v||'').replace(/_/g,' ').replace(/\b\w/g,c=>c.toUpperCase());}
function escapeHTML(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));}

setInterval(()=>{
  if(!state.me||state.session)return;
  if(state.activeTab==='sessions'){loadMetrics();loadSessions();}
},5000);

bootstrap();
