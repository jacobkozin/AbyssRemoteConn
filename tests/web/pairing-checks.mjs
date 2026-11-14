// Checks for the pairing screen: run with `node tests/web/pairing-checks.mjs`.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { check, finish, loadModules, pretendPage, web } from './check.mjs';

const [{ pairCodeFromHash, guessDeviceName, initPairing }] = await loadModules('pairing.js');

check('a QR-code address hands over its eight digits', () => {
  assert.equal(pairCodeFromHash('#pair=12345678'), '12345678');
  assert.equal(pairCodeFromHash('#pair=00000000'), '00000000');
});

check('anything else in the address is not a code', () => {
  const notCodes = ['', '#', '#pair=', '#pair=1234567', '#pair=123456789', '#pair=1234567a', '#pair=12345678&x=1',
    '#PAIR=12345678', ' #pair=12345678', '#pair=12345678 ', '#pair=１２３４５６７８', 'pair=12345678', '#x#pair=12345678',
    '#pair=12345678\n', null, undefined, 12345678];
  for (const hash of notCodes) assert.equal(pairCodeFromHash(hash), '', JSON.stringify(hash));
});

check('a code in the address fills the form and leaves the address bar at once', () => {
  const page = pretendPage({ hash: '#pair=12345678' });
  initPairing({ onPaired() {} });
  assert.equal(page.elements.pairCode.value, '12345678');
  assert.deepEqual(page.replaced, [[null, '', '/']]);
});

check('an address without a code changes nothing', () => {
  for (const hash of ['', '#games', '#pair=123']) {
    const page = pretendPage({ hash });
    initPairing({ onPaired() {} });
    assert.equal(page.elements.pairCode?.value ?? '', '');
    assert.deepEqual(page.replaced, []);
  }
});

check('the code is never logged or stored by the pairing screen', () => {
  const source = fs.readFileSync(path.join(web, 'pairing.js'), 'utf8');
  assert.doesNotMatch(source, /console\.|localStorage|sessionStorage|document\.cookie/);
});

check('the suggested device name fits the device', () => {
  assert.equal(guessDeviceName('iPhone', 5), 'My iPhone');
  assert.equal(guessDeviceName('iPad', 5), 'My iPad');
  // A newer iPad says it is a Mac, but a Mac has no touch screen.
  assert.equal(guessDeviceName('MacIntel', 5), 'My iPad');
  assert.equal(guessDeviceName('MacIntel', 0), 'My Mac');
  assert.equal(guessDeviceName('Linux armv8l', 5), 'My Android device');
  assert.equal(guessDeviceName('Linux aarch64', 5), 'My Android device');
  assert.equal(guessDeviceName('Win32', 0), 'My Windows PC');
  assert.equal(guessDeviceName('Win32', 10), 'My Windows PC');
  assert.equal(guessDeviceName('Linux x86_64', 0), 'My Linux PC');
  assert.equal(guessDeviceName('', 0), 'My device');
  assert.equal(guessDeviceName(undefined), 'My device');
});

check('the suggested name is one the PC accepts (60 characters at most)', () => {
  const page = pretendPage({ platform: 'iPhone' });
  initPairing({ onPaired() {} });
  assert.equal(page.elements.deviceName.value, 'My iPhone');
  assert.ok(page.elements.deviceName.value.length <= 60);
});

finish('pairing');
