// Away from home (1.2). At home, a paired device asks the PC for a one-time code for itself and
// takes it to the away address: a public address that the Gaming Hub on the PC forwards here.
// There the code gives this browser the device's away key. New devices cannot pair at the away
// address; the PC refuses that (see docs/SECURITY.md).
import { $ } from './dom.js';
import { api, session } from './api.js';

export const CODE_LENGTH = 12;
const ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'; // the PC's alphabet: no 0/O or 1/I

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
}

/** The away screen's form, shown at the away address to a browser without an away key.
 *  `onReady` runs once the PC accepted the code. */
export function initAwayView({ onReady }) {
  $('awayForm').addEventListener('submit', async (event) => {
    event.preventDefault();
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
  });
}

/** The "Away from home" part of This device, from what /api/v1/me said. Hidden while the PC
 *  has away access turned off. */
export function showAwayPanel(away = session.away) {
  const available = Boolean(away && away.available);
  $('awayPanel').classList.toggle('hidden', !available);
  if (!available) return;
  $('awayHandoff').classList.add('hidden');
  $('awayStatus').textContent = '';
  if (away.here) {
    $('awayState').textContent = `You are connected away from home, at ${away.url}.`;
    $('awaySetup').classList.add('hidden');
    return;
  }
  $('awayState').textContent = away.ready
    ? `This device is set up to use ${away.url} when you are away.`
    : `This device can also work away from home, at ${away.url}. Set it up here once, while you are at home.`;
  $('awaySetup').textContent = away.ready ? 'Set up again' : 'Set up away access';
  $('awaySetup').classList.remove('hidden');
}

/** The set-up button: asks the PC for a code and shows the link and the code. `unpaired(error)`
 *  is the home screen's check that sends an unpaired browser back to pairing. */
export function initAwayPanel({ unpaired }) {
  $('awaySetup').addEventListener('click', async () => {
    $('awayStatus').textContent = '';
    $('awaySetup').disabled = true;
    try {
      const reply = await api('/api/v1/away/code', { method: 'POST' });
      $('awayLink').href = reply.url;
      $('awayCodeShown').textContent = reply.code;
      $('awayHandoff').classList.remove('hidden');
    } catch (error) {
      if (!unpaired(error)) $('awayStatus').textContent = error.message;
    } finally {
      $('awaySetup').disabled = false;
    }
  });
}
