// Away from home (1.2). At home, a paired device asks the PC for a one-time code for itself and
// takes it to the away address: a public address that the Gaming Hub on the PC forwards here.
// There the code gives this browser the device's away key. New devices cannot pair at the away
// address; the PC refuses that (see docs/SECURITY.md).
//
// The home screen leads the user through it: the "Away from home" panel stands first on the
// home screen until this device is set up, and its steps say what to tap, in order.
import { $, el } from './dom.js';
import { api, session } from './api.js';

export const CODE_LENGTH = 12;
const ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'; // the PC's alphabet: no 0/O or 1/I
const LATER_KEY = 'abyss-remote:away-later';        // "Not now" was tapped on this device

/** An away code was accepted on this page just now, so the home screen says what comes next. */
let justSetUp = false;

/** The code in capitals without spaces or dashes, or '' when it cannot be an away code. */
export function normalizeAwayCode(text) {
  const code = String(text ?? '').toUpperCase().replace(/[\s-]/g, '');
  return code.length === CODE_LENGTH && [...code].every((ch) => ALPHABET.includes(ch)) ? code : '';
}

/** The code from an address ending in "#away=…" (the link the home screen gives), or ''. */
export function awayCodeFromHash(hash) {
  const match = /^#away=([A-Za-z0-9-]{12,14})$/.exec(String(hash ?? ''));
  return match ? normalizeAwayCode(match[1]) : '';
}

/** "ABCD-EFGH-JKLM": three groups of four, like the PC shows it. */
export function formatAwayCode(code) {
  const clean = normalizeAwayCode(code);
  return clean ? `${clean.slice(0, 4)}-${clean.slice(4, 8)}-${clean.slice(8)}` : '';
}

