'use strict';

const $ = (id) => document.getElementById(id);
const state = { csrf: null, myId: null, display: 0, stream: null, control: null, active: false,
                zoom: 100, rendering: false, pending: null, connecting: 0, lastX: 0, lastY: 0, retryCount: 0,
                lastFrameAt: 0, frameCounter: 0, frameWindowAt: 0, frameWidth: 1280, frameHeight: 720, immersive: false,
                displays: [], keyboardOpen: false, lastRequestedWidth: 0, menuOpen: false, fitUserSet: false };
const desktop = $('desktop');
const canvasContext = desktop.getContext('2d', { alpha: false, desynchronized: true });
const wsUrl = (path) => `wss://${location.host}${path}`;
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

function setNetwork(message, connected = false) {
  $('networkState').textContent = message;
  $('networkState').classList.toggle('connected', connected);
}
function showPair(error = '') {
  state.active = false;
  closeConnections();
  $('pairView').classList.remove('hidden'); $('remoteView').classList.add('hidden');
  if (state.immersive) setImmersive(false);
  if (state.keyboardOpen) setKeyboard(false);
  $('signOut').classList.add('hidden'); $('pairError').textContent = error;
  setNetwork('Not paired');
}
function showRemote() {
  $('pairView').classList.add('hidden'); $('remoteView').classList.remove('hidden');
  $('signOut').classList.remove('hidden'); state.active = true;
  // Mobile users get a compact session by default; settings remain one tap away.
  $('settingsPanel').classList.toggle('hidden', window.innerWidth < 700);
  $('settingsToggle').setAttribute('aria-expanded', String(window.innerWidth >= 700));
  if (window.matchMedia('(pointer: coarse)').matches && Math.min(window.innerWidth, window.innerHeight) < 800) {
    if (!state.fitUserSet) $('fitPick').value = 'fit';
    setImmersive(true);
  }
  syncQuickControls();
  requestAnimationFrame(updateScreenLayout);
}
async function api(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  if (state.csrf && options.method && options.method !== 'GET') headers['X-Abyss-CSRF'] = state.csrf;
  const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', ...options, headers });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) { const error = new Error(data.error || `Server returned ${response.status}`); error.status = response.status; throw error; }
  return data;
}
async function initialize() {
  try {
    const me = await api('/api/v1/me');
    state.csrf = me.csrf; state.myId = me.id;
    showRemote(); await getDisplays(); openConnections();
  } catch (error) {
    showPair(error.status === 401 ? '' : `Could not initialize a secure session: ${error.message}`);
  }
}
async function getDisplays() {
  const items = await api('/api/v1/displays');
  state.displays = items;
  const previous = Number($('displayPick').value || 0);
  $('displayPick').replaceChildren();
  for (const d of items) {
    const opt = document.createElement('option');
    opt.value = String(d.id);
    opt.textContent = `${d.primary ? '★ ' : ''}Display ${d.id + 1} · ${d.width}×${d.height}`;
    $('displayPick').append(opt);
  }
  state.display = items.some(d => d.id === previous) ? previous : items.find(d => d.primary)?.id ?? 0;
  $('displayPick').value = String(state.display);
  syncQuickControls();
}
function closeConnections() {
  ++state.connecting;
  for (const name of ['stream', 'control']) {
    const ws = state[name]; state[name] = null;
    if (ws) { ws.onclose = null; ws.onerror = null; try { ws.close(); } catch (_) {} }
  }
  state.pending = null;
}

