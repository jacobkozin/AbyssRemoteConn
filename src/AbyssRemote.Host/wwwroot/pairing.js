// The pairing screen. An eight-digit, one-time code from the Abyss Remote window on the PC
// turns this browser into a paired device.
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
