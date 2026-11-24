// Checks for away from home (away.js): run with `node tests/web/away-checks.mjs`.
import assert from 'node:assert/strict';
import { check, finish, loadModules, pretendPage } from './check.mjs';

const [{ normalizeAwayCode, awayCodeFromHash, formatAwayCode, takeAwayCodeFromAddress, initAwayView, CODE_LENGTH }] = await loadModules('away.js');

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

finish('away');
