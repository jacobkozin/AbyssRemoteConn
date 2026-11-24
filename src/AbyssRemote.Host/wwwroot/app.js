// Abyss Remote browser client: the stream view (video, sound, mouse/touch/keyboard input) and
// the switching between the three screens: pairing (pairing.js), home (home.js) and stream.
// Video comes from one of two stream modules with the same callbacks: jpeg-stream.js (Desktop
// mode) or video-stream.js (Game mode, hardware H.264 + WebCodecs). Input goes over one
// authenticated WebSocket as small JSON messages (see docs/API.md). No third-party code.
import { PointerMotion, movementQueue, cursorPictureReady } from './pointer-motion.js';
import { startJpegStream } from './jpeg-stream.js';
import { startVideoStream, gameModeSupported } from './video-stream.js';
import { unlockAudio, audioUnlocked, startAudio } from './audio.js';
import { watchGamepads } from './gamepad.js';
import { controllerRumble } from './rumble.js';
import { installTouchPad, pinchZoom, shortcutSteps } from './touch-controls.js';
import { clampZoom, followScroll, fractionAt, guessMonitorScale, scrollFor, trackpadGain, twoFingerGesture, wheelUnits, zoomRange } from './view-zoom.js';
import { TYPING_GUARD, keyCodeFor, typedText, typingChange } from './live-typing.js';
import { api, fetchFile, refreshIdentity, session } from './api.js';
import { $, el, icon, saveBlob } from './dom.js';
import { fileNameFromDisposition } from './format.js';
import { initPairing, clearPairCode } from './pairing.js';
import { initAwayView, redeemAwayCode, takeAwayCodeFromAddress } from './away.js';
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
// `zoom` is a percentage of the size at which the picture fits the stage. `viewX`/`viewY`
// say which point of the picture (0 to 1) is in the middle of the stage, so the place being
// looked at survives rotation and the phone keyboard opening. `pointed` is where this device
// last pointed in Direct touch, as the same kind of fractions.
const state = {
  active: false, stopped: false, watchOnly: false, monitorsOpen: false, touchPad: false,
  mode: 'desktop', gameAvailable: false, fallbackNotice: '', soundOn: false, audio: null,
  displays: [], display: 0, displayName: null,
  stream: null, control: null, epoch: 0, retryCount: 0, retryTimer: 0,
  zoom: 100, viewX: 0.5, viewY: 0.5, pointed: null, typingZoom: true,
  frameWidth: 1280, frameHeight: 720, lastRequestedWidth: 0,
  desktopWidth: 0, desktopHeight: 0,
  separateCursor: false, cursorPoll: 0, cursorWaiting: false, cursorAskedAt: 0,
  cursor: { x: 0, y: 0, visible: false, hotX: 0, hotY: 0, width: 0, height: 0 },
  immersive: false, keyboardOpen: false, keyboardMore: false, menuOpen: false, fitUserSet: false,
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
                 gameHeight: $('gameHeightPick').value, gameFps: $('gameFpsPick').value, bitrate: $('bitratePick').value,
                 typingZoom: state.typingZoom };
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
  state.typingZoom = saved.typingZoom !== false;
}

// ---------------------------------------------------------------- views

const views = { pair: $('pairView'), away: $('awayView'), home: $('homeView'), remote: $('remoteView') };
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
  if (awayAddress) {
    // At the away address nothing can be paired: a device is set up at home with a code.
    showView('away');
    $('awayError').textContent = /pair/i.test(error) ? AWAY_LOST : error;
    setNetwork('Not set up');
    return;
  }
  showView('pair');
  $('pairError').textContent = error;
  setNetwork('Not paired');
}

// True at the away address (the public one the Gaming Hub forwards to the PC), false at home,
// null until the PC has said. /api/v1/info answers without a session.
let awayAddress = null;
const AWAY_LOST = 'This device is not set up for away access any more. Set it up again at home.';
async function learnAddress() {
  if (awayAddress !== null) return;
  try { awayAddress = Boolean((await api('/api/v1/info')).away); } catch { /* not reachable: initialize says so */ }
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
  resetView(); // a new session starts with the whole screen
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
  await learnAddress();
  // A link from the home screen carries a one-time away code; it turns into this browser's away key.
  const awayCode = awayAddress ? takeAwayCodeFromAddress() : '';
  if (awayCode) {
    try { await redeemAwayCode(awayCode); }
    catch (error) { showPair(''); $('awayError').textContent = error.message; return; }
  }
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
  clearInterval(state.cursorPoll); state.cursorPoll = 0; state.cursorWaiting = false;
  state.separateCursor = false; pointerMotion.reset(); hideCursor();
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
  { id: 'touchpad', icon: 'keyboard', label: 'Touch gamepad', hint: 'On-screen controller; needs ViGEmBus on the PC',
    isOn: () => state.touchPad, flip: () => {
      releaseAllInput(); state.touchPad = !state.touchPad;
      $('touchPad').classList.toggle('hidden', !state.touchPad);
      gamepads.resync(); syncToggles(); setQuickMenu(false);
    } },
  { id: 'watch', icon: 'eye', label: 'Watch only', hint: 'Look without sending mouse, keyboard or controller input',
    isOn: () => state.watchOnly, flip: () => setWatchOnly(!state.watchOnly) },
  { id: 'sound', icon: 'sound', label: 'Sound', hint: "Play the PC's sound on this device",
    isOn: () => state.soundOn, flip: () => toggleSound() },
  { id: 'typingZoom', icon: 'zoom', label: 'Zoom to type', hint: 'Zoom in on the pointer while the phone keyboard is open',
    isOn: () => state.typingZoom, flip: () => { state.typingZoom = !state.typingZoom; saveSettings(); syncToggles(); } },
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
    pollCursor();
    state.cursorPoll = setInterval(pollCursor, 16);
  };
  control.onmessage = (event) => { if (epoch === state.epoch) controlMessage(event.data); };
  control.onclose = (event) => { if (epoch === state.epoch) connectionLost('control', event.code); };
}

