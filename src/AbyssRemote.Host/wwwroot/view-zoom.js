// Arithmetic for the stream view: how far the picture may be zoomed, where to scroll so a
// point stays under a finger, how a zoomed-in view goes along with the pointer, and how
// touch movement becomes pointer movement and scrolling. Nothing here touches the page, so
// tests/web/view-checks.mjs runs it in Node.

// Windows enlarges text on dense monitors (150% is common at 4K). The host says by how much
// (`scale` of a monitor); this is the guess for a host that does not.
export function guessMonitorScale(width) {
  return width >= 3200 ? 1.5 : width >= 2400 ? 1.25 : 1;
}

// Zoom is a percentage of the size at which the picture fits the stage (`baseWidth`, in CSS
// pixels). `readable` is the zoom at which text is about as large as in a phone app: one
// pixel of this device's page per pixel Windows lays its text out on (a monitor pixel at
// 100% scaling, one and a half of them at 150%). Zooming stops at twice that, and at 400% at
// the least.
export function zoomRange(baseWidth, monitorWidth, monitorScale = 1) {
  const actual = 100 * monitorWidth / (Math.max(1, monitorScale) * Math.max(1, baseWidth));
  const readable = Math.max(100, Math.round(actual));
  return { min: 100, readable, max: Math.max(400, readable * 2) };
}
export function clampZoom(zoom, range) {
  return Number.isFinite(zoom) ? Math.max(range.min, Math.min(range.max, zoom)) : range.min;
}

// The scroll position that puts the picture point `fraction` (0 = left or top edge, 1 = right
// or bottom edge) `offset` pixels into the visible part. `start` and `size` say where the
// picture lies on the scrolled surface.
export function scrollFor(fraction, offset, start, size, surface, visible) {
  const wanted = start + fraction * size - offset;
  return Math.max(0, Math.min(Math.max(0, surface - visible), wanted));
}
// The reverse: which point of the picture is `offset` pixels into the visible part.
export function fractionAt(scroll, offset, start, size) {
  return size > 0 ? Math.max(0, Math.min(1, (scroll + offset - start) / size)) : 0.5;
}
// Scrolls only as far as needed to keep `pointer` (a position on the surface) at least
// `margin` pixels inside the visible part.
export function followScroll(scroll, pointer, visible, surface, margin) {
  const edge = Math.min(margin, visible / 2);
  let next = scroll;
  if (pointer < scroll + edge) next = pointer - edge;
  else if (pointer > scroll + visible - edge) next = pointer - visible + edge;
  return Math.max(0, Math.min(Math.max(0, surface - visible), next));
}

// Two fingers either pinch (the distance between them changes) or scroll (they travel
// together). Whichever passes 14 pixels first decides for the rest of the gesture; null
// means not yet.
export function twoFingerGesture(distanceChange, travel) {
  const spread = Math.abs(distanceChange);
  if (spread > 14 && spread >= travel) return 'pinch';
  if (travel > 14) return 'scroll';
  return null;
}

// Trackpad: the pointer crosses this device's screen 1.4 times as fast as the finger,
// whatever the zoom, so zooming in gives finer control. `scale` is how large one monitor
// pixel is drawn, in CSS pixels; the result is monitor pixels per pixel of finger travel.
export function trackpadGain(scale) {
  return Math.max(0.35, Math.min(40, 1.4 / Math.max(scale, 0.01)));
}
// Two-finger scrolling: the page on the PC moves about as far across this device's screen as
// the fingers do. One notch of a mouse wheel is 120 units and scrolls roughly 100 pixels at
// 100% scaling.
export function wheelUnits(fingerPixels, scale, monitorScale = 1) {
  return fingerPixels * 1.2 / (Math.max(scale, 0.01) * Math.max(1, monitorScale));
}
