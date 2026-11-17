// Touch state belongs to the stream, not a physical controller. Cancellation always releases it.
export const neutralPad = () => ({ buttons: 0, lt: 0, rt: 0, lx: 0, ly: 0, rx: 0, ry: 0 });
export function stickAt(x, y, rect) {
  const radius = Math.max(1, Math.min(rect.width, rect.height) / 2);
  let dx = (x - rect.left - rect.width / 2) / radius, dy = (y - rect.top - rect.height / 2) / radius;
  const length = Math.hypot(dx, dy);
  if (length < 0.12) return { x: 0, y: 0 };
  if (length > 1) { dx /= length; dy /= length; }
  return { x: Math.round(dx * 512) / 512, y: Math.round(dy * 512) / 512 };
}
export function pinchZoom(startZoom, startDistance, distance, max = 250) {
  if (startDistance <= 0 || !Number.isFinite(distance)) return startZoom;
  return Math.round(Math.max(100, Math.min(max, startZoom * distance / startDistance)));
}
// A shortcut such as "ControlLeft+ShiftLeft+Escape" becomes key presses in the order a hand
// would make them: every key down from left to right, then up again from right to left.
export function shortcutSteps(shortcut) {
  const codes = String(shortcut).split('+').filter(Boolean);
  return [...codes.map(code => ({ code, action: 'down' })), ...codes.reverse().map(code => ({ code, action: 'up' }))];
}
export function installTouchPad(panel, send, allowed) {
  let state = neutralPad(), pending = 0;
  const held = new Map();
  function emit() { pending = 0; if (allowed()) send('gamepad', { index: 0, ...state }); }
  function schedule() { if (!pending) pending = requestAnimationFrame(emit); }
  function reset() {
    if (pending) cancelAnimationFrame(pending);
    pending = 0;
    for (const [id] of held) for (const button of panel.querySelectorAll('[data-pad-button]'))
      if (button.hasPointerCapture(id)) button.releasePointerCapture(id);
    held.clear(); state = neutralPad();
    for (const button of panel.querySelectorAll('button')) button.classList.remove('held');
    for (const stick of panel.querySelectorAll('[data-stick]')) { stick.style.setProperty('--x', '0'); stick.style.setProperty('--y', '0'); }
    if (allowed()) emit();
  }
  for (const button of panel.querySelectorAll('[data-pad-button]')) {
    const bit = Number(button.dataset.padButton);
    const up = (event) => {
      held.delete(event.pointerId);
      if (![...held.values()].includes(bit)) {
        state.buttons &= ~(1 << bit);
        if (bit === 6) state.lt = 0;
        if (bit === 7) state.rt = 0;
        button.classList.remove('held'); schedule();
      }
    };
    button.addEventListener('pointerdown', (event) => {
      if (!allowed()) return;
      event.preventDefault(); button.setPointerCapture(event.pointerId);
      held.set(event.pointerId, bit); state.buttons |= 1 << bit;
      if (bit === 6) state.lt = 1;
      if (bit === 7) state.rt = 1;
      button.classList.add('held'); schedule();
    });
    for (const name of ['pointerup', 'pointercancel', 'lostpointercapture']) button.addEventListener(name, up);
  }
  for (const stick of panel.querySelectorAll('[data-stick]')) {
    const side = stick.dataset.stick; let pointer = null;
    const move = (event) => {
      if (pointer !== event.pointerId || !allowed()) return;
      const position = stickAt(event.clientX, event.clientY, stick.getBoundingClientRect());
      state[side + 'x'] = position.x; state[side + 'y'] = position.y; schedule();
      // The knob follows the thumb (style.css reads these two numbers).
      stick.style.setProperty('--x', String(position.x)); stick.style.setProperty('--y', String(position.y));
    };
    stick.addEventListener('pointerdown', (event) => {
      if (!allowed() || pointer !== null) return;
      event.preventDefault(); pointer = event.pointerId; stick.setPointerCapture(pointer); move(event);
    });
    stick.addEventListener('pointermove', move);
    for (const name of ['pointerup', 'pointercancel', 'lostpointercapture']) stick.addEventListener(name, (event) => {
      if (pointer !== event.pointerId) return;
      pointer = null; state[side + 'x'] = state[side + 'y'] = 0; schedule();
      stick.style.setProperty('--x', '0'); stick.style.setProperty('--y', '0');
    });
  }
  return { reset };
}