function requestedWidth() {
  // Enough pixels for sharp text on high-DPI phones at the current zoom, never more than the
  // monitor has. `shown` is how wide the whole picture is drawn, in CSS pixels.
  const dpr = Math.min(window.devicePixelRatio || 1, 3);
  const displayWidth = state.displays.find(d => d.id === state.display)?.width || 2560;
  const shown = Math.max($('desktopStage').clientWidth, layout.drawW);
  const target = Math.max(1920, Math.round(shown * dpr * 1.35));
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
    const query = `display=${state.display}&fps=${$('gameFpsPick').value}&height=${$('gameHeightPick').value}&bitrate=${$('bitratePick').value === 'auto' ? 20000 : $('bitratePick').value}`;
    state.stream = startVideoStream({
      ...common,
      url: wsUrl(`/api/v1/video?${query}`),
      automaticBitrate: $('bitratePick').value === 'auto',
      onConfig: (config) => {
        if (epoch !== state.epoch) return;
        state.desktopWidth = config.desktopWidth;
        state.desktopHeight = config.desktopHeight;
        if ($('remoteError').textContent === state.fallbackNotice) showError(''); // Game mode works again
      },
      onCursor: (cursor) => { if (epoch === state.epoch && !state.separateCursor) moveCursor(cursor); },
      onCursorImage: (image) => { if (epoch === state.epoch && !state.separateCursor) setCursorImage(image); },
      onStats: (stats) => { if (epoch === state.epoch) gameStats(stats); },
    });
    return;
  }
  const width = requestedWidth();
  state.lastRequestedWidth = width;
  state.stream = startJpegStream({
    ...common,
    url: wsUrl(`/api/v1/stream?display=${state.display}&fps=${Number($('fpsPick').value)}&quality=85&width=${width}${state.separateCursor ? '&cursor=separate' : ''}`),
  });
}

