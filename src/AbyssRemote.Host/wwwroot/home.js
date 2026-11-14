// The home screen: what you see after pairing and whenever you leave a stream. Monitors,
// games and files have their own modules; this one arranges them and holds the two small
// sections "This PC" and "This device".
import { $, el, icon } from './dom.js';
import { api, session } from './api.js';
import { confirmAction } from './dialog.js';
import { createMonitorPicker } from './monitors.js';
import { initLibrary, loadLibrary } from './library.js';
import { initFiles, loadFiles } from './files.js';

// What the PC can be told to do, with the question asked first. Both cut this device off,
// so neither happens on a single tap.
const PC_ACTIONS = {
  lock: { icon: 'lock', title: 'Lock the PC?', message: 'You cannot unlock the PC from this device.', action: 'Lock', done: 'The PC is locked.' },
  sleep: { icon: 'moon', title: 'Put the PC to sleep?', message: 'The connection will end and you cannot wake the PC from this device.', action: 'Sleep', done: 'The PC is going to sleep.' },
};

let hooks = null;
let monitors = null;

/** `given`:
 *    openStream({ display, mode, sound })  leave home and show a monitor
 *    prepareSound()                        called inside a tap that will end in a stream with sound
 *    gameMode()                            true when the PC and this browser can do Game mode
 *    signedOut(message)                    this browser is not paired any more */
export function initHome(given) {
  hooks = { ...given, unpaired };
  monitors = createMonitorPicker($('monitorList'), {
    status: $('monitorStatus'),
    onPick: (display) => hooks.openStream({ display, mode: 'desktop' }),
    onGame: (display) => hooks.openStream({ display, mode: 'game' }),
    gameMode: hooks.gameMode,
    unpaired,
  });
  initLibrary(hooks);
  initFiles(hooks);

  $('clipGet').addEventListener('click', async () => {
    try { $('clipText').value = (await api('/api/v1/clipboard')).text || ''; sayClip('The PC clipboard is in the box.'); }
    catch (error) { if (!unpaired(error)) sayClip(error.message); }
  });
  $('clipSet').addEventListener('click', async () => {
    const text = $('clipText').value;
    if (!text) { sayClip('Type or paste something first.'); return; }
    try { await api('/api/v1/clipboard', { method: 'POST', body: JSON.stringify({ text }) }); sayClip('Copied to the PC clipboard.'); }
    catch (error) { if (!unpaired(error)) sayClip(error.message); }
  });

  $('devicesButton').addEventListener('click', () => {
    const hidden = $('devicesPanel').classList.toggle('hidden');
    $('devicesButton').setAttribute('aria-expanded', String(!hidden));
    if (!hidden) loadDevices();
  });
  $('signOut').addEventListener('click', signOut);
}

/** Fills the screen with the PC's current state and starts the live monitor pictures. */
export function showHomeScreen() {
  $('deviceLabel').textContent = session.device;
  monitors.start();
  loadLibrary();
  loadFiles();
  loadPcActions();
}

/** Stops asking the PC for pictures while another screen is shown. */
export function hideHomeScreen() {
  monitors?.stop();
}

/** Every section reports a failed request here. Returns true when the PC said "not paired",
 *  which sends the user back to the pairing screen instead of showing a message. */
function unpaired(error) {
  if (error.status !== 401) return false;
  hooks.signedOut('This device is no longer paired. Pair it again with a new code.');
  return true;
}

// ---------------------------------------------------------------- This PC

const sayPc = (message) => { $('pcStatus').textContent = message; };
const sayClip = (message) => { $('clipStatus').textContent = message; };

async function loadPcActions() {
  try {
    const { actions } = await api('/api/v1/actions');
    $('pcActions').replaceChildren(...actions.filter((action) => PC_ACTIONS[action.id]).map((action) => {
      const button = el('button', 'button');
      button.type = 'button';
      button.append(icon(PC_ACTIONS[action.id].icon), action.label);
      button.addEventListener('click', () => runPcAction(action.id));
      return button;
    }));
  } catch (error) {
    if (!unpaired(error)) sayPc(error.message);
  }
}

async function runPcAction(id) {
  const { title, message, action, done } = PC_ACTIONS[id];
  if (!await confirmAction({ title, message, action })) return;
  try {
    await api(`/api/v1/actions/${id}`, { method: 'POST' });
    sayPc(done);
  } catch (error) {
    if (!unpaired(error)) sayPc(error.message);
  }
}

// ---------------------------------------------------------------- This device

const sayDevice = (message) => { $('deviceStatus').textContent = message; };

async function signOut() {
  const yes = await confirmAction({ title: 'Sign out this device?', message: 'It loses its pairing. You will need a new code from the PC to use it again.', action: 'Sign out', danger: true });
  if (!yes) return;
  try {
    await api('/api/v1/logout', { method: 'POST' });
    hooks.signedOut('Signed out.');
  } catch (error) {
    if (!unpaired(error)) sayDevice(error.message);
  }
}

async function loadDevices() {
  try {
    const devices = await api('/api/v1/devices');
    $('devicesList').replaceChildren(...devices.map((device) => {
      const mine = device.id === session.id;
      const row = el('div', 'row');
      const text = el('div', 'row-text');
      const dates = `Paired ${new Date(device.createdUtc).toLocaleDateString()} · expires ${new Date(device.expiresUtc).toLocaleDateString()}`;
      text.append(el('strong', '', mine ? `${device.name} · this device` : device.name), el('small', '', dates));
      const revoke = el('button', 'button danger', 'Revoke');
      revoke.type = 'button';
      revoke.setAttribute('aria-label', `Revoke ${device.name}`);
      revoke.addEventListener('click', () => revokeDevice(device, mine));
      row.append(text, revoke);
      return row;
    }));
  } catch (error) {
    if (!unpaired(error)) sayDevice(error.message);
  }
}

async function revokeDevice(device, mine) {
  const yes = await confirmAction({ title: `Revoke ${device.name}?`, message: 'It is disconnected at once and has to be paired again.', action: 'Revoke', danger: true });
  if (!yes) return;
  try {
    await api(`/api/v1/devices/${encodeURIComponent(device.id)}`, { method: 'DELETE' });
    if (mine) hooks.signedOut('This device was revoked. Pair it again to use this PC.');
    else await loadDevices();
  } catch (error) {
    if (!unpaired(error)) sayDevice(error.message);
  }
}