// Only one frame is ever outstanding: the client asks for its next frame AFTER
// drawing the previous one. This removes stale JPEGs queuing in TCP on slow phones.
function requestFrame(epoch) {
  if (epoch !== state.connecting || !state.active) return;
  if (state.stream?.readyState === WebSocket.OPEN && state.stream.bufferedAmount < 128) {
    state.stream.send('next');
  }
}
function openConnections() {
  closeConnections();
  if (!state.active || !state.csrf) return;
  const epoch = state.connecting;
  $('screenOverlay').classList.remove('hidden');
  $('screenOverlay').textContent = 'Connecting to your desktop…';
  setNetwork('Connecting…');
  const fps = Number($('fpsPick').value);
  // Sharp is the only preset. Request enough pixels to keep fine desktop text
  // legible on Retina phones, but never exceed the selected display's width.
  // Higher resolutions cost extra JPEG encode time and LAN bandwidth.
  const dpr = Math.min(window.devicePixelRatio || 1, 3);
  const display = state.displays.find(d => d.id === state.display);
  const displayWidth = display?.width || 2560;
  const target = Math.max(1920, Math.round($('desktopStage').clientWidth * dpr * 1.35 * (state.zoom > 150 ? 1.15 : 1)));
  const width = Math.min(displayWidth, 3200, target);
  state.lastRequestedWidth = width;
  const path = `/api/v1/stream?display=${state.display}&fps=${fps}&quality=87&width=${width}`;
  const frames = new WebSocket(wsUrl(path));
  frames.binaryType = 'blob'; state.stream = frames;
  const control = new WebSocket(wsUrl('/api/v1/control'), ['abyss-v1', state.csrf]);
  state.control = control;
  frames.onmessage = (e) => {
    if (epoch !== state.connecting) return;
    state.pending = e.data;
    if (!state.rendering) void renderLatest(epoch);
  };
  frames.onopen = () => { if (epoch === state.connecting) { $('remoteError').textContent = ''; requestFrame(epoch); } };
  control.onopen = () => { if (epoch === state.connecting) setNetwork('Controls connected', true); };
  let retrying = false;
  const failed = () => {
    if (retrying || epoch !== state.connecting || !state.active) return;
    retrying = true;
    setNetwork('Connection interrupted');
    $('screenOverlay').classList.remove('hidden');
    $('screenOverlay').textContent = 'Disconnected. Reconnecting…';
    const retryDelay = Math.min(20000, 1600 * 2 ** Math.min(state.retryCount++, 4));
    sleep(retryDelay).then(async () => {
      if (epoch !== state.connecting || !state.active) return;
      try { await api('/api/v1/me'); }
      catch (error) { if (error.status === 401) { showPair('Device authorization expired or was revoked. Pair again.'); return; } }
      if (epoch === state.connecting && state.active) openConnections();
    });
  };
  frames.onclose = failed; control.onclose = failed;
  frames.onerror = () => { $('remoteError').textContent = 'Video connection interrupted. Check the host.'; };
  control.onerror = () => { $('remoteError').textContent = 'Input channel interrupted. Check the host.'; };
}
function updateScreenLayout() {
  const stage = $('desktopStage');
  if (stage.clientWidth === 0 || stage.clientHeight === 0) return;
  const viewportW = stage.clientWidth, viewportH = stage.clientHeight;
  const iw = state.frameWidth, ih = state.frameHeight;
  const viewMode = $('fitPick').value;
  let drawW, drawH;
  if (viewMode === 'stretch') { drawW = viewportW; drawH = viewportH; }
  else {
    const ratio = viewMode === 'fill'
      ? Math.max(viewportW / iw, viewportH / ih)
      : Math.min(viewportW / iw, viewportH / ih);
    drawW = iw * ratio; drawH = ih * ratio;
  }
  drawW = Math.max(1, Math.round(drawW * state.zoom / 100));
  drawH = Math.max(1, Math.round(drawH * state.zoom / 100));
  const surfaceW = Math.max(viewportW, drawW), surfaceH = Math.max(viewportH, drawH);
  const surface = $('screenSurface');
  surface.style.width = `${surfaceW}px`; surface.style.height = `${surfaceH}px`;
  desktop.style.width = `${drawW}px`; desktop.style.height = `${drawH}px`;
  desktop.style.left = `${Math.floor((surfaceW - drawW) / 2)}px`;
  desktop.style.top = `${Math.floor((surfaceH - drawH) / 2)}px`;
  const scroller = $('screenScroller');
  scroller.scrollLeft = Math.max(0, (surfaceW - viewportW) / 2);
  scroller.scrollTop = Math.max(0, (surfaceH - viewportH) / 2);
}
let layoutDebounce, resolutionDebounce;
function scheduleResolutionRefresh() {
  clearTimeout(resolutionDebounce);
  resolutionDebounce = setTimeout(() => {
    if (!state.active || state.keyboardOpen) return;
    const dpr = Math.min(window.devicePixelRatio || 1, 3);
    const width = Math.min(state.displays.find(d => d.id === state.display)?.width || 2560, 3200,
      Math.max(1920, Math.round($('desktopStage').clientWidth * dpr * 1.35 * (state.zoom > 150 ? 1.15 : 1))));
    if (Math.abs(width - state.lastRequestedWidth) > 240) openConnections();
  }, 480);
}
window.addEventListener('resize', () => {
  clearTimeout(layoutDebounce);
  layoutDebounce = setTimeout(() => {
    if (state.immersive && !state.fitUserSet) {
      $('fitPick').value = 'fit';
      syncQuickControls();
    }
    updateScreenLayout(); scheduleResolutionRefresh();
  }, 80);
});
async function renderLatest(epoch) {
  if (state.rendering) return;
  state.rendering = true;
  try {
    // The server won't send another frame until this one has been drawn.
    const blob = state.pending; state.pending = null;
    if (!blob) return;
    let frame;
    try { frame = await createImageBitmap(blob); }
    catch (_) { frame = await imageFallback(blob); }
    if (epoch !== state.connecting || !state.active) { if (frame.close) frame.close(); return; }
    if (desktop.width !== frame.width || desktop.height !== frame.height) {
      desktop.width = frame.width; desktop.height = frame.height;
      state.frameWidth = frame.width; state.frameHeight = frame.height;
      updateScreenLayout();
    }
    canvasContext.drawImage(frame, 0, 0);
    if (frame.close) frame.close();
    $('screenOverlay').classList.add('hidden');
    const now = performance.now();
    if (!state.frameWindowAt) state.frameWindowAt = now;
    if (++state.frameCounter >= 12) {
      const effective = 1000 * state.frameCounter / Math.max(1, now - state.frameWindowAt);
      $('streamStats').textContent = `${Math.round(effective)} fps received · ${state.frameWidth} × ${state.frameHeight}`;
      $('quickStatus').textContent = `${Math.round(effective)} FPS · ${state.frameWidth} × ${state.frameHeight}`;
      state.frameWindowAt = now; state.frameCounter = 0;
    }
    if (state.control?.readyState === WebSocket.OPEN) state.retryCount = 0;
  } catch (error) {
    $('remoteError').textContent = `Couldn't display a frame: ${error.message}`;
  } finally {
    // Always continue even after a damaged JPEG, avoiding a frozen session.
    if (epoch === state.connecting) requestFrame(epoch);
    state.rendering = false;
    // A reconnect may have delivered a frame while the old decoder was finishing.
    if (state.pending && state.active && epoch !== state.connecting) void renderLatest(state.connecting);
  }
}
async function imageFallback(blob) {
  const url = URL.createObjectURL(blob);
  try {
    const img = new Image();
    await new Promise((resolve, reject) => { img.onload = resolve; img.onerror = reject; img.src = url; });
    return img;
  } finally { URL.revokeObjectURL(url); }
}
function send(kind, data = {}) {
  if (!state.control || state.control.readyState !== WebSocket.OPEN) return false;
  state.control.send(JSON.stringify({ kind, ...data })); return true;
}
function mouseClick(button = 0) { send('mouse', { button, action: 'down' }); send('mouse', { button, action: 'up' }); }
function key(code, action) { send('key', { code, action }); }
function tapKey(code) { key(code, 'down'); key(code, 'up'); }
function combo(code) { key('ControlLeft', 'down'); tapKey(code); key('ControlLeft', 'up'); }
function absolutePointer(event) {
  const rect = desktop.getBoundingClientRect();
  const x = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
  const y = Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height));
  send('pointer', { x, y, display: state.display });
}
// Touch gestures: one-finger tap = left click; two-finger tap or stationary
// long press = right click. Hold briefly THEN move to drag. Moving immediately
// just moves the pointer. This avoids a mode switch for common desktop tasks.
let dragging = false, touchedAt = 0, travel = 0, sentAt = 0;
let dragButton = null, holdTimer = null, rightHoldTimer = null;
let dragReady = false, rightHeld = false, gesturePanning = false;
let twoFingerStart = 0, twoFingerMoved = false, twoFingerTravel = 0, twoFingerCenter = null;
const touches = new Map();
function clearTouchTimers() {
  clearTimeout(holdTimer); clearTimeout(rightHoldTimer);
  holdTimer = null; rightHoldTimer = null;
}
function releaseDrag() {
  clearTouchTimers(); dragReady = false;
  if (dragButton !== null) send('mouse', { button: dragButton, action: 'up' });
  dragButton = null;
}
function resetTouchGesture() {
  touches.clear(); gesturePanning = false; twoFingerStart = 0;
  twoFingerMoved = false; twoFingerTravel = 0; twoFingerCenter = null; rightHeld = false;
  releaseDrag(); dragging = false;
}
desktop.addEventListener('pointerdown', (event) => {
  if (!state.active) return;
  event.preventDefault();
  try { desktop.setPointerCapture(event.pointerId); } catch (_) {}
  if (event.pointerType === 'touch') {
    touches.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (touches.size > 1) {
      if (touches.size === 2) {
        const pair = Array.from(touches.values());
        twoFingerCenter = { x: (pair[0].x + pair[1].x) / 2, y: (pair[0].y + pair[1].y) / 2 };
        twoFingerStart = Date.now(); twoFingerMoved = false; twoFingerTravel = 0;
      } else twoFingerMoved = true;
      gesturePanning = true;
      releaseDrag(); dragging = false; return;
    }
  }
  if (gesturePanning) return; // First finger lifted during a multi-touch gesture.
  dragging = true; travel = 0; touchedAt = Date.now(); rightHeld = false;
  state.lastX = event.clientX; state.lastY = event.clientY;
  if ($('modePick').value === 'direct') absolutePointer(event);
  if (event.pointerType === 'touch') {
    // Only arm dragging after a deliberate pause. No mouse-down is sent until
    // the user moves, leaving a still long-press available for right-click.
    holdTimer = setTimeout(() => {
      if (dragging && !gesturePanning && touches.size === 1 && travel < 12)
        dragReady = true;
    }, 330);
    rightHoldTimer = setTimeout(() => {
      if (dragging && !gesturePanning && touches.size === 1 && travel < 12 && dragButton === null) {
        rightHeld = true; dragReady = false; mouseClick(2);
      }
    }, 660);
  } else {
    dragButton = event.button === 2 ? 2 : event.button === 1 ? 1 : 0;
    send('mouse', { button: dragButton, action: 'down' });
  }
});
desktop.addEventListener('pointermove', (event) => {
  if (!state.active) return;
  if (event.pointerType === 'touch' && touches.has(event.pointerId)) {
    const previous = touches.get(event.pointerId);
    touches.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (gesturePanning) {
      const deltaY = event.clientY - previous.y;
      const deltaX = event.clientX - previous.x;
      twoFingerTravel += Math.abs(deltaX) + Math.abs(deltaY);
      if (twoFingerTravel > 12) twoFingerMoved = true;
      const scroll = $('screenScroller');
      if (state.zoom > 100) {
        scroll.scrollLeft -= deltaX; scroll.scrollTop -= deltaY;
      } else if (performance.now() - sentAt > 28 && Math.abs(deltaY) > 2 && state.control?.bufferedAmount < 4096) {
        send('wheel', { delta: Math.max(-360, Math.min(360, Math.round(deltaY * 7))) });
        sentAt = performance.now();
      }
      return;
    }
  }
  if (gesturePanning || rightHeld) return;
  const dx = event.clientX - state.lastX, dy = event.clientY - state.lastY;
  state.lastX = event.clientX; state.lastY = event.clientY;
  if (dragging) {
    travel += Math.abs(dx) + Math.abs(dy);
    if (event.pointerType === 'touch' && travel > 12) {
      const startDrag = dragReady && dragButton === null;
      clearTouchTimers();
      if (startDrag) {
        dragButton = 0; send('mouse', { button: 0, action: 'down' });
      }
      dragReady = false;
    }
  }
  const now = performance.now();
  if (now - sentAt < 16 || state.control?.bufferedAmount > 4096) return;
  sentAt = now;
  if ($('modePick').value === 'direct') absolutePointer(event);
  else if (dragging) send('move', { dx: Math.round(dx * 1.5), dy: Math.round(dy * 1.5) });
});
function pointerEnd(event) {
  if (event.pointerType === 'touch') touches.delete(event.pointerId);
  if (gesturePanning) {
    if (touches.size === 0) {
      // A two-finger tap is distinct from a scrolling gesture. Only fire it
      // once, after both contacts are released and with minimal movement.
      if (!twoFingerMoved && twoFingerStart && Date.now() - twoFingerStart < 420) {
        if ($('modePick').value === 'direct' && twoFingerCenter) {
          absolutePointer({ clientX: twoFingerCenter.x, clientY: twoFingerCenter.y });
        }
        mouseClick(2);
      }
      resetTouchGesture();
    }
    return;
  }
  if (!dragging) return;
  event.preventDefault(); dragging = false;
  const wasDown = dragButton !== null;
  const wasRightHeld = rightHeld;
  releaseDrag(); rightHeld = false;
  if (event.pointerType === 'touch' && !wasDown && !wasRightHeld && travel < 15 && Date.now() - touchedAt < 950)
    mouseClick();
}
desktop.addEventListener('pointerup', pointerEnd);
desktop.addEventListener('pointercancel', (event) => {
  // Cancellation (e.g. iOS system gesture) must never generate a click.
  if (event.pointerType === 'touch') touches.delete(event.pointerId);
  twoFingerMoved = true;
  if (touches.size === 0) resetTouchGesture();
  else { releaseDrag(); dragging = false; }
});
window.addEventListener('blur', resetTouchGesture);
// Suppress iOS long-press selection and duplicate synthetic context menus.
// Hardware-mouse right-click is already handled by pointerdown/pointerup.
desktop.addEventListener('contextmenu', (event) => event.preventDefault());
desktop.addEventListener('wheel', (event) => {
  if (!state.active) return;
  event.preventDefault(); send('wheel', { delta: event.deltaY > 0 ? -120 : 120 });
}, { passive: false });

