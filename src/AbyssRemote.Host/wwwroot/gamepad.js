// Controllers paired with this phone, tablet or computer. The Gamepad API is polled about 125
// times a second while a controller is connected, and each change is sent to the PC, which
// drives a virtual Xbox 360 controller (this needs the free ViGEmBus driver on the PC).
//
// State uses the browser's "standard" layout: a bit per button (A, B, X, Y, LB, RB, LT, RT,
// Back, Start, LS, RS, D-pad up/down/left/right, Guide), triggers 0..1, sticks -1..1.

const POLL_MS = 8;

export function watchGamepads({ send, onChange }) {
  const last = new Map();
  let timer = 0;

  function snapshot(pad) {
    let buttons = 0;
    for (let i = 0; i < Math.min(pad.buttons.length, 17); i++) if (pad.buttons[i].pressed) buttons |= 1 << i;
    // Quantizing hides sensor noise, so a resting stick does not send a stream of tiny changes.
    const q = (value) => Math.round((value || 0) * 512) / 512;
    return { buttons, lt: q(pad.buttons[6]?.value), rt: q(pad.buttons[7]?.value),
             lx: q(pad.axes[0]), ly: q(pad.axes[1]), rx: q(pad.axes[2]), ry: q(pad.axes[3]) };
  }

  function poll() {
    for (const pad of navigator.getGamepads?.() ?? []) {
      if (!pad?.connected) continue;
      const index = Math.min(pad.index, 3);
      const state = snapshot(pad);
      const previous = last.get(index);
      if (previous && Object.keys(state).every((k) => state[k] === previous[k])) continue;
      if (send('gamepad', { index, ...state })) last.set(index, state);
    }
  }

  function update() {
    const pads = (navigator.getGamepads?.() ?? []).filter((p) => p?.connected);
    if (pads.length && !timer && !document.hidden) timer = setInterval(poll, POLL_MS);
    if ((!pads.length || document.hidden) && timer) { clearInterval(timer); timer = 0; }
    onChange(pads.map((p) => p.id));
  }

  const disconnected = (event) => {
    // Let go of everything on the PC for a controller that just went away.
    const index = Math.min(event.gamepad.index, 3);
    send('gamepad', { index, buttons: 0, lt: 0, rt: 0, lx: 0, ly: 0, rx: 0, ry: 0 });
    last.delete(index);
    update();
  };
  window.addEventListener('gamepadconnected', update);
  window.addEventListener('gamepaddisconnected', disconnected);
  document.addEventListener('visibilitychange', update);
  update();

  return {
    // After a reconnect the host has a fresh virtual controller: resend the current state.
    resync() { last.clear(); },
  };
}
