// Abyss Remote browser client: the stream view (video, sound, mouse/touch/keyboard input) and
// the switching between the three screens: pairing (pairing.js), home (home.js) and stream.
// Video comes from one of two stream modules with the same callbacks: jpeg-stream.js (Desktop
// mode) or video-stream.js (Game mode, hardware H.264 + WebCodecs). Input goes over one
// authenticated WebSocket as small JSON messages (see docs/API.md). No third-party code.
import { startJpegStream } from './jpeg-stream.js';
import { startVideoStream, gameModeSupported } from './video-stream.js';
import { unlockAudio, audioUnlocked, startAudio } from './audio.js';
import { watchGamepads } from './gamepad.js';
import { api, fetchFile, refreshIdentity, session } from './api.js';
import { $, el, icon, saveBlob } from './dom.js';
import { fileNameFromDisposition } from './format.js';
import { initPairing, clearPairCode } from './pairing.js';
import { initHome, showHomeScreen, hideHomeScreen } from './home.js';
import { createMonitorPicker, monitorLabel, pickMonitor } from './monitors.js';

const desktop = $('desktop');
const canvasContext = desktop.getContext('2d', { alpha: false, desynchronized: true });
const PROTOCOL = 'abyss-v1';
const CLOSE_REVOKED = 4001, CLOSE_REPLACED = 4008, CLOSE_BUSY = 4029, CLOSE_UNSUPPORTED = 4415;
const SETTINGS_KEY = 'abyss-remote:settings';
const STREAMING_KEY = 'abyss-remote:streaming';
const UNPAIRED = 'This device is no longer paired. Pair it again with a new code.';
const WATCHING = 'Watch only is on. Turn it off to control the PC.';

// `active` means the stream view is on screen. `display` is the number of the monitor being
// shown; `displayName` is the Windows device name of the monitor the user chose, which is
// what gets remembered (numbers change when monitors are plugged in or rearranged).
// `watchOnly` stops every kind of input from being sent.
const state = {
  active: false, stopped: false, watchOnly: false, monitorsOpen: false,
  mode: 'desktop', gameAvailable: false, fallbackNotice: '', soundOn: false, audio: null,
  displays: [], display: 0, displayName: null,
  stream: null, control: null, epoch: 0, retryCount: 0, retryTimer: 0,
  zoom: 100, frameWidth: 1280, frameHeight: 720, lastRequestedWidth: 0,
  desktopWidth: 0, desktopHeight: 0,
  cursor: { x: 0, y: 0, visible: false, hotX: 0, hotY: 0, width: 0, height: 0 },
  immersive: false, keyboardOpen: false, menuOpen: false, fitUserSet: false,
  pressedKeys: new Set(), lastX: 0, lastY: 0, lockedDx: 0, lockedDy: 0,
  stats: { frames: 0, bytes: 0, since: 0, idle: false },
};

// ---------------------------------------------------------------- status and settings

function setNetwork(message, connected = false) {
  $('networkState').textContent = message;
  $('networkState').classList.toggle('connected', connected);
}
function showOverlay(message) {
  $('screenOverlay').textContent = message;
  $('screenOverlay').classList.remove('hidden');
}
function hideOverlay() { $('screenOverlay').classList.add('hidden'); }
// Messages go on the line under the stage. Phones hide that line (the stream fills the
// screen), so the same text also appears over the picture for a few seconds.
let noteTimer = 0;
function showError(message) {
  $('remoteError').textContent = message;
  $('stageNote').textContent = message;
  $('stageNote').classList.toggle('hidden', !message);
  clearTimeout(noteTimer);
  if (message) noteTimer = setTimeout(() => $('stageNote').classList.add('hidden'), 6000);
}

// Viewer preferences live in this browser only (never secrets). Private browsing may block
// storage; the app then simply starts with defaults.
function loadSettings() {
  try { return JSON.parse(localStorage.getItem(SETTINGS_KEY) || '{}') || {}; } catch { return {}; }
}
function saveSettings() {
  const data = { displayName: state.displayName, mode: $('modePick').value, fps: $('fpsPick').value,
                 fit: state.fitUserSet ? $('fitPick').value : undefined, streamMode: state.mode, sound: state.soundOn, watchOnly: state.watchOnly,
                 gameHeight: $('gameHeightPick').value, gameFps: $('gameFpsPick').value, bitrate: $('bitratePick').value };
  try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(data)); } catch { /* not remembered */ }
}
function applySettings(saved) {
  const pick = (select, value) => {
    if (value !== undefined && [...$(select).options].some(o => o.value === String(value))) { $(select).value = String(value); return true; }
    return false;
  };
  pick('modePick', saved.mode);
  pick('fpsPick', saved.fps);
  pick('gameHeightPick', saved.gameHeight);
  pick('gameFpsPick', saved.gameFps);
  pick('bitratePick', saved.bitrate);
  if (pick('fitPick', saved.fit)) state.fitUserSet = true;
  if (typeof saved.displayName === 'string') state.displayName = saved.displayName;
  if (saved.streamMode === 'game') state.mode = 'game'; // confirmed once we know the PC and browser support it
  state.soundOn = saved.sound === true; // still needs a tap after each page load before audio may play
  state.watchOnly = saved.watchOnly === true; // remembered, so "only watching" never silently turns into control
}

// ---------------------------------------------------------------- views

const views = { pair: $('pairView'), home: $('homeView'), remote: $('remoteView') };
function showView(name) {
  for (const [key, view] of Object.entries(views)) view.classList.toggle('hidden', key !== name);
}

// A reload (or iOS throwing the page away in the background) should come back to the stream
// that was open, not to the home screen. Kept per tab, and only while a stream is showing.
function rememberStreaming(on) {
  try { if (on) sessionStorage.setItem(STREAMING_KEY, '1'); else sessionStorage.removeItem(STREAMING_KEY); } catch { /* not remembered */ }
}
function wasStreaming() {
  try { return sessionStorage.getItem(STREAMING_KEY) === '1'; } catch { return false; }
}