/** "remote.example.org" from "https://remote.example.org": the address as a person types it. */
export function hostOf(url) {
  return String(url ?? '').replace(/^https?:\/\//i, '').replace(/[/?#].*$/, '');
}

/** True in an icon on an iPhone's or iPad's Home Screen. Such an icon keeps its own sign-in,
 *  apart from Safari's, and a link to another address opens in a browser sheet inside it. So
 *  a link cannot set it up for the away address: that address needs an icon of its own. */
export function isHomeScreenIcon(nav = navigator) {
  return nav.standalone === true;
}

/** True on an iPhone or iPad, in Safari or in an icon. Safari on an iPad says it is a Mac;
 *  only the iPad has a touch screen. */
export function isIphoneOrIpad(nav = navigator) {
  const system = String(nav.platform ?? '');
  return /^(iPhone|iPod|iPad)/.test(system) || (/^Mac/.test(system) && (nav.maxTouchPoints ?? 0) > 1);
}

/** Returns a code that came with the address and takes it out of the address bar and the
 *  history at once. It is a fragment, so it never reached the PC or the proxy's logs. */
export function takeAwayCodeFromAddress() {
  const hash = String(location.hash ?? '');
  if (!hash.startsWith('#away=')) return '';
  history.replaceState(null, '', location.pathname);
  return awayCodeFromHash(hash);
}

/** Gives this browser the device's away key. Throws with the PC's own sentence when it refuses. */
export async function redeemAwayCode(code) {
  await api('/api/v1/away/redeem', { method: 'POST', body: JSON.stringify({ code }) });
  justSetUp = true;
}

/** The away screen's form, shown at the away address to a browser without an away key.
 *  `onReady` runs once the PC accepted the code. */
export function initAwayView({ onReady }) {
  const useCode = async () => {
    $('awayError').textContent = '';
    const code = normalizeAwayCode($('awayCode').value);
    if (!code) {
      $('awayError').textContent = 'An away code has 12 letters and digits, like ABCD-EFGH-JKLM.';
      return;
    }
    $('awaySubmit').disabled = true;
    try {
      await redeemAwayCode(code);
      $('awayCode').value = '';
      await onReady();
    } catch (error) {
      $('awayError').textContent = error.message;
    } finally {
      $('awaySubmit').disabled = false;
    }
  };
  $('awayForm').addEventListener('submit', (event) => {
    event.preventDefault();
    return useCode();
  });

  // Safari on an iPhone would take the code for itself, and an icon made afterwards would
  // have no sign-in: say so before the code is used.
  $('awayIconHint').classList.toggle('hidden', !isIphoneOrIpad() || isHomeScreenIcon());

  // "Paste code" takes the code that "Copy code" put on the clipboard at home. The browser
  // asks the user before it hands the clipboard over.
  const paste = $('awayPaste');
  paste.classList.toggle('hidden', typeof navigator.clipboard?.readText !== 'function');
  paste.addEventListener('click', async () => {
    let text = '';
    try { text = await navigator.clipboard.readText(); } catch { /* not allowed, or nothing there */ }
    const code = normalizeAwayCode(text);
    if (!code) {
      $('awayError').textContent = 'There is no away code to paste yet. Copy it in Abyss Remote at home first, or type it in.';
      return;
    }
    $('awayCode').value = formatAwayCode(code);
    await useCode();
  });
}

/** What the "Away from home" panel on the home screen says, from what /api/v1/me reported
 *  about this device. `text` is a list of pieces; a piece in brackets is shown in bold.
 *  `top` puts the panel first on the home screen, where nobody has to look for it. */
export function awayPanelView(away, { later = false, justSetUp: fresh = false, icon = false } = {}) {
  if (!away || !away.available) return { visible: false };
  const name = hostOf(away.url);
  if (away.here) {
    if (!fresh) return { visible: true, top: false, title: 'Away from home', text: ['You are connected away from home, at ', [name], '.'] };
    return {
      visible: true, top: true, title: 'This device is set up', done: true,
      text: icon
        ? ['Use this icon whenever you are away from home. At home, keep using your other Abyss Remote icon.']
        : ['This is the page to open when you are away from home: ', [name], '. Add it to your bookmarks now, so it is one tap away.'],
    };
  }
  if (away.ready) {
    return { visible: true, top: false, title: 'Away from home', setup: 'Set up again', text: ['This device is set up. When you are away from home, open ', [name], '.'] };
  }
  return {
    visible: true, top: !later, title: 'Use this device away from home', setup: 'Set up away access', later: !later,
    text: ['Right now this device only reaches your PC at home. To use it from anywhere, at ', [name], ', set it up once while you are at home. It takes a minute.'],
  };
}

/** Shows the panel for the current state: first on the home screen while this device still
 *  has to be set up (or was set up a moment ago), last otherwise. Hidden while the PC has
 *  away access turned off. */
export function showAwayPanel(away = session.away) {
  const view = awayPanelView(away, { later: saidLater(), justSetUp, icon: isHomeScreenIcon() });
  const panel = $('awayPanel');
  panel.classList.toggle('hidden', !view.visible);
  if (!view.visible) return;
  if (view.top) $('homeView').prepend(panel); else $('homeSide').append(panel);
  $('awayTitle').textContent = view.title;
  $('awayState').replaceChildren(...pieces(view.text));
  $('awayHandoff').classList.add('hidden');
  $('awayHandoff').replaceChildren();
  $('awayStatus').textContent = '';
  $('awaySetup').textContent = view.setup ?? '';
  $('awaySetup').classList.toggle('hidden', !view.setup);
  $('awaySetup').classList.toggle('primary', Boolean(view.top));
  $('awayLater').classList.toggle('hidden', !view.later);
  $('awayDone').classList.toggle('hidden', !view.done);
  $('awayButtons').classList.toggle('hidden', !(view.setup || view.later || view.done));
}

/** The panel's buttons. `unpaired(error)` is the home screen's check that sends an unpaired
 *  browser back to pairing. */
export function initAwayPanel({ unpaired }) {
  $('awaySetup').addEventListener('click', async () => {
    $('awayStatus').textContent = '';
    $('awaySetup').disabled = true;
    try {
      showSteps(await api('/api/v1/away/code', { method: 'POST' }));
    } catch (error) {
      if (!unpaired(error)) $('awayStatus').textContent = error.message;
    } finally {
      $('awaySetup').disabled = false;
    }
  });
  $('awayLater').addEventListener('click', () => {
    try { localStorage.setItem(LATER_KEY, '1'); } catch { /* not remembered: it asks again next time */ }
    showAwayPanel();
  });
  $('awayDone').addEventListener('click', () => {
    justSetUp = false;
    showAwayPanel();
  });
}

function saidLater() {
  try { return localStorage.getItem(LATER_KEY) === '1'; } catch { return false; }
}

/** The PC made a code (`reply` is its answer): show what to do with it, step by step. A
 *  browser gets a link that does everything. An icon on an iPhone's Home Screen cannot use a
 *  link (see isHomeScreenIcon), so it gets the code to carry over, and a browser shows those
 *  steps too, folded, for whoever wants such an icon. */
function showSteps(reply) {
  const name = hostOf(reply.address);
  const box = $('awayHandoff');
  if (isHomeScreenIcon()) {
    $('awayState').replaceChildren(...pieces(['This icon only works at home. Away from home needs an icon of its own, made from ', [name], '. Here is how:']));
    box.replaceChildren(iconSteps(reply, name));
  } else {
    const fold = el('details', 'help');
    fold.append(
      el('summary', '', 'On an iPhone or iPad, with a Home Screen icon?'),
      words('p', '', ['An icon keeps its own sign-in, so the link above does not reach it. Make an icon for ', [name], ' instead:']),
      iconSteps(reply, name));
    box.replaceChildren(browserSteps(reply, name), fold);
  }
  box.classList.remove('hidden');
  // The steps take over. The button now only makes a fresh code, should this one run out.
  $('awaySetup').textContent = 'New code';
  $('awaySetup').classList.remove('primary');
  $('awayLater').classList.add('hidden');
}

function browserSteps(reply, name) {
  const link = el('a', 'button primary', 'Open the away address');
  link.href = reply.url;
  link.rel = 'noopener';
  const press = window.matchMedia?.('(pointer: coarse)').matches ? 'Tap ' : 'Click '; // a finger or a mouse
  return steps(
    [words('span', '', [press, ['Open the away address'], '. It opens ', [name], ' with a one-time code, and this browser is set up.']), link],
    [words('span', '', ['That is all. Whenever you are away from home, open ', [name], ' in this browser.'])]);
}

function iconSteps(reply, name) {
  const code = el('div', 'code-row');
  code.append(el('strong', 'away-code', reply.code), copyButton('Copy code', reply.code));
  return steps(
    [words('span', '', ['Copy this code. It works once, for 10 minutes.']), code],
    [words('span', '', ['Open Safari and go to ', [name], '.']), copyButton('Copy address', name)],
    [words('span', '', ['There, tap the Share button, then ', ['Add to Home Screen'], '. Open the new icon.'])],
    [words('span', '', ['In the new icon, tap ', ['Paste code'], '.'])]);
}

/** A numbered list. Each step is the things it shows, top to bottom. */
function steps(...items) {
  const list = el('ol', 'steps');
  for (const item of items) {
    const line = el('li');
    line.append(...item);
    list.append(line);
  }
  return list;
}

/** An element holding text whose important words are bold (see awayPanelView). */
function words(tag, className, text) {
  const element = el(tag, className);
  element.append(...pieces(text));
  return element;
}

function pieces(text) {
  return text.map((piece) => (Array.isArray(piece) ? el('strong', '', piece[0]) : piece));
}

/** A button that puts `text` on the clipboard and says so for a moment. */
function copyButton(label, text) {
  const button = el('button', 'button', label);
  button.type = 'button';
  button.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(text);
      button.textContent = 'Copied';
      setTimeout(() => { button.textContent = label; }, 2000);
    } catch {
      $('awayStatus').textContent = `This browser does not let the page copy. Copy it by hand: ${text}`;
    }
  });
  return button;
}