document.addEventListener('keydown', (event) => {
  if (!state.active || !state.control || state.control.readyState !== WebSocket.OPEN) return;
  const tag = event.target?.tagName || '';
  if (['INPUT', 'TEXTAREA', 'SELECT', 'BUTTON'].includes(tag) || event.target?.isContentEditable) return;
  if (event.code === 'F5' || ((event.ctrlKey || event.metaKey) && event.code === 'KeyR')) return;
  event.preventDefault(); key(event.code, 'down');
});
document.addEventListener('keyup', (event) => {
  if (!state.active) return;
  const tag = event.target?.tagName || '';
  if (['INPUT', 'TEXTAREA', 'SELECT', 'BUTTON'].includes(tag)) return;
  event.preventDefault(); key(event.code, 'up');
});

$('pairForm').addEventListener('submit', async (event) => {
  event.preventDefault(); $('pairError').textContent = '';
  const button = $('pairForm').querySelector('button'); button.disabled = true;
  try {
    await api('/api/v1/pair', { method: 'POST', body: JSON.stringify({ code: $('pairCode').value, label: $('deviceName').value }) });
    $('pairCode').value = ''; await initialize();
  } catch (error) { $('pairError').textContent = error.message; }
  finally { button.disabled = false; }
});
$('displayPick').addEventListener('change', () => { state.display = Number($('displayPick').value); syncQuickControls(); openConnections(); });
$('fpsPick').addEventListener('change', openConnections);
$('refresh').addEventListener('click', async () => {
  try { await getDisplays(); openConnections(); } catch (error) { $('remoteError').textContent = error.message; }
});
$('zoom').addEventListener('input', () => {
  state.zoom = Number($('zoom').value); $('zoomValue').textContent = `${state.zoom}%`;
  updateScreenLayout();
  scheduleResolutionRefresh();
});
$('fitPick').addEventListener('change', () => { state.fitUserSet = true; syncQuickControls(); updateScreenLayout(); });
$('modePick').addEventListener('change', syncQuickControls);
$('settingsToggle').addEventListener('click', () => {
  const visible = $('settingsPanel').classList.toggle('hidden');
  $('settingsToggle').setAttribute('aria-expanded', String(!visible));
  updateScreenLayout();
});
function setImmersive(enabled) {
  state.immersive = enabled;
  document.body.classList.toggle('immersive', enabled);
  document.documentElement.classList.toggle('remote-immersive', enabled);
  if (!enabled) setQuickMenu(false);
  if (enabled && !state.fitUserSet) $('fitPick').value = 'fit';
  syncQuickControls();
  $('immersive').setAttribute('aria-pressed', String(enabled));
  $('immersive').querySelector('span').textContent = enabled ? 'Exit full screen' : 'Full screen';
  // The bezel protects a visual interaction area, NOT iOS system gestures.
  // Safari does not allow a webpage to disable Notification/Control Center.
  requestAnimationFrame(() => { updateScreenLayout(); scheduleResolutionRefresh(); });
}