// Ends the stream view: closes its sockets and puts away everything that belongs to it.
function leaveStream() {
  state.active = false;
  closeConnections();
  if (document.pointerLockElement) document.exitPointerLock();
  if (state.immersive) setImmersive(false);
  if (state.keyboardOpen) setKeyboard(false);
  if (state.monitorsOpen) setMonitorSheet(false);
  canvasContext.clearRect(0, 0, desktop.width, desktop.height); // the next stream must not start with this one's last picture
  rememberStreaming(false);
}
function showPair(error = '') {
  leaveStream();
  hideHomeScreen();
  session.csrf = null;
  showView('pair');
  $('pairError').textContent = error;
  setNetwork('Not paired');
}
function showHome() {
  leaveStream();
  showView('home');
  setNetwork('Paired', true);
  showHomeScreen();
}
function showRemote() {
  hideHomeScreen();
  showView('remote');
  rememberStreaming(true);
  state.active = true;
  // Phones start with a compact session; settings stay one tap away.
  $('settingsPanel').classList.toggle('hidden', window.innerWidth < 700);
  $('settingsToggle').setAttribute('aria-expanded', String(window.innerWidth >= 700));
  if (window.matchMedia('(pointer: coarse)').matches && Math.min(window.innerWidth, window.innerHeight) < 800) {
    if (!state.fitUserSet) $('fitPick').value = 'fit';
    setImmersive(true);
  }
  syncQuickControls();
  requestAnimationFrame(updateScreenLayout);
}

// ---------------------------------------------------------------- start

// The CSRF token changes whenever the host restarts, so this and every reconnect fetch the
// identity again (refreshIdentity in api.js).
async function initialize() {
  try {
    await refreshIdentity();
  } catch (error) {
    showPair(error.status === 401 ? '' : `Could not reach the PC: ${error.message}`);
    return;
  }
  clearPairCode();
  await detectGameMode();
  if (wasStreaming()) openStream(); else showHome();
}

// Shows a monitor. `display` is a monitor from the PC's list, 'main' for the main one, or
// nothing for the one used last time. `mode` is 'desktop' or 'game' (nothing keeps the last
// choice; Game mode quietly becomes Desktop mode where it is not available).
async function openStream({ display, mode, sound = false } = {}) {
  showRemote();
  showError('');
  showOverlay('Connecting to your desktop…');
  if (display && display !== 'main') state.displayName = display.name;
  if (mode) setStreamMode(mode, { restart: false, remember: false });
  if (sound && await soundPossible()) state.soundOn = true;
  syncToggles();
  try {
    await getDisplays();
  } catch (error) {
    if (error.status === 401) showPair(UNPAIRED);
    else scheduleReconnect('The PC is not answering. Retrying…');
    return;
  }
  const main = display === 'main' && state.displays.find(d => d.primary);
  if (main) { state.display = main.id; state.displayName = main.name; }
  showSessionName();
  saveSettings();
  connect();
}

// Game mode needs a hardware encoder on the PC and WebCodecs H.264 in this browser.
async function detectGameMode() {
  state.gameAvailable = session.features.gameMode === true && await gameModeSupported();
  $('streamModeLabel').classList.toggle('hidden', !state.gameAvailable);
  $('quickStreamModeLabel').classList.toggle('hidden', !state.gameAvailable);
  setStreamMode(state.gameAvailable ? state.mode : 'desktop', { restart: false });
}

function setStreamMode(mode, { restart = true, remember = true } = {}) {
  state.mode = mode === 'game' && state.gameAvailable ? 'game' : 'desktop';
  document.body.classList.toggle('game-mode', state.mode === 'game');
  $('streamMode').value = state.mode;
  $('quickStreamMode').value = state.mode;
  $('quickGameHeight').value = $('gameHeightPick').value;
  if (state.mode !== 'game') { hideCursor(); if (document.pointerLockElement) document.exitPointerLock(); }
  if (remember) saveSettings();
  if (restart) restartVideo();
}
async function getDisplays() {
  state.displays = await api('/api/v1/displays');
  state.display = pickMonitor(state.displays, state.displayName)?.id ?? 0;
  showSessionName();
}
// The stream's title is the monitor on screen, written like on its card ("2 · Aurora 32 4K").
function showSessionName() {
  const display = state.displays.find(d => d.id === state.display);
  $('sessionName').textContent = display ? monitorLabel(display) : 'Desktop';
}

// ---------------------------------------------------------------- connections

const wsUrl = (path) => `wss://${location.host}${path}`;

function closeConnections() {
  state.epoch++;
  clearTimeout(state.retryTimer);
  state.retryTimer = 0;
  stopAudio();
  state.stream?.stop();
  state.stream = null;
  const control = state.control;
  state.control = null;
  if (control) { control.onclose = null; try { control.close(); } catch { /* already closed */ } }
  releaseAllInput(false);
}

// Opens video and input. Every callback checks its epoch, so events from an older
// connection can never affect a newer one.
function connect() {
  closeConnections();
  if (!state.active || !session.csrf) return;
  state.stopped = false;
  showOverlay('Connecting to your desktop…');
  setNetwork('Connecting…');
  openControl(state.epoch);
  openVideo(state.epoch);
  openAudio(state.epoch);
}

// ---------------------------------------------------------------- on/off switches

// Every on/off switch of the stream is one entry in this list. Each entry gets a button in
// the top bar and one in the phone's Controls panel, so a new switch (an on-screen gamepad,
// special keys) only has to be added here.
const toggles = [
  { id: 'watch', icon: 'eye', label: 'Watch only', hint: 'Look without sending mouse, keyboard or controller input',
    isOn: () => state.watchOnly, flip: () => setWatchOnly(!state.watchOnly) },
  { id: 'sound', icon: 'sound', label: 'Sound', hint: "Play the PC's sound on this device",
    isOn: () => state.soundOn, flip: () => toggleSound() },
];

function buildToggles() {
  for (const [place, className] of [[$('topToggles'), 'toolbutton'], [$('quickToggles'), '']]) {
    place.replaceChildren(...toggles.map((toggle) => {
      const button = el('button', className);
      button.type = 'button';
      button.title = toggle.hint;
      button.dataset.toggle = toggle.id;
      const shown = el('span', 'toggle-state');
      shown.setAttribute('aria-hidden', 'true'); // aria-pressed already says it
      button.append(icon(toggle.icon), el('span', '', toggle.label), shown);
      button.addEventListener('click', toggle.flip);
      return button;
    }));
  }
}

