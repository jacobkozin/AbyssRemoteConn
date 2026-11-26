// Checks for the pairing screen: run with `node tests/web/pairing-checks.mjs`.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { check, finish, loadModules, pretendPage, web } from './check.mjs';

const [{ pairCodeFromHash, guessDeviceName, initPairing, pairingLayout, arrangePairing }] = await loadModules('pairing.js');

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

check('at home, without an away address, the screen is the plain one', () => {
  assert.deepEqual(pairingLayout(), { anywhere: false, certificateHelp: true, awaySwitch: false, iconHint: false, iconAwayHint: false });
});

check('at home, with an away address that works, the switch is offered', () => {
  const layout = pairingLayout({ offered: true });
  assert.equal(layout.awaySwitch, true);
  assert.equal(layout.certificateHelp, true, 'the home address still needs its certificate');
});

check('at the away address there is no certificate to trust and nowhere to move on to', () => {
  const layout = pairingLayout({ away: true, offered: true });
  assert.equal(layout.anywhere, true);
  assert.equal(layout.certificateHelp, false);
  assert.equal(layout.awaySwitch, false);
});

check('Safari on an iPhone is told to make the icon first, from the address that works everywhere', () => {
  assert.equal(pairingLayout({ away: true, offered: true, apple: true }).iconHint, true, 'at the away address: from this page');
  assert.equal(pairingLayout({ apple: true }).iconHint, true, 'at home without an away address: from this page');
  const home = pairingLayout({ offered: true, apple: true });
  assert.equal(home.iconHint, false);
  assert.equal(home.iconAwayHint, true, 'at home with one: from the away address');
  assert.equal(pairingLayout({ offered: true }).iconAwayHint, false, 'other devices are not bothered with it');
});

check('an icon on the Home Screen gets no hint about icons and no switch it could not follow', () => {
  for (const where of [{}, { offered: true }, { away: true, offered: true }]) {
    const layout = pairingLayout({ ...where, apple: true, icon: true });
    assert.equal(layout.iconHint, false);
    assert.equal(layout.iconAwayHint, false);
    assert.equal(layout.awaySwitch, false);
  }
});

check('the screen shows and hides its parts to match', () => {
  const hidden = (page, id) => page.elements[id].classList.contains('hidden');
  let page = pretendPage();
  arrangePairing({ offered: true });
  assert.equal(hidden(page, 'pairAwayRow'), false);
  assert.equal(hidden(page, 'pairCertHelp'), false);
  assert.equal(hidden(page, 'pairAnywhere'), true);
  assert.equal(hidden(page, 'pairIconHint'), true);
  assert.equal(hidden(page, 'pairIconAwayHint'), true);

  page = pretendPage();
  arrangePairing({ away: true, offered: true, apple: true });
  assert.equal(hidden(page, 'pairAwayRow'), true);
  assert.equal(hidden(page, 'pairCertHelp'), true);
  assert.equal(hidden(page, 'pairForwardNote'), true);
  assert.equal(hidden(page, 'pairAnywhere'), false);
  assert.equal(hidden(page, 'pairIconHint'), false);
});

check('the hint for an iPhone at home links to the away address by name', () => {
  const page = pretendPage({ platform: 'iPhone', touchPoints: 5 });
  arrangePairing({ offered: true, apple: true }, 'https://remote.example.org');
  assert.equal(page.elements.pairIconAwayLink.href, 'https://remote.example.org');
  assert.equal(page.elements.pairIconAwayLink.textContent, 'remote.example.org');
});

check('the switch for away from home is on unless someone turns it off', () => {
  const html = fs.readFileSync(path.join(web, 'index.html'), 'utf8');
  assert.match(html, /<input id="pairAway" type="checkbox" checked>/);
});

finish('pairing');
