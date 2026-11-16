import assert from 'node:assert/strict';
import { loadModules, check, finish } from './check.mjs';
const [{ PointerMotion, movementQueue, cursorPictureReady }] = await loadModules('pointer-motion.js');
const monitor = { id: 2, x: -1920, y: 375, width: 1920, height: 1080 };
check('local movement appears before the host replies; old replies cannot pull it back', () => {
  const p = new PointerMotion();
  p.receive({ sequence: 0, x: -1700, y: 600, visible: true });
  const command = p.relative(30, -10, monitor);
  assert.equal(p.x, -1670); assert.equal(p.y, 590);
  assert.equal(p.receive({ sequence: 0, x: -1700, y: 600, visible: true }), false);
  assert.equal(p.x, -1670);
  p.sent();
  assert.equal(p.receive({ sequence: command.sequence, x: -1671, y: 589, visible: true }), true);
  assert.equal(p.x, -1671);
});
check('fractional trackpad movement accumulates and stays on the selected monitor', () => {
  const p = new PointerMotion(); p.absolute(50, 100, monitor);
  for (let i = 0; i < 100; i++) p.relative(0.25, 0.5, monitor);
  assert.equal(p.x, -1845); assert.equal(p.y, 525);
  const edge = p.relative(1e6, -1e6, monitor);
  assert.equal(p.x, -1); assert.equal(p.y, 375);
  assert.equal(edge.x, 1919/1920); assert.equal(edge.y, 0);
});
check('switching monitors seeds a trackpad inside that screen without jumping offscreen', () => {
  const p = new PointerMotion(); p.receive({ sequence: 0, x: 100, y: 100, visible: true });
  p.relative(10, 0, monitor);
  assert.equal(p.x, -950); assert.equal(p.y, 915);
});
check('cancelling an unsent move accepts fresh host state again; reconnect resets sequence', () => {
  const p = new PointerMotion(); p.absolute(10, 10, monitor); p.sent();
  p.absolute(40, 40, monitor); p.cancel();
  assert.equal(p.visible, false);
  assert.equal(p.receive({ sequence: 1, x: -1910, y: 385, visible: true }), true);
  p.reset(); assert.equal(p.sequence, 0); assert.equal(p.known, false);
});
function queue() {
  const tasks = new Map(), sent = []; let next = 0;
  const q = movementQueue((kind, data) => sent.push({kind, ...data}),
    fn => { tasks.set(++next, fn); return next; }, id => tasks.delete(id));
  return { q, sent, tasks, tick() { for (const fn of [...tasks.values()]) fn(); } };
}
check('240 Hz input coalesces to display frames without dropping relative travel', () => {
  const {q,sent,tick,tasks} = queue();
  for (let i=0;i<240;i++) { q.push('move',{dx:0.25,dy:0.5}); if(i%4===3) tick(); }
  assert.equal(sent.reduce((s,m)=>s+m.dx,0),60);
  assert.equal(sent.reduce((s,m)=>s+m.dy,0),120);
  assert.equal(sent.length,60); assert.equal(tasks.size,0);
});
check('absolute moves send only the latest point; lift flushes it before clicking', () => {
  const {q,sent,tasks} = queue();
  q.push('pointer',{x:0.1,sequence:1}); q.push('pointer',{x:0.9,sequence:2});
  assert.equal(tasks.size,1); q.flush();
  sent.push({kind:'mouse'});
  assert.deepEqual(sent,[{kind:'pointer',x:0.9,sequence:2},{kind:'mouse'}]);
  assert.equal(tasks.size,0);
});
check('gesture cancellation discards unsent input and fractional remainder', () => {
  const {q,sent,tick} = queue(); q.push('move',{dx:0.3,dy:0}); tick();
  q.push('move',{dx:5,dy:8}); q.cancel(); tick();
  assert.equal(sent.length,0);
  q.push('move',{dx:0.3,dy:0}); tick(); assert.equal(sent.length,0);
});
check('the video keeps its pointer until a cursor picture can really be drawn', () => {
  const png = 'data:image/png;base64,iVBORw0KGgo=';
  assert.equal(cursorPictureReady({ width: 32, height: 32 }, png), true);
  assert.equal(cursorPictureReady({ width: 0, height: 0 }, png), false);       // nothing received yet
  assert.equal(cursorPictureReady({ width: 32, height: 32 }, null), false);    // no picture in the page
  assert.equal(cursorPictureReady({ width: 32, height: 32 }, ''), false);
  assert.equal(cursorPictureReady({ width: 32, height: 32 }, 'data:image/png;base64,'), false);
});
finish('cursor motion');
