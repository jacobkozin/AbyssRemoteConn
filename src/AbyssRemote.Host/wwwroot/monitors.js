// Monitor cards: one per monitor of the PC, each with a small live picture. The home screen
// shows them to open a stream; the stream view shows the same cards to switch monitor.
import { el } from './dom.js';
import { api, fetchFile } from './api.js';

const REFRESH_MS = 5000;
const WIDEST = 4, NARROWEST = 0.4; // limits for a card's shape, so an odd answer cannot break the page

// ---------------------------------------------------------------- pure helpers (checked in Node)

/** "2 · Aurora 32 4K". Two monitors of one model share a name, so the number is always there;
 *  a monitor the PC calls "Monitor 3" already has it. */
export function monitorLabel(display) {
  const number = display.id + 1;
  const name = String(display.friendlyName || '').trim() || `Monitor ${number}`;
  return name === `Monitor ${number}` ? name : `${number} · ${name}`;
}

/** "2560 × 1440 · 144 Hz". The refresh rate is left out when Windows did not report one. */
export function monitorDetail(display) {
  const size = `${display.width} × ${display.height}`;
  return display.refreshHz > 0 ? `${size} · ${display.refreshHz} Hz` : size;
}

/** Width divided by height, for drawing the card in the monitor's own shape. */
export function monitorAspect(display) {
  const ratio = display.width / display.height;
  if (!Number.isFinite(ratio) || ratio <= 0) return 16 / 9;
  return Math.min(WIDEST, Math.max(NARROWEST, ratio));
}

/** How wide a preview to ask the PC for: enough pixels for the card on this screen, in steps
 *  of 160 so a slightly different layout does not ask for a slightly different picture. The
 *  PC accepts 160 to 960. */
export function previewWidth(cardWidth, pixelRatio = 1) {
  const wanted = (Number(cardWidth) || 0) * Math.min(Math.max(Number(pixelRatio) || 1, 1), 3);
  if (wanted <= 0) return 480;
  return Math.min(960, Math.max(160, Math.ceil(wanted / 160) * 160));
}

/** The monitor to show: the one remembered by its Windows device name, else the main one,
 *  else the first. Numbers are not remembered: Windows renumbers monitors when one is
 *  plugged in or the arrangement changes. */
export function pickMonitor(displays, rememberedName) {
  if (!Array.isArray(displays) || displays.length === 0) return null;
  return displays.find((d) => d.name === rememberedName) ?? displays.find((d) => d.primary) ?? displays[0];
}

/** True when two answers of the PC describe the same monitors (so the cards can stay). */
export function sameMonitors(a, b) {
  const key = (d) => [d.id, d.name, d.width, d.height, d.primary, d.friendlyName, d.refreshHz].join('|');
  return a.length === b.length && a.every((d, i) => key(d) === key(b[i]));
}

// ---------------------------------------------------------------- the cards

/** Fills `list` (a <ul>) with monitor cards and keeps their pictures fresh while started.
 *  options:
 *    status       element for a message about the whole list
 *    onPick       called with the monitor when its card is tapped
 *    onGame       (optional) called for the smaller "Game mode" action
 *    gameMode     (optional) () => true when Game mode can be offered
 *    current      (optional) () => id of the monitor on screen now, to mark its card
 *    onList       (optional) called with each fresh list of monitors
 *    unpaired     called with an error; returns true when it handled "not paired any more" */
export function createMonitorPicker(list, options) {
  let displays = [], running = false, busy = false, timer = 0;
  const cards = new Map(); // monitor id -> { item, frame, picture, note, url }

  function build() {
    for (const card of cards.values()) URL.revokeObjectURL(card.url);
    cards.clear();
    list.replaceChildren(...displays.map((display) => {
      const item = el('li', 'monitor-card');
      item.style.setProperty('--ratio', String(monitorAspect(display)));

      const open = el('button', 'monitor-open');
      open.type = 'button';
      const frame = el('span', 'monitor-frame');
      const picture = el('img', 'monitor-picture hidden');
      picture.alt = '';
      const note = el('span', 'monitor-note', 'Loading…');
      frame.append(picture, note);
      const text = el('span', 'monitor-text');
      const title = el('strong', '', monitorLabel(display));
      if (display.primary) title.append(el('span', 'badge', 'Main'));
      text.append(title, el('small', '', monitorDetail(display)));
      open.append(frame, text);
      open.addEventListener('click', () => options.onPick(display));
      item.append(open);

      if (options.onGame && options.gameMode?.()) {
        const game = el('button', 'button monitor-game', 'Game mode');
        game.type = 'button';
        game.setAttribute('aria-label', `Open ${monitorLabel(display)} in Game mode`);
        game.addEventListener('click', () => options.onGame(display));
        item.append(game);
      }
      cards.set(display.id, { item, frame, picture, note, url: '' });
      return item;
    }));
    markCurrent();
  }

  function markCurrent() {
    const current = options.current?.();
    for (const [id, card] of cards) {
      if (id === current) card.item.setAttribute('aria-current', 'true');
      else card.item.removeAttribute('aria-current');
    }
  }

  // An <img> cannot read the sentence the PC sends with a refusal, so the picture is fetched
  // and shown from memory instead.
  async function loadPicture(display) {
    const card = cards.get(display.id);
    try {
      const width = previewWidth(card.frame.clientWidth, window.devicePixelRatio);
      const response = await fetchFile(`/api/v1/displays/${display.id}/preview?width=${width}`);
      const url = URL.createObjectURL(await response.blob());
      URL.revokeObjectURL(card.url);
      card.url = url;
      card.picture.src = url;
      card.picture.classList.remove('hidden');
      card.note.classList.add('hidden');
    } catch (error) {
      if (!error.status || error.status === 401) throw error; // the PC is gone, or this device is: not about one card
      // 503 carries the PC's reason (locked, UAC prompt). 404 means the monitor was unplugged.
      card.note.textContent = error.status === 503 ? error.message : error.status === 404 ? 'This monitor is no longer connected.' : 'No picture right now.';
      card.picture.classList.add('hidden');
      card.note.classList.remove('hidden');
    }
  }

  async function refresh() {
    if (busy || !running || document.hidden) return;
    busy = true;
    try {
      const latest = await api('/api/v1/displays');
      if (cards.size === 0 || !sameMonitors(latest, displays)) {
        displays = latest;
        build();
      }
      options.onList?.(displays);
      options.status.textContent = displays.length ? '' : 'The PC did not report any monitors.';
      // One after the other: the PC also captures one monitor at a time.
      for (const display of displays) {
        if (!running) break;
        await loadPicture(display);
      }
    } catch (error) {
      if (!options.unpaired(error)) options.status.textContent = error.status ? error.message : 'The PC is not answering. Trying again…';
    } finally {
      busy = false;
    }
  }

  const tabShown = () => { if (!document.hidden) refresh(); };

  return {
    /** Shows the cards and refreshes their pictures every few seconds while the tab is visible. */
    start() {
      if (running) return;
      running = true;
      markCurrent();
      refresh();
      timer = setInterval(refresh, REFRESH_MS);
      document.addEventListener('visibilitychange', tabShown);
    },
    /** Stops asking the PC for pictures (the cards are hidden). */
    stop() {
      running = false;
      clearInterval(timer);
      document.removeEventListener('visibilitychange', tabShown);
    },
  };
}
