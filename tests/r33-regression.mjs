// R3.3 focus checks. Run with `node tests/r33-regression.mjs`.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const html = read('src/AbyssRemote.Host/wwwroot/index.html');
const js = read('src/AbyssRemote.Host/wwwroot/app.js');
const css = read('src/AbyssRemote.Host/wwwroot/style.css');
const host = read('src/AbyssRemote.Host/Program.cs');
const stateStore = read('src/AbyssRemote.Host/StateStore.cs');
const project = read('src/AbyssRemote.Host/AbyssRemote.Host.csproj');
const check = (name, fn) => { fn(); console.log('PASS', name); };

check('Release identity is consistently R3.3 / 0.3.3', () => {
  assert.match(host, /version = "0\.3\.3"/);
  assert.match(host, /ABYSS REMOTE R3\.3/);
  assert.match(html, /R3\.3 · 0\.3\.3/);
  assert.match(html, /app\.js\?v=0\.3\.3/);
  assert.match(html, /style\.css\?v=0\.3\.3/);
  assert.match(project, /<Version>0\.3\.3<\/Version>/);
});

check('Fit remains the uncropped default until the user changes it', () => {
  assert.match(js, /fitUserSet: false/);
  assert.match(js, /if \(!state\.fitUserSet\) \$\('fitPick'\)\.value = 'fit'/);
  assert.match(js, /state\.fitUserSet = true; syncQuickControls\(\); updateScreenLayout\(\)/);
  assert.match(html, /<option value="fit">Fit whole screen<\/option>/);
});

check('Touch gestures cover click, right-click, drag, scroll, and safe cancellation', () => {
  assert.match(js, /rightHoldTimer = setTimeout/);
  assert.match(js, /twoFingerStart = Date\.now\(\)/);
  assert.match(js, /dragReady && dragButton === null/);
  assert.match(js, /send\('wheel', \{ delta:/);
  assert.match(js, /desktop\.addEventListener\('pointercancel'/);
  assert.match(js, /window\.addEventListener\('blur', resetTouchGesture\)/);
  assert.match(html, /Tap with two fingers or hold one finger still to right-click/);
});

check('Immersive desktop respects device safe areas', () => {
  assert.match(css, /R3\.3: Comfortable immersive desktop/);
  assert.match(css, /top:calc\(19px \+ env\(safe-area-inset-top/);
  assert.match(css, /right:calc\(15px \+ env\(safe-area-inset-right/);
  assert.match(css, /bottom:calc\(19px \+ env\(safe-area-inset-bottom/);
  assert.match(css, /left:calc\(15px \+ env\(safe-area-inset-left/);
});

check('Active WebSockets re-check revocation and 30-day expiry after receiving data', () => {
  assert.match(stateStore, /StillPaired\(string id\)/);
  assert.match(stateStore, /CreatedUtc >= DateTimeOffset\.UtcNow\.AddDays\(-30\)/);
  assert.ok((host.match(/if \(!state\.StillPaired\(device\.Id\)\) break;/g) || []).length >= 4);
});

console.log('R3.3 source checks passed; real Windows and iOS/iPadOS testing are still required.');
