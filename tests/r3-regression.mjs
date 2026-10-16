// R3 regression checks: protocol, viewport, input and source handoff contract.
// Execute with `node tests/r3-regression.mjs`. No external test dependencies.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read=(f)=>fs.readFileSync(path.join(root,f),'utf8');
const host=read('src/AbyssRemote.Host/Program.cs');
const capture=read('src/AbyssRemote.Host/DesktopEngine.cs');
const client=read('src/AbyssRemote.Host/wwwroot/app.js');
const html=read('src/AbyssRemote.Host/wwwroot/index.html');
const css=read('src/AbyssRemote.Host/wwwroot/style.css');
const ignore=read('.gitignore');
function pass(label,fn){fn(); console.log('PASS',label)}
pass('Stream is bounded by client frame request (not blind server push)',()=>{
  assert.match(host,/socket\.ReceiveAsync\(new ArraySegment<byte>\(request\)/);
  assert.match(host,/message\.Count != 4/);
  assert.match(host,/WebSocketMessageType\.Binary, true/);
  assert.doesNotMatch(host,/desktop\.Snapshot\(/);
  assert.match(client,/state\.stream\.send\('next'\)/);
  assert.match(client,/requestFrame\(epoch\)/);
});
pass('Higher FPS cap is explicit and not promised',()=>{
 assert.match(host,/QueryInt\(ctx, "fps", 18, 1, 24\)/);
 assert.match(html,/<option value="24">24 FPS<\/option>/);
});
pass('Capture buffers are reusable and per-stream disposed',()=>{
 assert.match(capture,/class DesktopCaptureSession : IDisposable/);
 assert.match(capture,/_lastWidth != d\.Width/);
 assert.match(capture,/CopyFromScreen/);
 assert.match(host,/using var capture = desktop\.CreateCaptureSession/);
});
pass('Fit, Fill and Stretch settings are real UI behavior',()=>{
 for(const mode of ['fit','fill','stretch']) assert.match(html,new RegExp(`value="${mode}"`));
 assert.match(client,/Math\.max\(viewportW \/ iw, viewportH \/ ih\)/);
 assert.match(client,/Math\.min\(viewportW \/ iw, viewportH \/ ih\)/);
 assert.match(client,/getBoundingClientRect\(\)/);
 assert.match(client,/updateScreenLayout\(\)/);
});
pass('Mobile immersive mode has explicit CSS fallback and exit',()=>{
 assert.match(html,/id="immersive"/);
 assert.match(html,/id="leaveImmersive"/);
 assert.match(client,/document\.body\.classList\.toggle\('immersive'/);
 assert.match(css,/body\.immersive \.desktop-stage/);
 assert.match(css,/100dvh/);
});
pass('Touch clicks, long-press drag and two-finger panning exist',()=>{
 assert.match(client,/touches\.size > 1/);
 assert.match(client,/scroll\.scrollLeft/);
 assert.match(client,/holdTimer = setTimeout/);
 assert.match(client,/mouseClick\(\)/);
});
pass('No external CDN or analytics',()=>{
 assert.doesNotMatch(html+css+client,/(https?:)?\/\/(?:.*cdn|fonts\.googleapis|google-analytics|segment\.io)/i);
});
pass('Repository excludes builds, certificates, local state and legacy handoffs',()=>{
 for(const str of ['**/bin/','**/obj/','/dist/','settings.json','*.pfx','*.key','*.cer','/handoffs/','/handoff-baseline.json'])
  assert.ok(ignore.includes(str),str);
});
console.log('R3 regression checks passed. Native Windows + Safari field test still required.');
