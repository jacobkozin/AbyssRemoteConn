// Away from home (1.2). With away access on, the PC has a second address: a public one that
// a web server on the PC forwards here. It works at home and away, so a device needs no other.
// There are two ways onto it:
//   - A device opens that address and pairs there, with the same code as at home (pairing.js).
//   - A browser that is paired at home takes itself there: it asks the PC for a one-time code
//     of its own and opens the away address with it. That is the switch on the pairing screen
//     and the button on the home screen.
import { $ } from './dom.js';
import { api, session } from './api.js';

export const CODE_LENGTH = 12;
const ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'; // the PC's alphabet: no 0/O or 1/I
const LATER_KEY = 'abyss-remote:away-later';        // "Not now" was tapped on this device

/** This browser was signed in at the away address just now, so the home screen says so once. */
let justSetUp = false;

/** The code in capitals without spaces or dashes, or '' when it cannot be a hand-over code. */
export function normalizeAwayCode(text) {
  const code = String(text ?? '').toUpperCase().replace(/[\s-]/g, '');
  return code.length === CODE_LENGTH && [...code].every((ch) => ALPHABET.includes(ch)) ? code : '';
}

/** The code from an address ending in "#away=…": the link a paired browser brings from home. */
export function awayCodeFromHash(hash) {
  const text = String(hash ?? ''), start = '#away=';
  return text.startsWith(start) && /^[A-Za-z0-9-]{12,14}$/.test(text.slice(start.length)) ? normalizeAwayCode(text.slice(start.length)) : '';
}