// Display, frame rate and resolution changes only restart the video; input stays connected.
function restartVideo({ quiet = false } = {}) {
  if (!state.active || state.stopped || !state.control) { connect(); return; }
  state.stream?.stop();
  // A change of sharpness alone keeps the last picture on screen until the new one arrives.
  if (!quiet) showOverlay('Switching…');
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
  if (message.type === 'cursorState') {
    state.cursorWaiting = false;
    if (!message.supported) { clearInterval(state.cursorPoll); state.cursorPoll = 0; return; }
    if (message.image) setCursorImage(message.image);
    // No picture to draw yet: leave the pointer in the video exactly as before.
    if (!state.separateCursor && !cursorPictureReady(state.cursor, $('remoteCursor').getAttribute('src'))) return;
    const first = !state.separateCursor;
    state.separateCursor = true;
    if (pointerMotion.receive(message)) placeCursor();
    // Negotiate before opting out of the baked-in Desktop-mode cursor. Older hosts
    // keep working; their original video pointer remains visible.
    if (first && state.mode !== 'game') restartVideo({ quiet: true }); // the same picture, minus its pointer
  }
  if (message.type === 'notice') showError(message.message || '');
  if (message.type === 'rumble') rumble.receive(message);
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

// ---------------------------------------------------------------- independent mouse pointer
const pointerMotion = new PointerMotion();
const pointerMoves = movementQueue((kind, data) => {
  if (send(kind, data)) { if (kind === 'pointer') pointerMotion.sent(); }
  else { pointerMotion.cancel(); hideCursor(); }
});
function pollCursor() {
  if (!state.active || state.stopped || document.hidden || state.control?.readyState !== WebSocket.OPEN) return;
  // At most one poll in flight. The host skips a request that arrives too soon after the
  // last one (Wi-Fi often delivers two together), so ask again shortly instead of waiting a second.
  if (state.cursorWaiting && performance.now() - state.cursorAskedAt < 150) return;
  if (state.control.bufferedAmount > 4096) return;
  state.cursorWaiting = true; state.cursorAskedAt = performance.now();
  state.control.send(JSON.stringify({ kind: 'cursor' })); // read-only, including Watch only
}
function canMovePointer() {
  return !state.watchOnly && state.active && !state.stopped && state.control?.readyState === WebSocket.OPEN && state.control.bufferedAmount < 4096;
}
function currentMonitor() { return state.displays.find(d => d.id === state.display); }
function queuePointer(data) {
  pointerMoves.push('pointer', data);
  placeCursor(); // touch feedback now, without a round trip or a video decode
}
function relativePointer(dx, dy) {
  if (!canMovePointer()) return;
  const monitor = currentMonitor();
  if (state.separateCursor && monitor) { queuePointer(pointerMotion.relative(dx, dy, monitor)); followPointer(); }
  else pointerMoves.push('move', { dx, dy });
}


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
  const monitor = currentMonitor();
  let x = c.x + c.hotX, y = c.y + c.hotY, visible = c.visible;
  let width = state.desktopWidth, height = state.desktopHeight;
  if (state.separateCursor && monitor) {
    x = pointerMotion.x - monitor.x; y = pointerMotion.y - monitor.y;
    width = monitor.width; height = monitor.height;
    visible = pointerMotion.visible && x >= 0 && y >= 0 && x < width && y < height;
  }
  if ((!state.separateCursor && state.mode !== 'game') || !visible || !c.width || !width || !image.getAttribute('src') || mouseLocked()) { hideCursor(); return; }
  const scaleX = desktop.clientWidth / width, scaleY = desktop.clientHeight / height;
  const size = Math.max(scaleX, 0.5);
  image.style.width = `${c.width * size}px`;
  image.style.height = `${c.height * size}px`;
  image.style.transform = `translate3d(${desktop.offsetLeft + x * scaleX - c.hotX * size}px, ${desktop.offsetTop + y * scaleY - c.hotY * size}px, 0)`;
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

// ---------------------------------------------------------------- zoom and position

// What the last layout worked out, in CSS pixels: the stage (viewW × viewH), the picture at
// 100% zoom (baseW × baseH) and as drawn (drawW × drawH), and the scrolled surface that
// holds it, with the picture's corner at left/top.
const layout = { viewW: 0, viewH: 0, baseW: 0, baseH: 0, drawW: 0, drawH: 0, surfaceW: 0, surfaceH: 0, left: 0, top: 0 };

function monitorScale() {
  const monitor = currentMonitor();
  return monitor?.scale || guessMonitorScale(monitor?.width || state.frameWidth);
}
function zoomLimits() {
  return zoomRange(layout.baseW || 1, currentMonitor()?.width || state.frameWidth, monitorScale());
}
// How large one pixel of the PC's monitor is drawn here, in CSS pixels.
function pictureScale() { return layout.drawW / Math.max(1, currentMonitor()?.width || state.frameWidth); }
// True when the picture is larger than the stage, so there is somewhere to move it.
function canPan() { return layout.surfaceW > layout.viewW + 1 || layout.surfaceH > layout.viewH + 1; }

function updateScreenLayout() {
  const stage = $('desktopStage');
  if (stage.clientWidth === 0 || stage.clientHeight === 0) return;
  const viewW = stage.clientWidth, viewH = stage.clientHeight;
  const iw = state.frameWidth, ih = state.frameHeight;
  const viewMode = $('fitPick').value;
  let baseW, baseH;
  if (viewMode === 'stretch') { baseW = viewW; baseH = viewH; }
  else {
    const ratio = viewMode === 'fill' ? Math.max(viewW / iw, viewH / ih) : Math.min(viewW / iw, viewH / ih);
    baseW = iw * ratio;
    baseH = ih * ratio;
  }
  // A zoomed-in picture keeps its size when the stage changes (rotation, the phone keyboard
  // opening or closing). Only a picture that showed the whole screen is fitted again.
  const stageChanged = viewW !== layout.viewW || viewH !== layout.viewH;
  if (state.zoom > 100 && stageChanged && layout.baseW > 0) state.zoom *= layout.baseW / baseW;
  Object.assign(layout, { viewW, viewH, baseW, baseH });
  state.zoom = clampZoom(state.zoom, zoomLimits());
  sizePicture();
  scrollToView();
  syncZoomControls();
}

// Sizes the picture for the current zoom, in the middle of the stage when it is smaller.
function sizePicture() {
  const drawW = Math.max(1, Math.round(layout.baseW * state.zoom / 100)), drawH = Math.max(1, Math.round(layout.baseH * state.zoom / 100));
  const surfaceW = Math.max(layout.viewW, drawW), surfaceH = Math.max(layout.viewH, drawH);
  const left = Math.floor((surfaceW - drawW) / 2), top = Math.floor((surfaceH - drawH) / 2);
  Object.assign(layout, { drawW, drawH, surfaceW, surfaceH, left, top });
  const surface = $('screenSurface');
  surface.style.width = `${surfaceW}px`;
  surface.style.height = `${surfaceH}px`;
  desktop.style.width = `${drawW}px`;
  desktop.style.height = `${drawH}px`;
  desktop.style.left = `${left}px`;
  desktop.style.top = `${top}px`;
  // Scrollbars only when there is something to scroll; otherwise they just steal space.
  $('desktopStage').classList.toggle('zoomed', canPan());
  placeCursor();
}

// Scrolls so that the remembered point of the picture is in the middle of the stage.
function scrollToView() {
  const scroller = $('screenScroller');
  scroller.scrollLeft = scrollFor(state.viewX, layout.viewW / 2, layout.left, layout.drawW, layout.surfaceW, layout.viewW);
  scroller.scrollTop = scrollFor(state.viewY, layout.viewH / 2, layout.top, layout.drawH, layout.surfaceH, layout.viewH);
}
// Remembers which point is in the middle after the view was moved.
function rememberView() {
  const scroller = $('screenScroller');
  state.viewX = fractionAt(scroller.scrollLeft, layout.viewW / 2, layout.left, layout.drawW);
  state.viewY = fractionAt(scroller.scrollTop, layout.viewH / 2, layout.top, layout.drawH);
}
$('screenScroller').addEventListener('scroll', rememberView, { passive: true });
function resetView() {
  state.zoom = 100; state.viewX = 0.5; state.viewY = 0.5; state.pointed = null; viewBeforeTyping = null;
}

// Changes the zoom. The picture point under `anchor` (a place on this device's screen) stays
// under it; without an anchor the middle of the stage stays where it is. A pinch also passes
// the picture point its fingers started on (pictureX/pictureY), so the picture travels with
// the fingers.
function setZoom(zoom, anchor) {
  const scroller = $('screenScroller'), box = scroller.getBoundingClientRect();
  const offsetX = anchor ? anchor.x - box.left : layout.viewW / 2, offsetY = anchor ? anchor.y - box.top : layout.viewH / 2;
  const pictureX = anchor?.pictureX ?? fractionAt(scroller.scrollLeft, offsetX, layout.left, layout.drawW);
  const pictureY = anchor?.pictureY ?? fractionAt(scroller.scrollTop, offsetY, layout.top, layout.drawH);
  state.zoom = clampZoom(zoom, zoomLimits());
  sizePicture();
  scroller.scrollLeft = scrollFor(pictureX, offsetX, layout.left, layout.drawW, layout.surfaceW, layout.viewW);
  scroller.scrollTop = scrollFor(pictureY, offsetY, layout.top, layout.drawH, layout.surfaceH, layout.viewH);
  rememberView();
  syncZoomControls();
  scheduleResolutionRefresh();
}
// Zooms in on the PC's pointer, or on the middle of what is shown while its place is unknown.
function zoomToPointer(zoom) {
  const pointer = pointerFraction();
  if (pointer) { state.viewX = pointer.x; state.viewY = pointer.y; }
  state.zoom = clampZoom(zoom, zoomLimits());
  sizePicture();
  scrollToView();
  syncZoomControls();
  scheduleResolutionRefresh();
}
// The zoom button: one tap goes in to where text is easy to read, the next shows the whole
// screen again.
function toggleZoom() {
  zoomChosen();
  if (state.zoom > 100) setZoom(100);
  else zoomToPointer(Math.max(200, zoomLimits().readable));
}
function syncZoomControls() {
  const zoom = Math.round(state.zoom), zoomedIn = zoom > 100;
  $('zoom').max = String(zoomLimits().max);
  $('zoom').value = String(zoom);
  $('zoomValue').textContent = `${zoom}%`;
  $('quickZoom').setAttribute('aria-pressed', String(zoomedIn));
  $('quickZoom').setAttribute('aria-label', zoomedIn ? 'Show the whole screen' : 'Zoom in on the pointer');
}

// Where the PC's pointer is on the shown monitor, as fractions of its width and height.
// Until the host has reported it, the last place touched in Direct touch stands in; null
// when neither is known.
function pointerFraction() {
  const monitor = currentMonitor();
  if (state.separateCursor && monitor && pointerMotion.known) {
    const x = (pointerMotion.x - monitor.x) / monitor.width, y = (pointerMotion.y - monitor.y) / monitor.height;
    if (x >= 0 && y >= 0 && x <= 1 && y <= 1) return { x, y };
  }
  return state.pointed;
}
// Trackpad control moves a pointer the finger is not on, so a zoomed-in view goes with it.
function followPointer() {
  const pointer = canPan() ? pointerFraction() : null;
  if (!pointer) return;
  const scroller = $('screenScroller');
  scroller.scrollLeft = followScroll(scroller.scrollLeft, layout.left + pointer.x * layout.drawW, layout.viewW, layout.surfaceW, Math.min(80, layout.viewW * 0.22));
  scroller.scrollTop = followScroll(scroller.scrollTop, layout.top + pointer.y * layout.drawH, layout.viewH, layout.surfaceH, Math.min(80, layout.viewH * 0.22));
}
function panBy(dx, dy) {
  const scroller = $('screenScroller');
  scroller.scrollLeft -= dx;
  scroller.scrollTop -= dy;
}

// While the phone keyboard is open the view zooms in on the pointer, which is where the
// text box was tapped, and goes back when the keyboard closes. `viewBeforeTyping` is what to
// go back to; it is forgotten as soon as the zoom is changed by hand.
let viewBeforeTyping = null;
function zoomChosen() { viewBeforeTyping = null; }
function zoomForTyping() {
  if (!state.typingZoom || !state.immersive) return;
  const readable = zoomLimits().readable;
  if (state.zoom >= readable * 0.8) return; // already large enough to read
  viewBeforeTyping = { width: state.zoom > 100 ? layout.drawW : 0, x: state.viewX, y: state.viewY };
  zoomToPointer(readable);
}
function leaveTypingZoom() {
  const before = viewBeforeTyping;
  viewBeforeTyping = null;
  if (!before) return;
  state.viewX = before.x; state.viewY = before.y;
  state.zoom = before.width ? clampZoom(100 * before.width / layout.baseW, zoomLimits()) : 100;
  sizePicture();
  scrollToView();
  syncZoomControls();
  scheduleResolutionRefresh();
}

let layoutDebounce, resolutionDebounce;
function scheduleResolutionRefresh() {
  clearTimeout(resolutionDebounce);
  resolutionDebounce = setTimeout(() => {
    // Game mode's resolution is the one picked in its settings, not the stage's.
    if (!state.active || state.stopped || state.mode === 'game') return;
    if (touches.size > 0) { scheduleResolutionRefresh(); return; } // not in the middle of a pinch
    if (Math.abs(requestedWidth() - state.lastRequestedWidth) > 240) restartVideo({ quiet: true });
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
function mouseClick(button = 0) { pointerMoves.flush(); send('mouse', { button, action: 'down' }); send('mouse', { button, action: 'up' }); }

// Called when this page loses focus or hides: the host releases every key and button this
// connection is holding, so nothing stays stuck down on the PC.
function releaseAllInput(notifyHost = true) {
  pointerMoves.cancel(); pointerMotion.cancel(); hideCursor();
  state.pressedKeys.clear();
  touchPad.reset();
  rumble.reset();
  for (const button of document.querySelectorAll('[data-special-key]')) button.setAttribute('aria-pressed', 'false');
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
  if (!canMovePointer()) return;
  const rect = desktop.getBoundingClientRect();
  const x = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
  const y = Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height));
  state.pointed = { x, y };
  const monitor = currentMonitor();
  if (state.separateCursor && monitor) queuePointer(pointerMotion.absolute(x * monitor.width, y * monitor.height, monitor));
  else pointerMoves.push('pointer', { x, y, display: state.display });
}

// Touch gestures, anywhere on the stage. One-finger tap = left click; two-finger tap or a
// still long press = right click. Hold briefly THEN move to drag; moving right away just
// moves the pointer. Two fingers travelling together scroll; two fingers moving apart or
// together zoom, and the picture goes with them. When the picture is larger than the stage,
// Direct touch moves the picture with one finger and puts the pointer where a tap or a hold
// lands.
const pad = $('screenSurface');
let dragging = false, touchStamp = 0, travel = 0, sentAt = 0;
let dragButton = null;
let dragReady = false, rightHeld = false, gesturePanning = false;
let pinchStartDistance = 0, pinchStartZoom = 100, pinchAnchor = null;
let twoFingerKind = null, twoFingerStart = 0, twoFingerMoved = false, twoFingerCenter = null;
let scrollFrom = 0, scrollOwed = 0;
// Direct touch: `touchPoint` is where the finger came down, kept until it is clear whether
// this is a tap, a hold, a swipe or the first finger of a pinch; only then does the PC's
// pointer go anywhere. `swiping` says a swipe is moving a zoomed-in picture.
let touchPoint = null, swiping = false;
const touches = new Map();
const touchTimers = new Set();
function clearTouchTimers() {
  for (const timer of touchTimers) clearTimeout(timer);
  touchTimers.clear();
}
// Runs `action` once one finger has rested on the same spot for `delay` milliseconds. A page
// that was busy does not know yet what the finger did meanwhile: its touch events are still
// waiting. So a timer that runs late stands down (the release then decides, by the touches'
// own times), and one that is on time takes a short second step and looks again.
function whenStill(delay, action) {
  const began = performance.now();
  const still = () => dragging && !gesturePanning && !swiping && touches.size === 1 && travel < 12;
  const later = (wait, then) => {
    const timer = setTimeout(() => { touchTimers.delete(timer); then(); }, wait);
    touchTimers.add(timer);
  };
  later(delay - 30, () => {
    if (still() && performance.now() - began < delay + 120) later(30, () => { if (still()) action(); });
  });
}
function releaseDrag() {
  clearTouchTimers(); dragReady = false;
  pointerMoves.flush();
  if (dragButton !== null) send('mouse', { button: dragButton, action: 'up' });
  dragButton = null;
}
function resetTouchGesture() {
  pinchStartDistance = 0; pinchAnchor = null; twoFingerKind = null; scrollOwed = 0;
  touches.clear(); gesturePanning = false; twoFingerStart = 0;
  twoFingerMoved = false; twoFingerCenter = null; rightHeld = false;
  touchPoint = null; swiping = false;
  releaseDrag(); dragging = false;
}
function onPicture(event) {
  const box = desktop.getBoundingClientRect();
  return event.clientX >= box.left && event.clientX <= box.right && event.clientY >= box.top && event.clientY <= box.bottom;
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

// Two fingers on the stage: a pinch zooms around the point between the fingers, anything
// else scrolls the window under the PC's pointer.
function twoFingerMove() {
  const [a, b] = touches.values();
  const distance = Math.hypot(a.x - b.x, a.y - b.y);
  const middle = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
  if (!twoFingerKind) {
    twoFingerKind = twoFingerGesture(distance - pinchStartDistance, Math.hypot(middle.x - twoFingerCenter.x, middle.y - twoFingerCenter.y));
    if (!twoFingerKind) return;
    twoFingerMoved = true;
    if (twoFingerKind === 'pinch') {
      // The point of the picture the fingers started on stays between them for the whole pinch.
      const scroller = $('screenScroller'), box = scroller.getBoundingClientRect();
      pinchAnchor = { pictureX: fractionAt(scroller.scrollLeft, twoFingerCenter.x - box.left, layout.left, layout.drawW),
                      pictureY: fractionAt(scroller.scrollTop, twoFingerCenter.y - box.top, layout.top, layout.drawH) };
      pinchStartDistance = distance; // zoom from here on, without a jump
      zoomChosen();
    }
    scrollFrom = middle.y;
  }
  if (twoFingerKind === 'pinch') {
    setZoom(pinchZoom(pinchStartZoom, pinchStartDistance, distance, zoomLimits().max), { ...pinchAnchor, x: middle.x, y: middle.y });
    return;
  }
  scrollOwed += wheelUnits(middle.y - scrollFrom, pictureScale(), monitorScale());
  scrollFrom = middle.y;
  if (performance.now() - sentAt > 24 && Math.abs(scrollOwed) >= 1 && state.control?.bufferedAmount < 4096) {
    const delta = Math.max(-1200, Math.min(1200, Math.round(scrollOwed)));
    send('wheel', { delta });
    scrollOwed -= delta; sentAt = performance.now();
  }
}

pad.addEventListener('pointerdown', (event) => {
  if (!state.active) return;
  // A mouse or pen only counts on the picture itself; fingers may use the whole stage.
  if (event.pointerType !== 'touch' && event.target !== desktop) return;
  event.preventDefault();
  // Keyboard input goes to the PC while the desktop has focus. While the typing bar is open
  // a finger leaves the focus in its box, or the phone would put its keyboard away.
  if (!state.keyboardOpen || event.pointerType !== 'touch') desktop.focus({ preventScroll: true });
  if (mouseLocked()) {
    dragging = true;
    dragButton = event.button >= 0 && event.button <= 4 ? event.button : 0;
    pointerMoves.flush();
    send('mouse', { button: dragButton, action: 'down' });
    return;
  }
  try { desktop.setPointerCapture(event.pointerId); } catch { /* not capturable */ }
  const direct = $('modePick').value === 'direct';
  if (event.pointerType === 'touch') {
    touches.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (touches.size > 1) {
      if (touches.size === 2) {
        const [a, b] = touches.values();
        twoFingerCenter = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
        pinchStartDistance = Math.hypot(a.x - b.x, a.y - b.y);
        pinchStartZoom = state.zoom; twoFingerKind = null; scrollOwed = 0;
        twoFingerStart = event.timeStamp; twoFingerMoved = false;
      } else twoFingerMoved = true;
      gesturePanning = true;
      releaseDrag(); dragging = false; touchPoint = null; swiping = false; return;
    }
    // Direct touch points at what is under the finger, and beside the picture there is nothing.
    if (direct && !gesturePanning && !onPicture(event)) return;
  }
  if (gesturePanning) return; // first finger lifted during a multi-touch gesture
  dragging = true; travel = 0; touchStamp = event.timeStamp; rightHeld = false; swiping = false;
  state.lastX = event.clientX; state.lastY = event.clientY;
  // A finger may be the first of a pinch, or about to move a zoomed-in picture, so the pointer waits.
  touchPoint = direct && event.pointerType === 'touch' ? { clientX: event.clientX, clientY: event.clientY } : null;
  if (direct && !touchPoint) absolutePointer(event);
  if (event.pointerType === 'touch') {
    // Only arm dragging after a deliberate pause. No mouse-down is sent until the finger
    // moves, which leaves a still long-press free for right-click.
    whenStill(330, () => {
      dragReady = true;
      if (touchPoint) absolutePointer(touchPoint); // the pointer shows where a drag would start
    });
    whenStill(660, () => {
      if (dragButton === null) { rightHeld = true; dragReady = false; mouseClick(2); }
    });
  } else {
    dragButton = event.button >= 0 && event.button <= 4 ? event.button : 0; // 3/4 = back/forward
    pointerMoves.flush();
    send('mouse', { button: dragButton, action: 'down' });
  }
});
pad.addEventListener('pointermove', (event) => {
  if (!state.active) return;
  if (mouseLocked()) { queueLockedMove(event); return; }
  if (event.pointerType === 'touch') {
    if (!touches.has(event.pointerId)) return;
    touches.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (gesturePanning) { if (touches.size === 2 && pinchStartDistance > 0) twoFingerMove(); return; }
  } else if (event.target !== desktop) return;
  if (gesturePanning || rightHeld) return;
  const dx = event.clientX - state.lastX, dy = event.clientY - state.lastY;
  state.lastX = event.clientX; state.lastY = event.clientY;
  if (dragging) {
    travel += Math.abs(dx) + Math.abs(dy);
    if (event.pointerType === 'touch' && travel > 12) {
      // The times are the touches' own, so a page that was busy cannot mistake a swipe for a hold.
      const startDrag = dragReady && dragButton === null && event.timeStamp - touchStamp >= 300;
      clearTouchTimers();
      if (startDrag) { pointerMoves.flush(); dragButton = 0; send('mouse', { button: 0, action: 'down' }); }
      else if (touchPoint && dragButton === null) {
        // No pause first: the finger moves a zoomed-in picture, or else the pointer follows it.
        if (canPan()) swiping = true; else touchPoint = null;
      }
      dragReady = false;
    }
  }
  if (swiping) { panBy(dx, dy); return; }
  if ($('modePick').value === 'direct') {
    if (event.pointerType !== 'touch' || (dragging && (!touchPoint || dragButton !== null))) absolutePointer(event);
  } else if (dragging) {
    const gain = trackpadGain(pictureScale());
    relativePointer(dx * gain, dy * gain);
  }
});
function pointerEnd(event) {
  if (event.pointerType !== 'touch' && !dragging) return;
  pointerMoves.flush();
  if (event.pointerType === 'touch') touches.delete(event.pointerId);
  if (gesturePanning) {
    if (touches.size === 0) {
      // A two-finger tap is not a scroll: fire it once, after both fingers lift, with little movement.
      if (!twoFingerMoved && twoFingerStart && event.timeStamp - twoFingerStart < 420) {
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
  const wasRightHeld = rightHeld, wasSwiping = swiping, waited = touchPoint;
  releaseDrag(); rightHeld = false; swiping = false; touchPoint = null;
  if (event.pointerType === 'touch' && !wasDown && !wasRightHeld && !wasSwiping && travel < 15) {
    if (waited) absolutePointer(event); // the pointer waited for this: it goes where the tap landed
    // By the touches' own times: a short touch is a click. A long one that got no right-click
    // while the finger was down (the page was busy) gets it now.
    const held = event.timeStamp - touchStamp;
    if (held < 660) mouseClick();
    else if (travel < 12) mouseClick(2);
    typingMoved();
  }
}
pad.addEventListener('pointerup', pointerEnd);
pad.addEventListener('pointercancel', (event) => {
  pointerMoves.cancel(); pointerMotion.cancel(); hideCursor();
  // Cancellation (e.g. an iOS system gesture) must never produce a click.
  if (event.pointerType === 'touch') touches.delete(event.pointerId);
  twoFingerMoved = true;
  if (touches.size === 0) resetTouchGesture();
  else { releaseDrag(); dragging = false; touchPoint = null; swiping = false; }
});
// Suppress iOS long-press selection and the browser's own context menu.
pad.addEventListener('contextmenu', (event) => event.preventDefault());

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
  releaseAllInput();
  state.display = display.id;
  state.displayName = display.name;
  resetView(); // another monitor starts with its whole screen
  updateScreenLayout();
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
$('zoom').addEventListener('input', () => { zoomChosen(); setZoom(Number($('zoom').value)); });
$('quickZoom').addEventListener('click', toggleZoom);
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
  // The visual viewport is the part of the page the phone keyboard leaves free. The sheet
  // sits on its lower edge and, while typing on a phone, the stage starts at its upper edge.
  const vv = window.visualViewport;
  const bottom = vv ? Math.max(0, window.innerHeight - vv.height - vv.offsetTop) : 0;
  const style = document.documentElement.style;
  style.setProperty('--keyboard-offset', `${Math.round(bottom)}px`);
  style.setProperty('--viewport-top', `${Math.round(vv ? Math.max(0, vv.offsetTop) : 0)}px`);
}
window.visualViewport?.addEventListener('resize', syncKeyboardViewport);
window.visualViewport?.addEventListener('scroll', syncKeyboardViewport);
// On a phone the stage ends above the typing bar (style.css). With "More" open the sheet
// covers the picture instead, so its height is not passed on then.
function syncTypingHeight() {
  if (!state.keyboardMore) document.documentElement.style.setProperty('--typing-height', `${state.keyboardOpen ? $('keyboardSheet').offsetHeight : 0}px`);
}
new ResizeObserver(syncTypingHeight).observe($('keyboardSheet'));

function setKeyboard(open) {
  if (open && state.keyboardOpen) { $('liveKeys').focus({ preventScroll: true }); return; }
  state.keyboardOpen = open;
  $('keyboardSheet').classList.toggle('hidden', !open);
  document.body.classList.toggle('typing', open);
  $('showKeyboard').setAttribute('aria-expanded', String(open));
  $('quickType').setAttribute('aria-pressed', String(open));
  if (open) {
    if (state.monitorsOpen) setMonitorSheet(false);
    $('keyboardStatus').textContent = '';
    resetLiveKeys();
    $('liveKeys').focus({ preventScroll: true }); // must happen inside the tap for iOS to show its keyboard
    syncTypingHeight();
    updateScreenLayout(); // the stage is smaller now; zoom in on what is left of it
    zoomForTyping();
  } else {
    setKeyboardMore(false);
    $('liveKeys').blur(); $('keyboardText').blur();
    releaseHeldKeys(); // a Ctrl left on would turn the next taps into shortcuts
    document.documentElement.style.setProperty('--keyboard-offset', '0px');
    syncTypingHeight();
    updateScreenLayout();
    leaveTypingZoom();
  }
  syncKeyboardViewport();
}
function setKeyboardMore(open) {
  state.keyboardMore = open;
  $('keyboardMorePanel').classList.toggle('hidden', !open);
  $('keyboardMore').setAttribute('aria-expanded', String(open));
  $('keyboardMore').textContent = open ? 'Less' : 'More';
}

// Live typing (live-typing.js has the rule). `typedSoFar` is what the box held when it was
// last compared, its invisible beginning included.
let typedSoFar = TYPING_GUARD, composing = false;
function resetLiveKeys() {
  typedSoFar = TYPING_GUARD;
  $('liveKeys').value = TYPING_GUARD;
  $('typingBox').classList.remove('has-text');
}
// The text cursor on the PC may be somewhere else now (a tap, Tab, an arrow key), so the box
// no longer says what a Backspace would delete there.
function typingMoved() { if (state.keyboardOpen && !composing) resetLiveKeys(); }
function releaseHeldKeys() {
  for (const button of document.querySelectorAll('[data-hold="true"][aria-pressed="true"]')) {
    key(button.dataset.specialKey, 'up');
    button.setAttribute('aria-pressed', 'false');
  }
}
function typeOnPc(text) {
  for (const part of text.split(/(\n)/)) {
    if (part === '\n') { tapKey('Enter'); continue; }
    if (!part) continue;
    // With Ctrl, Alt, Shift or the Windows key held, one letter or digit makes a shortcut.
    const held = document.querySelector('[data-hold="true"][aria-pressed="true"]');
    const code = held && Array.from(part).length === 1 ? keyCodeFor(part) : null;
    if (code) { tapKey(code); releaseHeldKeys(); }
    else sendTextChunks(part);
  }
}
function liveKeysChanged() {
  const box = $('liveKeys');
  if (state.watchOnly || state.control?.readyState !== WebSocket.OPEN) {
    $('keyboardStatus').textContent = state.watchOnly ? WATCHING : 'Input is disconnected.';
    if (!composing) resetLiveKeys();
    return;
  }
  $('keyboardStatus').textContent = '';
  const change = typingChange(typedSoFar, box.value);
  for (let i = 0; i < change.backspaces; i++) tapKey('Backspace');
  typeOnPc(change.text);
  typedSoFar = box.value;
  $('typingBox').classList.toggle('has-text', typedText(typedSoFar) !== '');
  // Start again after Enter, when the invisible beginning is damaged, and before the box
  // gets long. Never in the middle of a word the phone keyboard is still putting together.
  if (!composing && (change.text.includes('\n') || !typedSoFar.startsWith(TYPING_GUARD) || typedSoFar.length > 240)) resetLiveKeys();
}
$('liveKeys').addEventListener('input', liveKeysChanged);
$('liveKeys').addEventListener('compositionstart', () => { composing = true; });
$('liveKeys').addEventListener('compositionend', () => { composing = false; liveKeysChanged(); });

// A tap on one of the sheet's buttons must not take the focus out of the box being typed
// in, or a phone would put its keyboard away.
$('keyboardSheet').addEventListener('mousedown', (event) => { if (event.target.closest('button')) event.preventDefault(); });
$('showKeyboard').addEventListener('click', () => setKeyboard(!state.keyboardOpen));
$('quickType').addEventListener('click', () => setKeyboard(!state.keyboardOpen));
$('closeKeyboard').addEventListener('click', () => setKeyboard(false));
$('keyboardMore').addEventListener('click', () => setKeyboardMore(!state.keyboardMore));
$('keyboardSend').addEventListener('click', () => {
  const text = $('keyboardText').value;
  if (!text) { $('keyboardStatus').textContent = 'Type something first.'; return; }
  if (state.watchOnly) { $('keyboardStatus').textContent = WATCHING; return; }
  if (!sendTextChunks(text)) { $('keyboardStatus').textContent = 'Disconnected. Your text is kept; reconnect first.'; return; }
  $('keyboardText').value = '';
  $('keyboardStatus').textContent = 'Sent to the active window on your PC.';
  typingMoved();
});

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
const rumble = controllerRumble(() => state.active && !state.stopped && !state.watchOnly && !state.touchPad && !document.hidden && document.hasFocus() && state.control?.readyState === WebSocket.OPEN);
const touchPad = installTouchPad($('touchPad'), send, () => state.active && !state.stopped && !state.watchOnly && state.touchPad);
for (const button of document.querySelectorAll('[data-special-key]')) {
  button.addEventListener('click', () => {
    if (!state.active || state.watchOnly) return;
    const code = button.dataset.specialKey;
    if (button.dataset.hold === 'true') {
      const down = button.getAttribute('aria-pressed') !== 'true';
      key(code, down ? 'down' : 'up'); button.setAttribute('aria-pressed', String(down));
    } else { tapKey(code); typingMoved(); }
  });
}
for (const button of document.querySelectorAll('[data-combo]')) {
  button.addEventListener('click', () => {
    if (!state.active || state.watchOnly) return;
    for (const step of shortcutSteps(button.dataset.combo)) key(step.code, step.action);
    typingMoved();
  });
}
const gamepads = watchGamepads({
  send: (kind, data) => state.active && !state.touchPad && !document.hidden && send(kind, data),
  onChange: (names) => {
    if (names.length) $('quickStatus').textContent = `${names.length} controller${names.length > 1 ? 's' : ''} connected`;
  },
});

applySettings(loadSettings());
buildToggles();
syncToggles();
initPairing({ onPaired: initialize });
initAwayView({ onReady: initialize });
initHome({
  openStream,
  prepareSound,
  gameMode: () => state.gameAvailable,
  signedOut: showPair,
});
initialize();
