// Checks for away from home (away.js): run with `node tests/web/away-checks.mjs`.
import assert from 'node:assert/strict';
import { check, finish, loadModules, pretendPage } from './check.mjs';

const [{ normalizeAwayCode, awayCodeFromHash, awayPairCodeFromHash, formatAwayCode, takeAwayCodeFromAddress, initAwayView, goAway, awayPanelView, hostOf, isHomeScreenIcon, isIphoneOrIpad, CODE_LENGTH }] = await loadModules('away.js');

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

/** A pretend page at the away address, and a PC that records what it is sent and answers `reply`. */
function pageAndPc({ hash = '', reply = {} } = {}) {
  const page = pretendPage({ hash, platform: 'iPhone', touchPoints: 5 });
  page.sent = [];
  globalThis.fetch = async (url, options) => { page.sent.push([url, JSON.parse(options.body ?? 'null')]); return { ok: true, status: 200, json: async () => reply }; };
  return page;
}

await check('a name and the away code from the PC add this device', async () => {
  const page = pageAndPc();
  let ready = 0;
  initAwayView({ onReady() { ready++; } });
  assert.equal(page.elements.awayName.value, 'My iPhone', 'a first suggestion for the name');
  document.getElementById('awayCode').value = 'abcd-efgh-jklm';
  await page.elements.awayForm.listeners.submit({ preventDefault() {} });
  assert.deepEqual(page.sent, [['/api/v1/pair', { code: 'ABCDEFGHJKLM', label: 'My iPhone' }]]);
  assert.equal(ready, 1);
  assert.equal(page.elements.awayError.textContent, '');
});

check('the pairing code typed at the away address is explained, not sent', () => {
  const page = pageAndPc();
  initAwayView({ onReady() {} });
  document.getElementById('awayCode').value = ' 12345678 ';
  page.elements.awayForm.listeners.submit({ preventDefault() {} });
  assert.deepEqual(page.sent, []);
  assert.match(page.elements.awayError.textContent, /pairing code, which is for home/);
  assert.match(page.elements.awayError.textContent, /Show away code/);
});

check('the QR code in the PC\'s box fills in the code and leaves the address bar', () => {
  assert.equal(awayPairCodeFromHash('#pair=ABCDEFGHJKLM'), 'ABCDEFGHJKLM');
  assert.equal(awayPairCodeFromHash('#pair=12345678'), '', 'eight digits are a pairing code, for home');
  assert.equal(awayPairCodeFromHash('#away=ABCDEFGHJKLM'), '');
  const page = pageAndPc({ hash: '#pair=ABCDEFGHJKLM' });
  initAwayView({ onReady() {} });
  assert.equal(page.elements.awayCode.value, 'ABCD-EFGH-JKLM');
  assert.deepEqual(page.replaced, [[null, '', '/']]);
});

await check('a paired browser at home takes itself to the away address with a code of its own', async () => {
  const page = pageAndPc({ reply: { url: 'https://remote.example.org/#away=ABCDEFGHJKLM' } });
  const went = [];
  location.assign = (url) => went.push(url);
  await goAway();
  assert.deepEqual(page.sent, [['/api/v1/away/code', null]]);
  assert.deepEqual(went, ['https://remote.example.org/#away=ABCDEFGHJKLM']);
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

check('a browser paired at home gets one button, first on its home screen', () => {
  const view = awayPanelView(home());
  assert.equal(view.top, true);
  assert.equal(view.setup, true);
  assert.equal(view.later, true);
  assert.equal(view.copy, undefined);
  assert.match(text(view), /One tap/);
  assert.match(text(view), /remote\.example\.org/);
});

check('"Not now" moves the panel to the end, where the button stays', () => {
  const view = awayPanelView(home(), { later: true });
  assert.equal(view.top, false);
  assert.equal(view.setup, true);
  assert.equal(view.later, false);
});

check('an icon on an iPhone cannot take itself away, so it is told where to make one that can', () => {
  const view = awayPanelView(home(), { icon: true, apple: true });
  assert.equal(view.setup, undefined, 'no button that would open a sheet inside the icon');
  assert.equal(view.copy, true);
  assert.match(text(view), /This icon only works at home/);
  assert.match(text(view), /Show away code/);
});

check('once away access is on, the panel names the address and offers to open it', () => {
  const view = awayPanelView(home({ ready: true }));
  assert.equal(view.top, false);
  assert.equal(view.setup, undefined);
  assert.equal(view.open, true);
  assert.equal(awayPanelView(home({ ready: true }), { icon: true }).open, false);
});

check('right after signing in at the away address, the page says to use that address, once', () => {
  const there = home({ here: true, ready: true });
  const fresh = awayPanelView(there, { justSetUp: true });
  assert.equal(fresh.top, true);
  assert.equal(fresh.done, true);
  assert.match(text(fresh), /Use this address from now on: remote\.example\.org/);
  assert.doesNotMatch(text(fresh), /away code of its own/);
  assert.match(text(awayPanelView(there, { justSetUp: true, apple: true })), /an icon on the Home Screen asks for an away code of its own/);
  assert.match(text(awayPanelView(there, { justSetUp: true, icon: true, apple: true })), /Use this icon wherever you are/);

  const later = awayPanelView(there);
  assert.equal(later.top, false);
  assert.equal(later.done, undefined);
  assert.match(text(later), /works at home and away/);
});

finish('away');
