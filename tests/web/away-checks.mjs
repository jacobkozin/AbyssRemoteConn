// Checks for away from home (away.js): run with `node tests/web/away-checks.mjs`.
import assert from 'node:assert/strict';
import { check, finish, loadModules, pretendPage } from './check.mjs';

const [{ normalizeAwayCode, awayCodeFromHash, formatAwayCode, takeAwayCodeFromAddress, initAwayView, awayPanelView, hostOf, isHomeScreenIcon, isIphoneOrIpad, CODE_LENGTH }] = await loadModules('away.js');

check('a code is twelve letters and digits from the PC\'s alphabet, typed any way', () => {
  assert.equal(CODE_LENGTH, 12);
  assert.equal(normalizeAwayCode('ABCD-EFGH-JKLM'), 'ABCDEFGHJKLM');
  assert.equal(normalizeAwayCode(' abcd efgh jklm '), 'ABCDEFGHJKLM');
  assert.equal(normalizeAwayCode('abcd-efgh-jklm'), 'ABCDEFGHJKLM');
  assert.equal(formatAwayCode('abcdefghjklm'), 'ABCD-EFGH-JKLM');
});

check('anything else is not a code', () => {
  const notCodes = ['', 'ABCD-EFGH-JKL', 'ABCD-EFGH-JKLMN', 'ABCD-EFGH-JKL0', 'ABCD-EFGH-JKL1', 'OBCD-EFGH-JKLM', 'IBCD-EFGH-JKLM',
    'ABCD_EFGH_JKLM', 'ABCDÉFGHJKLM', null, undefined, 123456789012];
  for (const text of notCodes) assert.equal(normalizeAwayCode(text), '', JSON.stringify(text));
  assert.equal(formatAwayCode('nope'), '');
});

check('the link from the home screen hands its code over', () => {
  assert.equal(awayCodeFromHash('#away=ABCDEFGHJKLM'), 'ABCDEFGHJKLM');
  assert.equal(awayCodeFromHash('#away=ABCD-EFGH-JKLM'), 'ABCDEFGHJKLM');
  for (const hash of ['', '#', '#away=', '#away=ABC', '#pair=12345678', '#away=ABCDEFGHJKLM&x=1', ' #away=ABCDEFGHJKLM', '#AWAY=ABCDEFGHJKLM', null])
    assert.equal(awayCodeFromHash(hash), '', JSON.stringify(hash));
});

check('a code in the address leaves the address bar at once', () => {
  const page = pretendPage({ hash: '#away=ABCDEFGHJKLM' });
  assert.equal(takeAwayCodeFromAddress(), 'ABCDEFGHJKLM');
  assert.deepEqual(page.replaced, [[null, '', '/']]);
});

check('a broken away code in the address is still taken out, and other addresses are left alone', () => {
  let page = pretendPage({ hash: '#away=ABC' });
  assert.equal(takeAwayCodeFromAddress(), '');
  assert.deepEqual(page.replaced, [[null, '', '/']]);
  page = pretendPage({ hash: '#pair=12345678' });
  assert.equal(takeAwayCodeFromAddress(), '');
  assert.deepEqual(page.replaced, []);
});

check('the form explains a code that cannot be right instead of sending it', () => {
  const page = pretendPage();
  let sent = 0;
  globalThis.fetch = async () => { sent++; return { ok: true, status: 200, json: async () => ({}) }; };
  initAwayView({ onReady() {} });
  document.getElementById('awayCode').value = 'ABCD';
  // The check runs before the handler's first await, so its effects are visible at once.
  page.elements.awayForm.listeners.submit({ preventDefault() {} });
  assert.equal(sent, 0);
  assert.match(page.elements.awayError.textContent, /12 letters and digits/);
});

/** The pretend page with a clipboard holding `text`, and a PC that counts what it is sent. */
function pageWithClipboard(text) {
  const page = pretendPage();
  navigator.clipboard = { readText: async () => text };
  page.sent = [];
  globalThis.fetch = async (url, options) => { page.sent.push(JSON.parse(options.body)); return { ok: true, status: 200, json: async () => ({}) }; };
  return page;
}