// Shows each switch as on or off wherever it has a button.
function syncToggles() {
  for (const button of document.querySelectorAll('[data-toggle]')) {
    const on = toggles.find((toggle) => toggle.id === button.dataset.toggle).isOn();
    button.setAttribute('aria-pressed', String(on));
    button.querySelector('.toggle-state').textContent = on ? 'On' : 'Off';
  }
  $('watchBadge').classList.toggle('hidden', !state.watchOnly);
}

// Watch only: the picture and sound keep coming, but no pointer, keyboard, text or
// controller input leaves this device (see send). A badge on the picture shows it is on.
function setWatchOnly(on) {
  if (on) releaseAllInput(); // let go of every key and button on the PC before input stops
  state.watchOnly = on;
  if (on && document.pointerLockElement) document.exitPointerLock();
  if (!on) gamepads.resync(); // a controller's current state is sent again
  syncToggles();
  saveSettings();
  showError(on ? 'Watch only is on. Nothing you do here reaches the PC.' : '');
}

// ---------------------------------------------------------------- sound

function openAudio(epoch) {
  if (!state.soundOn || !state.active || state.stopped || state.audio || epoch !== state.epoch) return;
  if (!audioUnlocked()) { armAudioUnlock(); return; }
  state.audio = startAudio({
    url: wsUrl('/api/v1/audio'),
    protocols: [PROTOCOL, session.csrf],
    onClose: (code, reason) => {
      if (epoch !== state.epoch) return;
      state.audio = null;
      if (code === CLOSE_UNSUPPORTED) {
        state.soundOn = false;
        syncToggles();
        saveSettings();
        showError(`Sound is not available: ${reason || 'no audio output on the PC'}.`);
        return;
      }
      if (code === CLOSE_REVOKED || code === CLOSE_REPLACED) return; // video and input handle these
      // Sound alone dropped (for example the PC's audio device changed): try again shortly.
      setTimeout(() => openAudio(epoch), 2000);
    },
  });
}

function stopAudio() {
  state.audio?.stop();
  state.audio = null;
}

// A tap that will end in a stream with sound (starting a game) unlocks audio at once, while
// the browser still counts it as the user's action.
let soundUnlock = Promise.resolve(true);
function prepareSound() {
  soundUnlock = unlockAudio().then(() => true, () => false);
}
// False only when this browser cannot play the PC's sound at all. An unlock that is merely
// still waiting after a moment counts as possible: the next tap then starts the sound.
function soundPossible() {
  return Promise.race([soundUnlock, new Promise((resolve) => setTimeout(resolve, 1500, true))]);
}

// After a page load, sound may only start inside a tap or key press.
let unlockArmed = false;
function armAudioUnlock() {
  if (unlockArmed) return;
  unlockArmed = true;
  const unlock = async () => {
    window.removeEventListener('pointerup', unlock, true);
    window.removeEventListener('keydown', unlock, true);
    unlockArmed = false;
    try { await unlockAudio(); openAudio(state.epoch); } catch { /* stays off until the Sound button is used */ }
  };
  window.addEventListener('pointerup', unlock, true);
  window.addEventListener('keydown', unlock, true);
}

async function toggleSound() {
  if (state.soundOn) {
    state.soundOn = false;
    stopAudio();
  } else {
    try {
      await unlockAudio();
      state.soundOn = true;
      openAudio(state.epoch);
    } catch (error) {
      showError(error.message);
    }
  }
  syncToggles();
  saveSettings();
}

function openControl(epoch) {
  const control = new WebSocket(wsUrl('/api/v1/control'), [PROTOCOL, session.csrf]);
  state.control = control;
  control.onopen = () => {
    if (epoch !== state.epoch) return;
    setNetwork('Connected', true);
    gamepads.resync();
  };
  control.onmessage = (event) => { if (epoch === state.epoch) controlMessage(event.data); };
  control.onclose = (event) => { if (epoch === state.epoch) connectionLost('control', event.code); };
}

function requestedWidth() {
  // Enough pixels for sharp text on high-DPI phones, never more than the monitor has.
  const dpr = Math.min(window.devicePixelRatio || 1, 3);
  const displayWidth = state.displays.find(d => d.id === state.display)?.width || 2560;
  const target = Math.max(1920, Math.round($('desktopStage').clientWidth * dpr * 1.35 * (state.zoom > 150 ? 1.15 : 1)));
  return Math.min(displayWidth, 3840, target);
}

function openVideo(epoch) {
  state.stats = { frames: 0, bytes: 0, since: performance.now(), idle: false };
  const common = {
    protocols: [PROTOCOL, session.csrf],
    onFrame: (image, info) => { if (epoch === state.epoch) drawFrame(image, info); },
    onStatus: (status) => { if (epoch === state.epoch) streamStatus(status); },
    onClose: (code, reason) => { if (epoch === state.epoch) connectionLost('video', code, reason); },
  };
  if (state.mode === 'game') {
    const query = `display=${state.display}&fps=${$('gameFpsPick').value}&height=${$('gameHeightPick').value}&bitrate=${$('bitratePick').value}`;
    state.stream = startVideoStream({
      ...common,
      url: wsUrl(`/api/v1/video?${query}`),
      onConfig: (config) => {
        if (epoch !== state.epoch) return;
        state.desktopWidth = config.desktopWidth;
        state.desktopHeight = config.desktopHeight;
        if ($('remoteError').textContent === state.fallbackNotice) showError(''); // Game mode works again
      },
      onCursor: (cursor) => { if (epoch === state.epoch) moveCursor(cursor); },
      onCursorImage: (image) => { if (epoch === state.epoch) setCursorImage(image); },
      onStats: (stats) => { if (epoch === state.epoch) gameStats(stats); },
    });
    return;
  }
  const width = requestedWidth();
  state.lastRequestedWidth = width;
  state.stream = startJpegStream({
    ...common,
    url: wsUrl(`/api/v1/stream?display=${state.display}&fps=${Number($('fpsPick').value)}&quality=85&width=${width}`),
  });
}

