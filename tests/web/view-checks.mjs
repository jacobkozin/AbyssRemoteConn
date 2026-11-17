// Checks for the stream view's arithmetic (view-zoom.js): run with `node tests/web/view-checks.mjs`.
import assert from 'node:assert/strict';
import { loadModules, check, finish } from './check.mjs';
const [{ clampZoom, followScroll, fractionAt, guessMonitorScale, scrollFor, trackpadGain, twoFingerGesture, wheelUnits, zoomRange }, { pinchZoom }] =
  await loadModules('view-zoom.js', 'touch-controls.js');

check('zoom reaches the size at which text is readable, and twice that', () => {
  // A 4K monitor at 150% on a phone held upright: the picture fits in 390 CSS pixels.
  const phone = zoomRange(390, 3840, 1.5);
  assert.equal(phone.min, 100);
  assert.equal(phone.readable, 656); // 2560 layout pixels across 390 CSS pixels
  assert.equal(phone.max, 1312);
  // A window on a computer that already shows the monitor larger than life.
  const computer = zoomRange(2000, 1920, 1);
  assert.equal(computer.readable, 100);
  assert.equal(computer.max, 400);
  // A pinch can now go past the old fixed 250%.
  assert.equal(pinchZoom(100, 100, 900, phone.max), 900);
  assert.equal(pinchZoom(100, 100, 5000, phone.max), 1312);
});

check('zoom stays inside its range and survives a bad number', () => {
  const range = { min: 100, readable: 300, max: 600 };
  assert.equal(clampZoom(50, range), 100);
  assert.equal(clampZoom(450.5, range), 450.5);
  assert.equal(clampZoom(9000, range), 600);
  assert.equal(clampZoom(NaN, range), 100);
});

check('a monitor that does not say its scale gets a sensible guess', () => {
  assert.equal(guessMonitorScale(3840), 1.5);
  assert.equal(guessMonitorScale(2560), 1.25);
  assert.equal(guessMonitorScale(1920), 1);
});

check('the point under a finger stays under it when the picture grows', () => {
  // Stage 400 wide. The picture is 400 wide, then 1600 wide; the finger is 300 px in.
  const before = fractionAt(0, 300, 0, 400);
  assert.equal(before, 0.75);
  const scroll = scrollFor(before, 300, 0, 1600, 1600, 400);
  assert.equal(scroll, 900);
  assert.equal(fractionAt(scroll, 300, 0, 1600), 0.75);
});

check('scrolling stops at the edges of the picture', () => {
  assert.equal(scrollFor(0, 200, 0, 1600, 1600, 400), 0);       // cannot show what is left of the left edge
  assert.equal(scrollFor(1, 200, 0, 1600, 1600, 400), 1200);    // nor right of the right edge
  assert.equal(scrollFor(0.5, 200, 50, 300, 400, 400), 0);      // a picture smaller than the stage does not scroll
  assert.equal(fractionAt(0, 10, 50, 300), 0);                  // beside the picture counts as its edge
  assert.equal(fractionAt(0, 200, 0, 0), 0.5);
});

check('the view follows the pointer only when it gets near an edge', () => {
  // Visible part 400 wide of a 1600 surface, scrolled to 600: it shows 600 to 1000.
  assert.equal(followScroll(600, 800, 400, 1600, 80), 600);     // in the middle: nothing moves
  assert.equal(followScroll(600, 980, 400, 1600, 80), 660);     // 20 px from the right edge: scroll right
  assert.equal(followScroll(600, 620, 400, 1600, 80), 540);     // 20 px from the left edge: scroll left
  assert.equal(followScroll(600, 1590, 400, 1600, 80), 1200);   // never past the end
  assert.equal(followScroll(0, 10, 400, 1600, 80), 0);          // nor before the start
  assert.equal(followScroll(0, 150, 100, 1600, 80), 100);       // a margin wider than half the view is halved
});

check('two fingers pinch or scroll, and the first clear movement decides', () => {
  assert.equal(twoFingerGesture(5, 6), null);
  assert.equal(twoFingerGesture(30, 10), 'pinch');
  assert.equal(twoFingerGesture(-30, 10), 'pinch');
  assert.equal(twoFingerGesture(6, 40), 'scroll');
  assert.equal(twoFingerGesture(20, 40), 'scroll'); // fingers drifting apart while scrolling
});

check('the trackpad is faster when zoomed out and finer when zoomed in', () => {
  assert.ok(Math.abs(trackpadGain(0.1) - 14) < 1e-9);  // a 4K monitor fitted on a phone
  assert.ok(Math.abs(trackpadGain(1) - 1.4) < 1e-9);
  assert.equal(trackpadGain(10), 0.35);                // never slower than this
  assert.equal(trackpadGain(0), 40);                   // never faster than this
});

check('two-finger scrolling moves the page about as far as the fingers', () => {
  // 100 px of finger travel at 1:1 on a 100% monitor is 120 wheel units, one notch.
  assert.ok(Math.abs(wheelUnits(100, 1, 1) - 120) < 1e-9);
  // Zoomed out to a tenth, the same travel covers ten times as much of the page.
  assert.ok(Math.abs(wheelUnits(100, 0.1, 1) - 1200) < 1e-9);
  // At 150% scaling a notch scrolls one and a half times as many monitor pixels.
  assert.ok(Math.abs(wheelUnits(100, 1, 1.5) - 80) < 1e-9);
  assert.ok(wheelUnits(-50, 1, 1) < 0);
});

finish('stream view');
