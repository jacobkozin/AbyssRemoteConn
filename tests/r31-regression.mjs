// R3.1 focus checks. Run with `node tests/r31-regression.mjs`.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = p => fs.readFileSync(path.join(root, p), 'utf8');
const html=read('src/AbyssRemote.Host/wwwroot/index.html');
const js=read('src/AbyssRemote.Host/wwwroot/app.js');
const css=read('src/AbyssRemote.Host/wwwroot/style.css');
const host=read('src/AbyssRemote.Host/Program.cs');
const desktop=read('src/AbyssRemote.Host/DesktopEngine.cs');
const check=(name,fn)=>{fn(); console.log('PASS',name)};
check('Sharp-only web client: blurry choices removed',()=>{
  assert.doesNotMatch(html,/id="qualityPick"/);
  assert.match(html,/HIGH CLARITY/);
  assert.match(js,/quality=87/);
  assert.match(js,/Math\.max\(1920/);
  assert.match(host,/QueryInt\(ctx, "width", 1920, 480, 3200\)/);
  assert.match(desktop,/Math\.Clamp\(quality, 30, 90\)/);
});
check('Immersive bezel deliberately insets desktop below iPhone gesture edge',()=>{
  for (const id of ['leaveImmersive','immersiveKeyboard','immersiveRight','immersiveScrollUp','immersiveScrollDown']) {
    assert.ok(html.includes(`id="${id}"`),id);
  }
  assert.match(css,/--frame-top:calc\(64px \+ env\(safe-area-inset-top/);
  assert.match(css,/body\.immersive \.desktop-stage\{position:absolute/);
  assert.match(css,/cannot disable Notification Center/i);
  assert.match(js,/scheduleResolutionRefresh\(\)/);
});
check('Mobile keyboard drawer uses existing authenticated input path',()=>{
  for (const id of ['keyboardSheet','keyboardText','keyboardSend','closeKeyboard','keyboardTab','keyboardEnter','keyboardBackspace','showKeyboard'])
    assert.ok(html.includes(`id="${id}"`),id);
  assert.match(js,/\$\('keyboardText'\)\.focus\(\{ preventScroll: true \}\)/);
  assert.match(js,/if \(!send\('text', \{ text \}\)\)/);
  assert.match(js,/state\.control\?\.readyState !== WebSocket\.OPEN/);
  assert.match(desktop,/NativeInput\.TypeText\(text\)/);
  assert.match(js,/visualViewport\?\.addEventListener\('resize'/);
});
console.log('R3.1 feature checks passed; Windows and real iOS Safari testing are still required.');
