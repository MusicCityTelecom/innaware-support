'use strict';
const $ = (id) => document.getElementById(id);
const state = { me: null, sessions: [], ws: null, session: null, lastMove: 0 };

async function api(path, options = {}) {
  const init = { credentials: 'same-origin', ...options };
  if (init.body && typeof init.body !== 'string') {
    init.headers = { ...(init.headers || {}), 'Content-Type': 'application/json' };
    init.body = JSON.stringify(init.body);
  }
  const response = await fetch(path, init);
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || `Request failed (${response.status})`);
  return data;
}

function show(id, visible) { $(id).classList.toggle('hidden', !visible); }
function setAuthUI(loggedIn) {
  show('loginView', !loggedIn);
  show('dashboardView', loggedIn);
  show('logoutButton', loggedIn);
  $('whoami').textContent = loggedIn ? `Signed in as ${state.me.username}` : '';
}

async function bootstrap() {
  try { state.me = await api('/api/me'); setAuthUI(true); await loadSessions(); }
  catch { state.me = null; setAuthUI(false); }
  try {
    const status = await api('/api/download-status');
    if (status.available) {
      $('downloadText').textContent = 'Download the customer support app. Your technician will provide the temporary session code.';
      $('downloadButton').classList.remove('disabled'); $('downloadButton').setAttribute('aria-disabled','false');
    } else {
      $('downloadText').textContent = 'The Windows support app is being prepared on this server. Your technician can provide the current build directly.';
    }
  } catch { $('downloadText').textContent = 'Windows support app status is temporarily unavailable.'; }
}

$('loginForm').addEventListener('submit', async (event) => {
  event.preventDefault(); $('loginError').textContent = '';
  try {
    state.me = await api('/api/login', { method:'POST', body:{ username:$('username').value, password:$('password').value } });
    $('password').value=''; setAuthUI(true); await loadSessions();
  } catch (e) { $('loginError').textContent = e.message; }
});
$('logoutButton').addEventListener('click', async () => { try { await api('/api/logout',{method:'POST'}); } catch {} location.reload(); });

$('newSessionButton').addEventListener('click', () => $('sessionDialog').showModal());
$('closeSessionDialog').addEventListener('click', () => $('sessionDialog').close());
$('closeCodeDialog').addEventListener('click', () => $('codeDialog').close());
$('sessionForm').addEventListener('submit', async (event) => {
  event.preventDefault(); $('sessionError').textContent='';
  try {
    const data = await api('/api/sessions', { method:'POST', body:{ customer_label:$('customerLabel').value, requested_control:$('requestControl').checked, requested_elevation:$('requestElevation').checked } });
    $('sessionDialog').close();
    const code = data.code.replace(/(\d{4})(\d{4})/, '$1 $2');
    $('sessionCode').textContent = code;
    $('codeDialog').dataset.rawCode = data.code;
    $('codeDialog').showModal();
    $('customerLabel').value=''; $('requestControl').checked=true; $('requestElevation').checked=false;
    await loadSessions();
  } catch (e) { $('sessionError').textContent=e.message; }
});
$('copyCodeButton').addEventListener('click', async () => {
  const code=$('codeDialog').dataset.rawCode || $('sessionCode').textContent.replace(/\D/g,'');
  try { await navigator.clipboard.writeText(code); $('copyCodeButton').textContent='Copied'; setTimeout(()=> $('copyCodeButton').textContent='Copy code',1200); } catch {}
});

async function loadSessions() {
  if (!state.me) return;
  try {
    const data = await api('/api/sessions'); state.sessions=data.sessions||[]; renderSessions();
  } catch (e) { console.error(e); }
}

function renderSessions() {
  const list=$('sessionsList'); list.textContent='';
  const now=new Date(); const today=now.toISOString().slice(0,10);
  $('waitingMetric').textContent=state.sessions.filter(s=>s.status==='waiting'||s.status==='approved').length;
  $('connectedMetric').textContent=state.sessions.filter(s=>s.status==='connected').length;
  $('todayMetric').textContent=state.sessions.filter(s=>String(s.created_at).slice(0,10)===today).length;
  if (!state.sessions.length) { list.innerHTML='<div class="session-row"><div class="session-label"><strong>No sessions yet</strong><span>Create a support session to generate a customer code.</span></div></div>'; return; }
  for (const s of state.sessions) {
    const row=document.createElement('div'); row.className='session-row'; row.tabIndex=0;
    row.innerHTML=`<div class="session-label"><strong>${escapeHTML(s.customer_label||'Unlabeled support session')}</strong><span>${escapeHTML(s.machine_name||'Waiting for customer')}</span></div><span class="status ${s.status}">${escapeHTML(s.status)}</span><span class="session-meta">${new Date(s.created_at).toLocaleString()}</span><span class="session-meta">••${escapeHTML(s.code_hint)}</span>`;
    row.addEventListener('click',()=>openViewer(s.id)); row.addEventListener('keydown',(e)=>{if(e.key==='Enter')openViewer(s.id)}); list.appendChild(row);
  }
}