function syncQuickControls() {
  $('quickFit').value = $('fitPick').value;
  $('quickMode').value = $('modePick').value;
  const quickDisplay = $('quickDisplay');
  if (quickDisplay.options.length !== $('displayPick').options.length) {
    quickDisplay.replaceChildren();
    for (const o of $('displayPick').options) quickDisplay.add(new Option(o.textContent, o.value));
  }
  quickDisplay.value = String(state.display);
}
function setQuickMenu(open) {
  state.menuOpen = !!open;
  $('quickPanel').classList.toggle('hidden', !open);
  $('quickToggle').setAttribute('aria-expanded', String(open));
  $('quickToggle').setAttribute('aria-label', open ? 'Hide remote controls' : 'Open remote controls');
}
$('quickToggle').addEventListener('click', () => setQuickMenu(!state.menuOpen));
$('quickClose').addEventListener('click', () => setQuickMenu(false));
$('quickKeyboard').addEventListener('click', () => { setQuickMenu(false); setKeyboard(true); });
$('quickRight').addEventListener('click', () => mouseClick(2));
$('quickScrollUp').addEventListener('click', () => send('wheel', { delta: 360 }));
$('quickScrollDown').addEventListener('click', () => send('wheel', { delta: -360 }));
$('quickFit').addEventListener('change', () => {
  $('fitPick').value = $('quickFit').value; state.fitUserSet = true;
  updateScreenLayout();
});
$('quickMode').addEventListener('change', () => { $('modePick').value = $('quickMode').value; });
$('quickDisplay').addEventListener('change', () => {
  $('displayPick').value = $('quickDisplay').value;
  state.display = Number($('quickDisplay').value);
  openConnections();
});
$('quickReconnect').addEventListener('click', () => { setQuickMenu(false); $('refresh').click(); });
$('quickExit').addEventListener('click', () => setImmersive(false));
// Keeping the floating sheet off the work surface prevents most accidental
// interaction with the UI while swiping the streamed Windows desktop.
$('desktopStage').addEventListener('pointerdown', () => { if (state.menuOpen) setQuickMenu(false); });