/** "remote.example.org" from "https://remote.example.org": the address as a person types it. */
export function hostOf(url) {
  return String(url ?? '').replace(/^https?:\/\//i, '').replace(/[/?#].*$/, '');
}

/** True in an icon on an iPhone's or iPad's Home Screen. Such an icon keeps its own sign-in,
 *  apart from Safari's, and opens another address in a browser sheet inside it. So it cannot
 *  take itself to the away address: that address needs an icon of its own. */
export function isHomeScreenIcon(nav = navigator) {
  return nav.standalone === true;
}

/** True on an iPhone or iPad, in Safari or in an icon. Safari on an iPad says it is a Mac;
 *  only the iPad has a touch screen. */
export function isIphoneOrIpad(nav = navigator) {
  const system = String(nav.platform ?? '');
  return /^(iPhone|iPod|iPad)/.test(system) || (/^Mac/.test(system) && (nav.maxTouchPoints ?? 0) > 1);
}

/** Returns a code that a paired browser brought from home, and takes it out of the address bar
 *  and the history at once. It is a fragment, so it never reached the PC or the proxy's logs. */
export function takeAwayCodeFromAddress() {
  const hash = String(location.hash ?? '');
  if (!hash.startsWith('#away=')) return '';
  history.replaceState(null, '', location.pathname);
  return awayCodeFromHash(hash);
}

/** At the away address: signs this browser in with the code it brought from home. Throws with
 *  the PC's own sentence when the PC refuses. */
export async function redeemAwayCode(code) {
  await api('/api/v1/away/redeem', { method: 'POST', body: JSON.stringify({ code }) });
  justSetUp = true;
}

/** This browser was paired at the away address just now: the home screen says once that this
 *  is the address to keep. */
export function noteSetUpHere() {
  justSetUp = true;
}

/** At home, in a paired browser: asks the PC for a one-time code for this device and goes to
 *  the away address with it. This browser is signed in there when the page opens. */
export async function goAway() {
  const reply = await api('/api/v1/away/code', { method: 'POST' });
  location.assign(reply.url);
}

/** What the "Away from home" panel on the home screen says, from what /api/v1/me reported
 *  about this device. `text` is a list of pieces; a piece in brackets is shown in bold.
 *  `top` puts the panel first on the home screen, where nobody has to look for it.
 *  `icon`: this is an icon on an iPhone's Home Screen. `apple`: an iPhone or iPad. */
export function awayPanelView(away, { later = false, justSetUp: fresh = false, icon = false, apple = false } = {}) {
  if (!away || !away.available) return { visible: false };
  const name = hostOf(away.url);
  if (away.here) {
    if (!fresh) return { visible: true, top: false, title: 'At home and away', text: ['You are using ', [name], ', which works at home and away from home.'] };
    return {
      visible: true, top: true, title: 'Works at home and away', done: true,
      text: icon
        ? ['Use this icon wherever you are.']
        : ['Use this address from now on: ', [name], '. Add it to your bookmarks or your Home Screen.',
          ...(apple ? [' On an iPhone or iPad, an icon on the Home Screen is paired by itself: it asks for a code from the PC the first time.'] : [])],
    };
  }
  if (away.ready) {
    return { visible: true, top: false, title: 'Away from home', open: !icon, text: ['Away access is on for this device. It can use ', [name], ', at home and away from home.'] };
  }
  if (icon) {
    return {
      visible: true, top: !later, title: 'Use this device away from home', copy: true, later: !later,
      text: ['This icon only works at home. An icon made from ', [name], ' works at home and away: open that address in Safari, add it to your Home Screen, and pair in that icon with a code from the PC, like you paired this one.'],
    };
  }
  return {
    visible: true, top: !later, title: 'Use this device away from home', setup: true, later: !later,
    text: ['One tap, and this device also works when you are not at home. It then uses ', [name], ', an address that works at home and away.'],
  };
}

/** Shows the panel for the current state: first on the home screen while it has something to
 *  say that matters now, last otherwise. Hidden while the PC has away access turned off. */
export function showAwayPanel(away = session.away) {
  const view = awayPanelView(away, { later: saidLater(), justSetUp, icon: isHomeScreenIcon(), apple: isIphoneOrIpad() });
  const panel = $('awayPanel');
  panel.classList.toggle('hidden', !view.visible);
  if (!view.visible) return;
  if (view.top) $('homeView').prepend(panel); else $('homeSide').append(panel);
  $('awayTitle').textContent = view.title;
  $('awayState').replaceChildren(...view.text.map((piece) => (Array.isArray(piece) ? bold(piece[0]) : piece)));
  $('awayStatus').textContent = '';
  $('awaySetup').classList.toggle('hidden', !view.setup);
  $('awaySetup').disabled = false;
  $('awayOpen').classList.toggle('hidden', !view.open);
  $('awayOpen').href = away.url;
  $('awayOpen').textContent = `Open ${hostOf(away.url)}`;
  $('awayCopy').classList.toggle('hidden', !view.copy);
  $('awayLater').classList.toggle('hidden', !view.later);
  $('awayDone').classList.toggle('hidden', !view.done);
  $('awayButtons').classList.toggle('hidden', !(view.setup || view.open || view.copy || view.later || view.done));
}

/** The panel's buttons. `unpaired(error)` is the home screen's check that sends an unpaired
 *  browser back to pairing. */
export function initAwayPanel({ unpaired }) {
  $('awaySetup').addEventListener('click', async () => {
    $('awayStatus').textContent = '';
    $('awaySetup').disabled = true;
    try {
      await goAway(); // the page leaves for the away address
    } catch (error) {
      if (!unpaired(error)) $('awayStatus').textContent = error.message;
      $('awaySetup').disabled = false;
    }
  });
  $('awayCopy').addEventListener('click', async () => {
    const address = hostOf(session.away?.url);
    try {
      await navigator.clipboard.writeText(address);
      $('awayStatus').textContent = `Copied ${address}. Paste it into Safari's address bar.`;
    } catch {
      $('awayStatus').textContent = `This browser does not let the page copy. Type it in Safari: ${address}`;
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

function bold(text) {
  const element = document.createElement('strong');
  element.textContent = text;
  return element;
}
