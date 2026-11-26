// The pairing screen. An eight-digit, one-time code from the Abyss Remote window on the PC
// turns this browser into a paired device. It is the same screen at home and at the away
// address; pairingLayout says what differs.
import { $ } from './dom.js';
import { api } from './api.js';

/** The digits of an address ending in "#pair=12345678" (the PC's QR code opens one), or ''
 *  for anything else. */
export function pairCodeFromHash(hash) {
  const match = /^#pair=(\d{8})$/.exec(String(hash ?? ''));
  return match ? match[1] : '';
}

/** A first suggestion for this device's name, from what the browser says it runs on
 *  (navigator.platform, for example "iPhone", "MacIntel", "Win32" or "Linux armv8l").
 *  The user can change it before pairing. */
export function guessDeviceName(platform, touchPoints = 0) {
  const system = String(platform ?? '');
  if (/^(iPhone|iPod)/.test(system)) return 'My iPhone';
  // Safari on an iPad says it is a Mac. Only the iPad has a touch screen.
  if (/^iPad/.test(system) || (/^Mac/.test(system) && touchPoints > 1)) return 'My iPad';
  if (/^Mac/.test(system)) return 'My Mac';
  if (/^Win/.test(system)) return 'My Windows PC';
  if (/Android/.test(system) || (/^Linux (arm|aarch)/.test(system) && touchPoints > 0)) return 'My Android device';
  if (/^Linux/.test(system)) return 'My Linux PC';
  return 'My device';
}

/** What the pairing screen shows for where it is opened.
 *  `away`: at the away address, which works at home and away and has a certificate that every
 *  device trusts already. `offered`: the PC has an away address, and it works right now.
 *  `apple`: an iPhone or iPad. `icon`: an icon on its Home Screen. */
export function pairingLayout({ away = false, offered = false, apple = false, icon = false } = {}) {
  // Safari on an iPhone: an icon made after pairing keeps its own sign-in and starts unpaired,
  // so the icon comes first. At home it should be made from the away address when there is one.
  const safari = apple && !icon;
  return {
    anywhere: away,
    certificateHelp: !away,
    awaySwitch: offered && !away && !icon, // an icon cannot move on to another address
    iconHint: safari && (away || !offered),
    iconAwayHint: safari && offered && !away,
  };
}

/** Arranges the pairing screen (see pairingLayout). `awayUrl` is the away address, for the
 *  link in the hint that sends an iPhone there. */
export function arrangePairing(where, awayUrl = '') {
  const layout = pairingLayout(where);
  $('pairAnywhere').classList.toggle('hidden', !layout.anywhere);
  $('pairCertHelp').classList.toggle('hidden', !layout.certificateHelp);
  $('pairForwardNote').classList.toggle('hidden', !layout.certificateHelp);
  $('pairAwayRow').classList.toggle('hidden', !layout.awaySwitch);
  $('pairIconHint').classList.toggle('hidden', !layout.iconHint);
  $('pairIconAwayHint').classList.toggle('hidden', !layout.iconAwayHint);
  if (layout.iconAwayHint) {
    $('pairIconAwayLink').href = awayUrl;
    $('pairIconAwayLink').textContent = String(awayUrl).replace(/^https?:\/\//i, '');
  }
  return layout;
}

/** Sets up the form. `onPaired` runs after the PC accepted the code. */
export function initPairing({ onPaired }) {
  // A code handed over in the address goes into the form, and the address is cleaned at once
  // so the code does not stay in the address bar or the history. It is a fragment, so it was
  // never sent to the PC; the only place it goes from here is the pairing request below.
  const code = pairCodeFromHash(location.hash);
  if (code) {
    $('pairCode').value = code;
    history.replaceState(null, '', location.pathname);
  }
  $('deviceName').value = guessDeviceName(navigator.platform, navigator.maxTouchPoints);

  $('pairForm').addEventListener('submit', async (event) => {
    event.preventDefault();
    $('pairError').textContent = '';
    $('pairSubmit').disabled = true;
    try {
      await api('/api/v1/pair', { method: 'POST', body: JSON.stringify({ code: $('pairCode').value.trim(), label: $('deviceName').value }) });
      $('pairCode').value = '';
      await onPaired();
    } catch (error) {
      $('pairError').textContent = error.message;
    } finally {
      $('pairSubmit').disabled = false;
    }
  });
}

/** An already paired browser has no use for a code that came with the address. */
export function clearPairCode() { $('pairCode').value = ''; }