$('immersive').addEventListener('click', () => setImmersive(!state.immersive));
$('leaveImmersive').addEventListener('click', () => setImmersive(false));
document.addEventListener('keydown', e => {
  if (e.code === 'Escape' && state.immersive) setImmersive(false);
});
$('immersiveRight').addEventListener('click', () => mouseClick(2));
$('immersiveScrollUp').addEventListener('click', () => send('wheel', { delta: 360 }));
$('immersiveScrollDown').addEventListener('click', () => send('wheel', { delta: -360 }));
$('mouseLeft').addEventListener('click', () => mouseClick(0));
$('mouseRight').addEventListener('click', () => mouseClick(2));
$('scrollUp').addEventListener('click', () => send('wheel', { delta: 360 }));
$('scrollDown').addEventListener('click', () => send('wheel', { delta: -360 }));
for (const [id, code] of [['escapeKey', 'Escape'], ['tabKey', 'Tab'], ['enterKey', 'Enter'], ['backspaceKey', 'Backspace']])
  $(id).addEventListener('click', () => tapKey(code));
$('ctrlC').addEventListener('click', () => combo('KeyC'));
$('ctrlV').addEventListener('click', () => combo('KeyV'));
function sendText() {
  const text = $('textSend').value;
  if (!text) return;
  if (send('text', { text })) $('textSend').value = '';
  else $('remoteError').textContent = 'Controls are disconnected. Keep your text and reconnect.';
}
function syncKeyboardViewport() {
  // VisualViewport shrinks when the iPhone software keyboard appears; move
  // the text sheet above it while leaving the remote canvas in place.
  const vv = window.visualViewport;
  const bottom = vv ? Math.max(0, window.innerHeight - vv.height - vv.offsetTop) : 0;
  document.documentElement.style.setProperty('--keyboard-offset', `${Math.round(bottom)}px`);
}
window.visualViewport?.addEventListener('resize', syncKeyboardViewport);
window.visualViewport?.addEventListener('scroll', syncKeyboardViewport);
function setKeyboard(open) {
  state.keyboardOpen = open;
  $('keyboardSheet').classList.toggle('hidden', !open);
  for (const id of ['showKeyboard','immersiveKeyboard']) $(id).setAttribute('aria-expanded', String(open));
  if (open) {
    $('keyboardStatus').textContent = '';
    $('keyboardText').focus({ preventScroll: true }); // Must run during user tap for iOS keyboard.
  } else {
    $('keyboardText').blur();
    document.documentElement.style.setProperty('--keyboard-offset', '0px');
  }
  syncKeyboardViewport();
}
$('showKeyboard').addEventListener('click', () => setKeyboard(!state.keyboardOpen));
$('immersiveKeyboard').addEventListener('click', () => setKeyboard(!state.keyboardOpen));
$('closeKeyboard').addEventListener('click', () => setKeyboard(false));
function sendKeyboardText() {
  const text = $('keyboardText').value;
  if (!text) { $('keyboardStatus').textContent = 'Type something first.'; return; }
  if (!send('text', { text })) {
    $('keyboardStatus').textContent = 'Disconnected — text preserved. Reconnect first.';
    return;
  }
  $('keyboardText').value = '';
  $('keyboardStatus').textContent = 'Text sent to the active Windows app.';
}
$('keyboardSend').addEventListener('click', sendKeyboardText);
for (const [id,code] of [['keyboardTab','Tab'],['keyboardEnter','Enter'],['keyboardBackspace','Backspace']]) {
  $(id).addEventListener('click', () => {
    if (state.control?.readyState !== WebSocket.OPEN) { $('keyboardStatus').textContent = 'Controls disconnected.'; return; }
    tapKey(code);
    $('keyboardText').focus({ preventScroll: true });
  });
}
// Explicit Send instead of injecting text on every iPhone keypress. It works
// with iOS autocorrect, paste, and Unicode without sending partial compositions.
$('sendText').addEventListener('click', sendText);
$('copyPc').addEventListener('click', async () => {
  try {
    const data = await api('/api/v1/clipboard');
    $('textSend').value = data.text || '';
    $('textSend').focus(); $('textSend').select();
    $('remoteError').textContent = 'PC clipboard loaded into the text box. Copy it from there if needed.';
  } catch (error) { $('remoteError').textContent = error.message; }
});
$('pushClipboard').addEventListener('click', async () => {
  try {
    const text = $('textSend').value;
    if (!text) { $('remoteError').textContent = 'Type or paste something into the text box first.'; return; }
    await api('/api/v1/clipboard', { method: 'POST', body: JSON.stringify({ text }) });
    $('remoteError').textContent = 'Copied text into your Windows clipboard. Press Ctrl+V remotely to paste.';
  } catch (error) { $('remoteError').textContent = error.message; }
});
$('textSend').addEventListener('keydown', (event) => { if (event.key === 'Enter') { event.preventDefault(); sendText(); } });
$('devicesButton').addEventListener('click', async () => {
  const panel = $('devicesPanel'); panel.classList.toggle('hidden');
  if (!panel.classList.contains('hidden')) await populateDevices();
});
async function populateDevices() {
  try {
    const items = await api('/api/v1/devices'); $('devicesList').replaceChildren();
    for (const d of items) {
      const row = document.createElement('div'); row.className = 'device-entry';
      const copy = document.createElement('div');
      const name = document.createElement('strong'); name.textContent = d.name + (d.id === state.myId ? ' · this device' : '');
      const date = document.createElement('small'); date.textContent = `Paired ${new Date(d.createdUtc).toLocaleDateString()}`;
      copy.append(name, date);
      const revoke = document.createElement('button'); revoke.textContent = 'Revoke'; revoke.className = 'ghost';
      revoke.addEventListener('click', async () => {
        if (!window.confirm(`Revoke access for ${d.name}?`)) return;
        try { await api(`/api/v1/devices/${encodeURIComponent(d.id)}`, { method: 'DELETE' });
              if (d.id === state.myId) showPair('Device revoked. Re-pair to access this PC.');
              else await populateDevices(); }
        catch (error) { $('remoteError').textContent = error.message; }
      });
      row.append(copy, revoke); $('devicesList').append(row);
    }
  } catch (error) { $('remoteError').textContent = error.message; }
}
$('signOut').addEventListener('click', async () => {
  if (!confirm('Sign out and revoke this device? You will need a new pairing code.')) return;
  try { await api('/api/v1/logout', { method: 'POST' }); showPair('Signed out.'); }
  catch (error) { $('remoteError').textContent = error.message; }
});
window.addEventListener('pagehide', () => closeConnections());
// iOS Safari can restore a page from its back-forward cache without reloading JS.
window.addEventListener('pageshow', event => {
  if (event.persisted && state.active) openConnections();
});
initialize();