// Display, frame rate and resolution changes only restart the video; input stays connected.
function restartVideo() {
  if (!state.active || state.stopped || !state.control) { connect(); return; }
  state.stream?.stop();
  showOverlay('Switching…');
  openVideo(state.epoch);
}

function connectionLost(which, code, reason = '') {
  if (!state.active || state.stopped) return;
  if (code === CLOSE_REVOKED) { showPair('This device was signed out or revoked. Pair it again with a new code.'); return; }
  if (code === CLOSE_UNSUPPORTED && which === 'video') {
    // Game mode failed for this monitor: fall back to Desktop mode and say why. The choice
    // stays available (and remembered) so it can be tried again, e.g. on another monitor.
    setStreamMode('desktop', { remember: false });
    state.fallbackNotice = `Game mode is not available: ${reason || 'no hardware video encoder'}. Using Desktop mode.`;
    showError(state.fallbackNotice);
    return;
  }
  if (code === CLOSE_REPLACED) {
    if (which === 'control') {
      state.control = null;
      setNetwork('Input is being used in another tab');
      showError('Mouse and keyboard moved to another tab or window. Tap Reconnect to take them back.');
      return;
    }
    stopSession('This screen was opened in another tab or window. Tap Reconnect to take it back.');
    return;
  }
  scheduleReconnect(code === CLOSE_BUSY ? 'Too many devices are connected to the PC. Retrying…' : 'Connection lost. Reconnecting…');
}

function stopSession(message) {
  closeConnections();
  state.stopped = true;
  showOverlay(message);
  setNetwork('Disconnected');
}

function scheduleReconnect(message) {
  if (state.retryTimer) return;
  closeConnections();
  showOverlay(message);
  setNetwork('Reconnecting…');
  const delay = Math.min(15000, 1000 * 2 ** Math.min(state.retryCount++, 4));
  state.retryTimer = setTimeout(reconnectNow, delay);
}

async function reconnectNow() {
  clearTimeout(state.retryTimer);
  state.retryTimer = 0;
  if (!state.active) return;
  try {
    await refreshIdentity();
    if (state.displays.length === 0) await getDisplays(); // the PC was not answering when the stream was opened
  } catch (error) {
    if (error.status === 401) { showPair(UNPAIRED); return; }
    scheduleReconnect('The PC is not answering. Retrying…');
    return;
  }
  showError('');
  connect();
}

function controlMessage(text) {
  let message;
  try { message = JSON.parse(text); } catch { return; }
  if (message.type === 'notice') showError(message.message || '');
}

// ---------------------------------------------------------------- drawing

function drawFrame(image, info) {
  if (desktop.width !== info.width || desktop.height !== info.height) {
    desktop.width = info.width;
    desktop.height = info.height;
    state.frameWidth = info.width;
    state.frameHeight = info.height;
    updateScreenLayout();
  }
  canvasContext.drawImage(image, 0, 0);
  hideOverlay();
  if (state.mode !== 'game') { // Game mode gets its numbers from the host (gameStats)
    const stats = state.stats;
    stats.frames++;
    stats.bytes += info.bytes || 0;
    stats.idle = false;
    updateStats();
  }
  if (state.control?.readyState === WebSocket.OPEN) state.retryCount = 0;
}

function streamStatus(status) {
  if (status.type === 'idle') {
    state.stats.idle = true;
    hideOverlay();
    updateStats();
  } else if (status.type === 'unavailable') {
    showOverlay(status.message || 'The desktop is not available right now.');
  } else if (status.type === 'resumed') {
    hideOverlay();
  } else if (status.type === 'error') {
    showError(status.message || 'Video error.');
  }
}

// Game mode numbers come from the host: frames it sent, encode time, and how long a frame
// takes from being sent until this browser has decoded it and its ack is back.
function gameStats(stats) {
  const size = `${state.frameWidth} × ${state.frameHeight}`;
  const text = stats.fps === 0
    ? `Screen unchanged · ${size}`
    : `${Math.round(stats.fps)} fps · ${stats.mbps} Mbps · encode ${stats.encodeMs} ms · network+decode ${stats.deliverMs} ms · ${size}`;
  $('streamStats').textContent = text;
  $('quickStatus').textContent = text;
}

// ---------------------------------------------------------------- Game-mode mouse pointer

function setCursorImage(image) {
  state.cursor.hotX = image.hotX;
  state.cursor.hotY = image.hotY;
  state.cursor.width = image.width;
  state.cursor.height = image.height;
  $('remoteCursor').src = `data:image/png;base64,${image.png}`;
  placeCursor();
}
function moveCursor(position) {
  state.cursor.x = position.x;
  state.cursor.y = position.y;
  state.cursor.visible = position.visible;
  placeCursor();
}
function hideCursor() { $('remoteCursor').classList.add('hidden'); }
// Desktop duplication reports the pointer image's top-left corner in monitor pixels. The
// image is drawn at least half size so it stays visible when a 4K desktop fits on a phone.
function placeCursor() {
  const image = $('remoteCursor'), c = state.cursor;
  if (state.mode !== 'game' || !c.visible || !c.width || !state.desktopWidth || !image.getAttribute('src')) { hideCursor(); return; }
  const scaleX = desktop.clientWidth / state.desktopWidth, scaleY = desktop.clientHeight / state.desktopHeight;
  const size = Math.max(scaleX, 0.5);
  image.style.width = `${c.width * size}px`;
  image.style.height = `${c.height * size}px`;
  image.style.left = `${desktop.offsetLeft + (c.x + c.hotX) * scaleX - c.hotX * size}px`;
  image.style.top = `${desktop.offsetTop + (c.y + c.hotY) * scaleY - c.hotY * size}px`;
  image.classList.remove('hidden');
}