await check('"Paste code" takes a copied code, however it was copied, and uses it at once', async () => {
  const page = pageWithClipboard(' abcd-efgh-jklm\n');
  let ready = 0;
  initAwayView({ onReady() { ready++; } });
  await page.elements.awayPaste.listeners.click();
  assert.deepEqual(page.sent, [{ code: 'ABCDEFGHJKLM' }]);
  assert.equal(ready, 1);
  assert.equal(page.elements.awayError.textContent, '');
});

await check('"Paste code" with something else on the clipboard sends nothing and says what to do', async () => {
  const page = pageWithClipboard('https://example.org/some/other/thing');
  initAwayView({ onReady() {} });
  await page.elements.awayPaste.listeners.click();
  assert.deepEqual(page.sent, []);
  assert.match(page.elements.awayError.textContent, /Copy it in Abyss Remote at home/);
});

check('the address is shown the way a person types it', () => {
  assert.equal(hostOf('https://remote.example.org'), 'remote.example.org');
  assert.equal(hostOf('https://remote.example.org/#away=ABCDEFGHJKLM'), 'remote.example.org');
  assert.equal(hostOf(undefined), '');
});

check('only an icon on an iPhone or iPad Home Screen counts as one', () => {
  assert.equal(isHomeScreenIcon({ standalone: true }), true);
  assert.equal(isHomeScreenIcon({ standalone: false }), false);
  assert.equal(isHomeScreenIcon({}), false, 'every other browser, also in full screen');
});

check('an iPhone or iPad is recognised in Safari too, where an icon is not', () => {
  assert.equal(isIphoneOrIpad({ platform: 'iPhone', maxTouchPoints: 5 }), true);
  assert.equal(isIphoneOrIpad({ platform: 'iPad', maxTouchPoints: 5 }), true);
  assert.equal(isIphoneOrIpad({ platform: 'MacIntel', maxTouchPoints: 5 }), true, 'Safari on an iPad says it is a Mac');
  assert.equal(isIphoneOrIpad({ platform: 'MacIntel', maxTouchPoints: 0 }), false, 'a real Mac');
  assert.equal(isIphoneOrIpad({ platform: 'Win32', maxTouchPoints: 10 }), false);
  assert.equal(isIphoneOrIpad({ platform: 'Linux armv8l', maxTouchPoints: 5 }), false, 'an Android phone');
  assert.equal(isIphoneOrIpad({}), false);
});

const text = (view) => view.text.map((piece) => (Array.isArray(piece) ? piece[0] : piece)).join('');
const home = (extra) => ({ available: true, url: 'https://remote.example.org', here: false, ready: false, ...extra });

check('the panel stays away while the PC has away access turned off', () => {
  assert.deepEqual(awayPanelView(null), { visible: false });
  assert.deepEqual(awayPanelView({ available: false, url: null, here: false, ready: false }), { visible: false });
});

check('a device that is not set up gets the panel first on the home screen, with one thing to tap', () => {
  const view = awayPanelView(home());
  assert.equal(view.top, true);
  assert.equal(view.setup, 'Set up away access');
  assert.equal(view.later, true);
  assert.match(text(view), /remote\.example\.org/);
  assert.match(text(view), /while you are at home/);
});

check('"Not now" moves the panel to the end, where it still offers the set-up', () => {
  const view = awayPanelView(home(), { later: true });
  assert.equal(view.top, false);
  assert.equal(view.setup, 'Set up away access');
  assert.equal(view.later, false);
});

check('a device that is set up is told which address to open, and can be set up again', () => {
  const view = awayPanelView(home({ ready: true }));
  assert.equal(view.top, false);
  assert.equal(view.setup, 'Set up again');
  assert.match(text(view), /open remote\.example\.org/);
});

check('right after the code was accepted, the page says what to do next, once', () => {
  const there = home({ here: true, ready: true });
  const fresh = awayPanelView(there, { justSetUp: true });
  assert.equal(fresh.top, true);
  assert.equal(fresh.done, true);
  assert.equal(fresh.setup, undefined, 'nothing can be set up at the away address');
  assert.match(text(fresh), /bookmarks/);
  assert.match(text(awayPanelView(there, { justSetUp: true, icon: true })), /Use this icon/);

  const later = awayPanelView(there);
  assert.equal(later.top, false);
  assert.equal(later.done, undefined);
  assert.match(text(later), /connected away from home/);
});

finish('away');
