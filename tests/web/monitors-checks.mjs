// Checks for the monitor cards' logic: run with `node tests/web/monitors-checks.mjs`.
import assert from 'node:assert/strict';
import { check, finish, loadModules } from './check.mjs';

const [{ monitorLabel, monitorDetail, monitorAspect, previewWidth, pickMonitor, sameMonitors }] = await loadModules('monitors.js');

// The shape of GET /api/v1/displays. The main monitor is deliberately not number 0.
const side = { id: 0, name: '\\\\.\\DISPLAY1', x: 3840, y: 360, width: 2560, height: 1440, primary: false, friendlyName: 'Northlight 27 QHD', refreshHz: 144 };
const main = { id: 1, name: '\\\\.\\DISPLAY2', x: 0, y: 0, width: 3840, height: 2160, primary: true, friendlyName: 'Aurora 32 4K', refreshHz: 240 };
const upright = { id: 2, name: '\\\\.\\DISPLAY3', x: -1080, y: 0, width: 1080, height: 1920, primary: false, friendlyName: 'Monitor 3', refreshHz: 60 };
const all = [side, main, upright];

check('a card is titled with the monitor number and its model name', () => {
  assert.equal(monitorLabel(side), '1 · Northlight 27 QHD');
  assert.equal(monitorLabel(main), '2 · Aurora 32 4K');
});

check('a monitor without a model name is not numbered twice', () => {
  assert.equal(monitorLabel(upright), 'Monitor 3');
  assert.equal(monitorLabel({ ...side, friendlyName: '' }), 'Monitor 1');
  assert.equal(monitorLabel({ id: 4 }), 'Monitor 5');
  // A name that only looks like another monitor's number still gets its own.
  assert.equal(monitorLabel({ ...side, friendlyName: 'Monitor 3' }), '1 · Monitor 3');
});

check('the detail line shows size and refresh rate, and leaves out an unknown rate', () => {
  assert.equal(monitorDetail(side), '2560 × 1440 · 144 Hz');
  assert.equal(monitorDetail({ ...side, refreshHz: 0 }), '2560 × 1440');
  assert.equal(monitorDetail({ width: 1920, height: 1080 }), '1920 × 1080');
});

check('a card has its monitor\'s shape, within sane limits', () => {
  assert.equal(monitorAspect(main), 16 / 9);
  assert.equal(monitorAspect(upright), 1080 / 1920);
  assert.equal(monitorAspect({ width: 5120, height: 1440 }), 5120 / 1440);
  assert.equal(monitorAspect({ width: 10000, height: 100 }), 4);
  assert.equal(monitorAspect({ width: 100, height: 10000 }), 0.4);
  for (const odd of [{ width: 0, height: 0 }, { width: 1920, height: 0 }, { width: -5, height: 3 }, {}]) assert.equal(monitorAspect(odd), 16 / 9);
});

check('previews are asked for in steps of 160, never outside what the PC accepts', () => {
  assert.equal(previewWidth(143, 2), 320);
  assert.equal(previewWidth(160, 1), 160);
  assert.equal(previewWidth(161, 1), 320);
  assert.equal(previewWidth(300, 3), 960);
  assert.equal(previewWidth(900, 3), 960);
  assert.equal(previewWidth(40, 1), 160);
  assert.equal(previewWidth(200, 8), 640);  // a pixel ratio above 3 counts as 3
  assert.equal(previewWidth(200, 0.5), 320); // and below 1 as 1
  for (const hidden of [0, -10, NaN, undefined]) assert.equal(previewWidth(hidden, 2), 480);
  for (let width = 1; width < 1500; width += 7) {
    const asked = previewWidth(width, 2);
    assert.ok(asked >= 160 && asked <= 960 && asked % 160 === 0, `${width} -> ${asked}`);
  }
});

check('the remembered monitor is found by its device name, not its number', () => {
  assert.equal(pickMonitor(all, '\\\\.\\DISPLAY3'), upright);
  // Windows renumbered the monitors: the name still finds the same one.
  const renumbered = [{ ...upright, id: 0 }, { ...side, id: 1 }, { ...main, id: 2 }];
  assert.equal(pickMonitor(renumbered, '\\\\.\\DISPLAY1').id, 1);
});

check('without a remembered monitor, or when it is gone, the main one is used', () => {
  assert.equal(pickMonitor(all, null), main);
  assert.equal(pickMonitor(all, undefined), main);
  assert.equal(pickMonitor(all, '\\\\.\\DISPLAY9'), main);
  assert.equal(pickMonitor([side, upright], null), side); // no main monitor reported: the first
  assert.equal(pickMonitor([], '\\\\.\\DISPLAY1'), null);
  assert.equal(pickMonitor(undefined, null), null);
});

check('cards are only rebuilt when the monitors really changed', () => {
  assert.ok(sameMonitors(all, all.map((d) => ({ ...d }))));
  assert.ok(sameMonitors([], []));
  assert.ok(!sameMonitors(all, [side, main]));
  assert.ok(!sameMonitors(all, [main, side, upright]));
  assert.ok(!sameMonitors(all, [side, { ...main, refreshHz: 120 }, upright]));
  assert.ok(!sameMonitors(all, [side, { ...main, primary: false }, upright]));
  // The position on the PC's desk does not change a card.
  assert.ok(sameMonitors(all, [side, { ...main, x: 5 }, upright]));
});

finish('monitor');