function updateStats(force = false) {
  const stats = state.stats;
  const now = performance.now();
  const elapsed = now - stats.since;
  if (elapsed < 1000 && !force) return;
  const fps = Math.round(stats.frames * 1000 / Math.max(1, elapsed));
  const mbps = (stats.bytes * 8 / 1000 / Math.max(1, elapsed)).toFixed(1);
  const size = `${state.frameWidth} × ${state.frameHeight}`;
  const text = fps === 0 && stats.idle ? `Screen unchanged · ${size}` : `${fps} fps · ${mbps} Mbps · ${size}`;
  $('streamStats').textContent = text;
  $('quickStatus').textContent = text;
  state.stats = { frames: 0, bytes: 0, since: now, idle: stats.idle };
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
    const ratio = viewMode === 'fill' ? Math.max(viewportW / iw, viewportH / ih) : Math.min(viewportW / iw, viewportH / ih);
    drawW = iw * ratio;
    drawH = ih * ratio;
  }
  drawW = Math.max(1, Math.round(drawW * state.zoom / 100));
  drawH = Math.max(1, Math.round(drawH * state.zoom / 100));
  const surfaceW = Math.max(viewportW, drawW), surfaceH = Math.max(viewportH, drawH);
  const surface = $('screenSurface');
  surface.style.width = `${surfaceW}px`;
  surface.style.height = `${surfaceH}px`;
  desktop.style.width = `${drawW}px`;
  desktop.style.height = `${drawH}px`;
  desktop.style.left = `${Math.floor((surfaceW - drawW) / 2)}px`;
  desktop.style.top = `${Math.floor((surfaceH - drawH) / 2)}px`;
  // Scrollbars only when zoomed in; at 100% they just steal space from the desktop.
  stage.classList.toggle('zoomed', state.zoom > 100 || viewMode === 'fill');
  const scroller = $('screenScroller');
  scroller.scrollLeft = Math.max(0, (surfaceW - viewportW) / 2);
  scroller.scrollTop = Math.max(0, (surfaceH - viewportH) / 2);
  placeCursor();
}

let layoutDebounce, resolutionDebounce;
function scheduleResolutionRefresh() {
  clearTimeout(resolutionDebounce);
  resolutionDebounce = setTimeout(() => {
    if (!state.active || state.keyboardOpen || state.stopped) return;
    if (Math.abs(requestedWidth() - state.lastRequestedWidth) > 240) restartVideo();
  }, 480);
}
// Re-fit the picture whenever the stage changes size for any reason: rotation, window
// resizing, full screen, the settings panel opening, or the phone keyboard appearing.
new ResizeObserver(() => {
  clearTimeout(layoutDebounce);
  layoutDebounce = setTimeout(() => {
    if (state.immersive && !state.fitUserSet) { $('fitPick').value = 'fit'; syncQuickControls(); }
    updateScreenLayout();
    scheduleResolutionRefresh();
  }, 80);
}).observe($('desktopStage'));

// ---------------------------------------------------------------- input

function send(kind, data = {}) {
  // Watch only: nothing goes to the PC except "release", which lets go of what was held.
  if (state.watchOnly && kind !== 'release') return false;
  const control = state.control;
  if (!control || control.readyState !== WebSocket.OPEN) return false;
  control.send(JSON.stringify({ kind, ...data }));
  return true;
}
function key(code, action) {
  if (action === 'down') state.pressedKeys.add(code); else state.pressedKeys.delete(code);
  send('key', { code, action });
}
function tapKey(code) { key(code, 'down'); key(code, 'up'); }
function combo(code) { key('ControlLeft', 'down'); tapKey(code); key('ControlLeft', 'up'); }
function mouseClick(button = 0) { send('mouse', { button, action: 'down' }); send('mouse', { button, action: 'up' }); }

// Called when this page loses focus or hides: the host releases every key and button this
// connection is holding, so nothing stays stuck down on the PC.
function releaseAllInput(notifyHost = true) {
  state.pressedKeys.clear();
  resetTouchGesture();
  if (notifyHost) send('release');
}

// Sends text in pieces small enough for one control message, never splitting a character.
function sendTextChunks(text) {
  const characters = Array.from(text);
  for (let i = 0; i < characters.length; i += 400) {
    if (!send('text', { text: characters.slice(i, i + 400).join('') })) return false;
  }
  return true;
}

function absolutePointer(event) {
  const rect = desktop.getBoundingClientRect();
  const x = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
  const y = Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height));
  send('pointer', { x, y, display: state.display });
}

// Touch gestures: one-finger tap = left click; two-finger tap or a still long press = right
// click. Hold briefly THEN move to drag; moving right away just moves the pointer.
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
// Game mode on a computer: "Lock mouse" hides the local pointer and sends raw movement, which
// is what first-person games expect. The browser always releases the lock on Esc.
const mouseLocked = () => document.pointerLockElement === desktop;
let lockedMoveQueued = false;
function queueLockedMove(event) {
  const parts = event.getCoalescedEvents?.();
  for (const e of parts?.length ? parts : [event]) { state.lockedDx += e.movementX; state.lockedDy += e.movementY; }
  if (lockedMoveQueued) return;
  lockedMoveQueued = true;
  requestAnimationFrame(() => {
    lockedMoveQueued = false;
    const dx = Math.round(state.lockedDx), dy = Math.round(state.lockedDy);
    state.lockedDx -= dx; state.lockedDy -= dy;
    if (dx || dy) send('move', { dx, dy });
  });
}
$('lockMouse').addEventListener('click', () => {
  if (state.watchOnly) { showError(WATCHING); return; }
  const request = desktop.requestPointerLock?.();
  if (request?.catch) request.catch(() => showError('This browser did not allow locking the mouse.'));
  else if (!desktop.requestPointerLock) showError('This browser cannot lock the mouse.');
});
document.addEventListener('pointerlockchange', () => {
  const locked = mouseLocked();
  document.body.classList.toggle('pointer-locked', locked);
  showError(locked ? 'Mouse locked to the PC. Press Esc to release it.' : '');
  if (!locked) releaseAllInput();
});