async function openViewer(id) {
  const data=await api(`/api/sessions/${encodeURIComponent(id)}`); state.session=data.session;
  show('dashboardView',false); show('viewerView',true); show('publicView',false); show('loginView',false);
  $('viewerLabel').textContent=state.session.customer_label||'Remote support session'; $('viewerMachine').textContent=state.session.machine_name||'No customer connected'; setViewerStatus(state.session.status);
  connectViewerWS(id);
}
function closeViewer(){ if(state.ws){state.ws.close();state.ws=null} state.session=null; show('viewerView',false); show('dashboardView',true); show('publicView',true); loadSessions(); }
$('backButton').addEventListener('click',closeViewer);
$('endSessionButton').addEventListener('click',async()=>{if(!state.session)return;if(!confirm('End this support session? The customer connection will be revoked.'))return;try{await api(`/api/sessions/${encodeURIComponent(state.session.id)}/end`,{method:'POST'});}finally{closeViewer();}});
function setViewerStatus(status){$('viewerStatus').textContent=status; $('viewerStatus').className=`status-dot ${status==='connected'?'connected':''}`;}

function connectViewerWS(id){
  if(state.ws)state.ws.close(); const proto=location.protocol==='https:'?'wss':'ws'; const ws=new WebSocket(`${proto}://${location.host}/ws/tech?session=${encodeURIComponent(id)}`); ws.binaryType='arraybuffer'; state.ws=ws;
  ws.onopen=()=>setViewerStatus(state.session?.status==='connected'?'connected':'waiting');
  ws.onclose=()=>{ if(state.ws===ws)setViewerStatus('disconnected'); };
  ws.onerror=()=>setViewerStatus('connection error');
  ws.onmessage=async(event)=>{
    if(typeof event.data==='string'){try{const msg=JSON.parse(event.data);if(msg.type==='agent_status'){setViewerStatus(msg.status);if(msg.status==='connected'){$('screenPlaceholder').querySelector('strong').textContent='Customer connected';}}if(msg.type==='hello'){ $('viewerMachine').textContent=msg.machine_name||state.session.machine_name||'Customer connected'; }}catch{}return;}
    const blob=new Blob([event.data],{type:'image/jpeg'}); const bmp=await createImageBitmap(blob); const canvas=$('remoteCanvas'); canvas.width=bmp.width;canvas.height=bmp.height;canvas.getContext('2d').drawImage(bmp,0,0);bmp.close();canvas.style.display='block';$('screenPlaceholder').style.display='none';setViewerStatus('connected');
  };
}

function sendInput(input){ if(!state.ws||state.ws.readyState!==WebSocket.OPEN||!state.session?.requested_control)return; state.ws.send(JSON.stringify({type:'input',input})); }
const canvas=$('remoteCanvas');
function point(e){const r=canvas.getBoundingClientRect();return{x:Math.min(1,Math.max(0,(e.clientX-r.left)/r.width)),y:Math.min(1,Math.max(0,(e.clientY-r.top)/r.height))};}
canvas.addEventListener('mousemove',(e)=>{const n=performance.now();if(n-state.lastMove<33)return;state.lastMove=n;const p=point(e);sendInput({kind:'mouse_move',...p});});
canvas.addEventListener('mousedown',(e)=>{canvas.focus();const p=point(e);sendInput({kind:'mouse_button',button:e.button,down:true,...p});e.preventDefault();});
canvas.addEventListener('mouseup',(e)=>{const p=point(e);sendInput({kind:'mouse_button',button:e.button,down:false,...p});e.preventDefault();});
canvas.addEventListener('contextmenu',(e)=>e.preventDefault());
canvas.addEventListener('wheel',(e)=>{sendInput({kind:'mouse_wheel',delta:Math.sign(e.deltaY)*-120});e.preventDefault();},{passive:false});
canvas.addEventListener('keydown',(e)=>{if(['F5','F11','F12'].includes(e.key))return;sendInput({kind:'key',code:e.code,down:true});e.preventDefault();});
canvas.addEventListener('keyup',(e)=>{sendInput({kind:'key',code:e.code,down:false});e.preventDefault();});

function escapeHTML(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));}
setInterval(()=>{if(state.me&&!state.session)loadSessions();},5000);
bootstrap();