desktop.addEventListener('pointerdown', (event) => {
  if (!state.active) return;
  event.preventDefault();
  desktop.focus({ preventScroll: true }); // keyboard input goes to the PC while the desktop has focus
  if (mouseLocked()) {
    dragging = true;
    dragButton = event.button >= 0 && event.button <= 4 ? event.button : 0;
    send('mouse', { button: dragButton, action: 'down' });
    return;
  }
  try { desktop.setPointerCapture(event.pointerId); } catch { /* not capturable */ }
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
  if (gesturePanning) return; // first finger lifted during a multi-touch gesture
  dragging = true; travel = 0; touchedAt = Date.now(); rightHeld = false;
  state.lastX = event.clientX; state.lastY = event.clientY;
  if ($('modePick').value === 'direct') absolutePointer(event);
  if (event.pointerType === 'touch') {
    // Only arm dragging after a deliberate pause. No mouse-down is sent until the finger
    // moves, which leaves a still long-press free for right-click.
    holdTimer = setTimeout(() => {
      if (dragging && !gesturePanning && touches.size === 1 && travel < 12) dragReady = true;
    }, 330);
    rightHoldTimer = setTimeout(() => {
      if (dragging && !gesturePanning && touches.size === 1 && travel < 12 && dragButton === null) {
        rightHeld = true; dragReady = false; mouseClick(2);
      }
    }, 660);
  } else {
    dragButton = event.button >= 0 && event.button <= 4 ? event.button : 0; // 3/4 = back/forward
    send('mouse', { button: dragButton, action: 'down' });
  }
});
desktop.addEventListener('pointermove', (event) => {
  if (!state.active) return;
  if (mouseLocked()) { queueLockedMove(event); return; }
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
      if (startDrag) { dragButton = 0; send('mouse', { button: 0, action: 'down' }); }
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
      // A two-finger tap is not a scroll: fire it once, after both fingers lift, with little movement.
      if (!twoFingerMoved && twoFingerStart && Date.now() - twoFingerStart < 420) {
        if ($('modePick').value === 'direct' && twoFingerCenter) absolutePointer({ clientX: twoFingerCenter.x, clientY: twoFingerCenter.y });
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
  if (event.pointerType === 'touch' && !wasDown && !wasRightHeld && travel < 15 && Date.now() - touchedAt < 950) mouseClick();
}
desktop.addEventListener('pointerup', pointerEnd);
desktop.addEventListener('pointercancel', (event) => {
  // Cancellation (e.g. an iOS system gesture) must never produce a click.
  if (event.pointerType === 'touch') touches.delete(event.pointerId);
  twoFingerMoved = true;
  if (touches.size === 0) resetTouchGesture();
  else { releaseDrag(); dragging = false; }
});
// Suppress iOS long-press selection and the browser's own context menu.
desktop.addEventListener('contextmenu', (event) => event.preventDefault());

// Wheel: accumulate fine-grained trackpad deltas instead of sending a full notch per event.
// Browsers report about 100 px per mouse-wheel notch; Windows uses 120 units per notch.
let wheelX = 0, wheelY = 0, wheelTimer = 0;
desktop.addEventListener('wheel', (event) => {
  if (!state.active) return;
  event.preventDefault();
  const scale = (event.deltaMode === 1 ? 40 : event.deltaMode === 2 ? 800 : 1) * 1.2;
  wheelY -= event.deltaY * scale;
  wheelX += event.deltaX * scale;
  if (!wheelTimer) wheelTimer = setTimeout(flushWheel, 16);
}, { passive: false });
function flushWheel() {
  wheelTimer = 0;
  const y = Math.trunc(wheelY), x = Math.trunc(wheelX);
  if (y) send('wheel', { delta: Math.max(-1200, Math.min(1200, y)) });
  if (x) send('hwheel', { delta: Math.max(-1200, Math.min(1200, x)) });
  wheelY -= y; wheelX -= x;
}

// Physical keyboard: forwarded while the desktop (or the page itself) has focus. The host
// maps KeyboardEvent.code to scan codes, so the PC's own keyboard layout applies.
const typingTarget = (target) => ['INPUT', 'TEXTAREA', 'SELECT', 'BUTTON'].includes(target?.tagName) || target?.isContentEditable;
document.addEventListener('keydown', (event) => {
  if (!state.active || !state.control || typingTarget(event.target)) return;
  if (event.code === 'F5' || ((event.ctrlKey || event.metaKey) && event.code === 'KeyR')) return; // let the browser reload
  if (event.code === 'Escape' && state.immersive) setImmersive(false);
  if (state.watchOnly) return; // the keys stay with this browser
  event.preventDefault();
  key(event.code, 'down');
});
document.addEventListener('keyup', (event) => {
  if (!state.active || !state.pressedKeys.has(event.code)) return;
  event.preventDefault();
  key(event.code, 'up');
});
window.addEventListener('blur', () => { if (state.active) releaseAllInput(); });

// ---------------------------------------------------------------- page lifecycle

document.addEventListener('visibilitychange', () => {
  if (!state.active) return;
  if (document.hidden) { releaseAllInput(); return; }
  // Phones suspend background tabs; reconnect at once when the user comes back.
  if (!state.stopped && (!state.control || state.control.readyState !== WebSocket.OPEN)) {
    state.retryCount = 0;
    reconnectNow();
  }
});
window.addEventListener('pagehide', () => closeConnections());
// iOS Safari can restore a page from its back-forward cache without reloading it.
window.addEventListener('pageshow', (event) => { if (event.persisted && state.active) reconnectNow(); });

// ---------------------------------------------------------------- controls

function selectDisplay(display) {
  state.display = display.id;
  state.displayName = display.name;
  showSessionName();
  saveSettings();
  restartVideo();
}

// The monitor switcher: the same cards as on the home screen, in a sheet over the stream.
const sheetMonitors = createMonitorPicker($('sheetMonitorList'), {
  status: $('sheetMonitorStatus'),
  current: () => state.display,
  onList: (displays) => { state.displays = displays; showSessionName(); },
  onPick: (display) => { setMonitorSheet(false); selectDisplay(display); },
  unpaired: (error) => { if (error.status === 401) showPair(UNPAIRED); return error.status === 401; },
});
function setMonitorSheet(open) {
  state.monitorsOpen = open;
  $('monitorSheet').classList.toggle('hidden', !open);
  $('openMonitors').setAttribute('aria-expanded', String(open));
  if (!open) { sheetMonitors.stop(); return; }
  setQuickMenu(false);
  if (state.keyboardOpen) setKeyboard(false); // one sheet at a time: they share a place on screen
  sheetMonitors.start();
}
$('openMonitors').addEventListener('click', () => setMonitorSheet(!state.monitorsOpen));
$('quickMonitors').addEventListener('click', () => setMonitorSheet(true));
$('closeMonitors').addEventListener('click', () => setMonitorSheet(false));

// Screenshot: the PC captures the monitor at full size and this device saves the picture.
async function saveScreenshot() {
  setQuickMenu(false);
  showError('Taking a screenshot…');
  try {
    const response = await fetchFile(`/api/v1/displays/${state.display}/screenshot`);
    const name = fileNameFromDisposition(response.headers.get('Content-Disposition'), 'Abyss Remote screenshot.jpg');
    await saveBlob(await response.blob(), name);
    showError(`Screenshot taken: ${name}`);
  } catch (error) {
    if (error.status === 401) showPair(UNPAIRED);
    else showError(error.status ? error.message : 'The screenshot could not be fetched.');
  }
}
$('screenshot').addEventListener('click', saveScreenshot);
$('quickScreenshot').addEventListener('click', saveScreenshot);
$('fpsPick').addEventListener('change', () => { saveSettings(); restartVideo(); });
$('streamMode').addEventListener('change', () => setStreamMode($('streamMode').value));
$('quickStreamMode').addEventListener('change', () => setStreamMode($('quickStreamMode').value));
for (const id of ['gameHeightPick', 'gameFpsPick', 'bitratePick'])
  $(id).addEventListener('change', () => { $('quickGameHeight').value = $('gameHeightPick').value; saveSettings(); restartVideo(); });
$('quickGameHeight').addEventListener('change', () => {
  $('gameHeightPick').value = $('quickGameHeight').value;
  saveSettings();
  restartVideo();
});
$('refresh').addEventListener('click', async () => {
  showError('');
  try { await refreshIdentity(); await getDisplays(); state.retryCount = 0; connect(); }
  catch (error) {
    if (error.status === 401) showPair(UNPAIRED);
    else showError(error.message);
  }
});
$('goHome').addEventListener('click', showHome);
$('quickHome').addEventListener('click', showHome);
$('zoom').addEventListener('input', () => {
  state.zoom = Number($('zoom').value);
  $('zoomValue').textContent = `${state.zoom}%`;
  updateScreenLayout();
  scheduleResolutionRefresh();
});
$('fitPick').addEventListener('change', () => { state.fitUserSet = true; syncQuickControls(); updateScreenLayout(); saveSettings(); });
$('modePick').addEventListener('change', () => { syncQuickControls(); saveSettings(); });
$('settingsToggle').addEventListener('click', () => {
  const hidden = $('settingsPanel').classList.toggle('hidden');
  $('settingsToggle').setAttribute('aria-expanded', String(!hidden));
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
  setBrowserFullscreen(enabled);
  requestAnimationFrame(() => { updateScreenLayout(); scheduleResolutionRefresh(); });
}

// Real full screen where the browser allows it (iPad, computers). iPhone Safari only allows it
// for videos, so there the CSS layout above (or the Home Screen app) is what users get. In
// Chromium browsers, Keyboard Lock then sends Esc, Alt+Tab and the Windows key to the PC
// (hold Esc to leave full screen).
function setBrowserFullscreen(enabled) {
  const root = document.documentElement;
  const current = document.fullscreenElement || document.webkitFullscreenElement;
  try {
    if (enabled && !current) {
      const request = root.requestFullscreen?.({ navigationUI: 'hide' }) ?? root.webkitRequestFullscreen?.();
      Promise.resolve(request).then(() => navigator.keyboard?.lock?.()).catch(() => { /* not allowed here: CSS full screen still applies */ });
    } else if (!enabled && current) {
      navigator.keyboard?.unlock?.();
      (document.exitFullscreen || document.webkitExitFullscreen)?.call(document);
    }
  } catch { /* unsupported */ }
}
for (const event of ['fullscreenchange', 'webkitfullscreenchange']) {
  document.addEventListener(event, () => {
    // Leaving full screen with the browser's own controls also leaves the immersive layout.
    if (!(document.fullscreenElement || document.webkitFullscreenElement) && state.immersive && !window.matchMedia('(pointer: coarse)').matches) setImmersive(false);
  });
}
function syncQuickControls() {
  $('quickFit').value = $('fitPick').value;
  $('quickMode').value = $('modePick').value;
}
function setQuickMenu(open) {
  state.menuOpen = !!open;
  $('quickPanel').classList.toggle('hidden', !open);
  $('quickToggle').setAttribute('aria-expanded', String(open));
  $('quickToggle').setAttribute('aria-label', open ? 'Hide controls' : 'Open controls');
}
$('immersive').addEventListener('click', () => setImmersive(!state.immersive));
$('quickToggle').addEventListener('click', () => setQuickMenu(!state.menuOpen));
$('quickClose').addEventListener('click', () => setQuickMenu(false));
$('quickKeyboard').addEventListener('click', () => { setQuickMenu(false); setKeyboard(true); });
// A button that sends input says why nothing happens while Watch only is on.
const whenControlling = (action) => () => { if (state.watchOnly) showError(WATCHING); else action(); };
$('quickRight').addEventListener('click', whenControlling(() => mouseClick(2)));
$('quickScrollUp').addEventListener('click', whenControlling(() => send('wheel', { delta: 360 })));
$('quickScrollDown').addEventListener('click', whenControlling(() => send('wheel', { delta: -360 })));
$('quickFit').addEventListener('change', () => {
  $('fitPick').value = $('quickFit').value;
  state.fitUserSet = true;
  updateScreenLayout();
  saveSettings();
});
$('quickMode').addEventListener('change', () => { $('modePick').value = $('quickMode').value; saveSettings(); });
$('quickReconnect').addEventListener('click', () => { setQuickMenu(false); $('refresh').click(); });
$('quickExit').addEventListener('click', () => setImmersive(false));
// Touching the desktop closes the panel so swipes are not mistaken for panel taps.
$('desktopStage').addEventListener('pointerdown', () => { if (state.menuOpen) setQuickMenu(false); });

$('mouseLeft').addEventListener('click', whenControlling(() => mouseClick(0)));
$('mouseRight').addEventListener('click', whenControlling(() => mouseClick(2)));
$('scrollUp').addEventListener('click', whenControlling(() => send('wheel', { delta: 360 })));
$('scrollDown').addEventListener('click', whenControlling(() => send('wheel', { delta: -360 })));
for (const [id, code] of [['escapeKey', 'Escape'], ['tabKey', 'Tab'], ['enterKey', 'Enter'], ['backspaceKey', 'Backspace']])
  $(id).addEventListener('click', whenControlling(() => tapKey(code)));
$('ctrlC').addEventListener('click', whenControlling(() => combo('KeyC')));
$('ctrlV').addEventListener('click', whenControlling(() => combo('KeyV')));

// Text: an explicit Send (rather than a keystroke per phone key) works with autocorrect,
// paste and any Unicode text, and never sends half-finished compositions.
function sendText() {
  const text = $('textSend').value;
  if (!text) return;
  if (state.watchOnly) { showError(WATCHING); return; }
  if (sendTextChunks(text)) $('textSend').value = '';
  else showError('Input is disconnected. Your text is kept; reconnect and try again.');
}
$('sendText').addEventListener('click', sendText);
$('textSend').addEventListener('keydown', (event) => { if (event.key === 'Enter') { event.preventDefault(); sendText(); } });

function syncKeyboardViewport() {
  // The visual viewport shrinks when the phone keyboard opens; lift the sheet above it.
  const vv = window.visualViewport;
  const bottom = vv ? Math.max(0, window.innerHeight - vv.height - vv.offsetTop) : 0;
  document.documentElement.style.setProperty('--keyboard-offset', `${Math.round(bottom)}px`);
}
window.visualViewport?.addEventListener('resize', syncKeyboardViewport);
window.visualViewport?.addEventListener('scroll', syncKeyboardViewport);
function setKeyboard(open) {
  state.keyboardOpen = open;
  $('keyboardSheet').classList.toggle('hidden', !open);
  $('showKeyboard').setAttribute('aria-expanded', String(open));
  if (open) {
    if (state.monitorsOpen) setMonitorSheet(false);
    $('keyboardStatus').textContent = '';
    $('keyboardText').focus({ preventScroll: true }); // must happen inside the tap for iOS to show its keyboard
  } else {
    $('keyboardText').blur();
    document.documentElement.style.setProperty('--keyboard-offset', '0px');
  }
  syncKeyboardViewport();
}
$('showKeyboard').addEventListener('click', () => setKeyboard(!state.keyboardOpen));
$('closeKeyboard').addEventListener('click', () => setKeyboard(false));
$('keyboardSend').addEventListener('click', () => {
  const text = $('keyboardText').value;
  if (!text) { $('keyboardStatus').textContent = 'Type something first.'; return; }
  if (state.watchOnly) { $('keyboardStatus').textContent = WATCHING; return; }
  if (!sendTextChunks(text)) { $('keyboardStatus').textContent = 'Disconnected. Your text is kept; reconnect first.'; return; }
  $('keyboardText').value = '';
  $('keyboardStatus').textContent = 'Sent to the active window on your PC.';
});
for (const [id, code] of [['keyboardTab', 'Tab'], ['keyboardEnter', 'Enter'], ['keyboardBackspace', 'Backspace']]) {
  $(id).addEventListener('click', () => {
    if (state.watchOnly) { $('keyboardStatus').textContent = WATCHING; return; }
    if (state.control?.readyState !== WebSocket.OPEN) { $('keyboardStatus').textContent = 'Input is disconnected.'; return; }
    tapKey(code);
    $('keyboardText').focus({ preventScroll: true });
  });
}

// Clipboard: text only, up to 4,096 characters, and only when you ask for it.
async function readPcClipboard() { return (await api('/api/v1/clipboard')).text || ''; }
async function writePcClipboard(text) { await api('/api/v1/clipboard', { method: 'POST', body: JSON.stringify({ text }) }); }
$('copyPc').addEventListener('click', async () => {
  try {
    $('textSend').value = await readPcClipboard();
    $('textSend').focus(); $('textSend').select();
    showError('PC clipboard loaded into the text box.');
  } catch (error) { showError(error.message); }
});
$('pushClipboard').addEventListener('click', async () => {
  const text = $('textSend').value;
  if (!text) { showError('Type or paste something into the text box first.'); return; }
  try { await writePcClipboard(text); showError('Copied to the PC clipboard. Press Ctrl+V on the PC to paste.'); }
  catch (error) { showError(error.message); }
});
$('keyboardClipGet').addEventListener('click', async () => {
  try { $('keyboardText').value = await readPcClipboard(); $('keyboardStatus').textContent = 'PC clipboard loaded. Copy it from the box.'; }
  catch (error) { $('keyboardStatus').textContent = error.message; }
});
$('keyboardClipSet').addEventListener('click', async () => {
  const text = $('keyboardText').value;
  if (!text) { $('keyboardStatus').textContent = 'Type or paste something first.'; return; }
  try { await writePcClipboard(text); $('keyboardStatus').textContent = 'Copied to the PC clipboard.'; }
  catch (error) { $('keyboardStatus').textContent = error.message; }
});

// Controllers work in both modes; the host answers with a notice if its driver is missing.
const gamepads = watchGamepads({
  send: (kind, data) => state.active && send(kind, data),
  onChange: (names) => {
    if (names.length) $('quickStatus').textContent = `${names.length} controller${names.length > 1 ? 's' : ''} connected`;
  },
});

applySettings(loadSettings());
buildToggles();
syncToggles();
initPairing({ onPaired: initialize });
initHome({
  openStream,
  prepareSound,
  gameMode: () => state.gameAvailable,
  signedOut: showPair,
});
initialize();
