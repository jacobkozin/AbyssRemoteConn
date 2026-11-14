// Rumble only targets an attached controller; unsupported browsers simply ignore it.
export function rumbleValues(message) {
  if (!Number.isInteger(message.index) || message.index < 0 || message.index > 3) return null;
  const clamp = value => Number.isFinite(value) ? Math.max(0, Math.min(1, value)) : 0;
  return { index: message.index, strong: clamp(message.strong), weak: clamp(message.weak) };
}
export function controllerRumble(allowed, pads = () => navigator.getGamepads?.() ?? []) {
  const levels = new Map();
  let timer = 0;
  function play(index, level) {
    const pad = pads().find(p => p?.connected && p.index === index);
    const actuator = pad?.vibrationActuator;
    try {
      if (!actuator) return;
      if (!level.strong && !level.weak) Promise.resolve(actuator.reset?.()).catch(() => {});
      else Promise.resolve(actuator.playEffect?.('dual-rumble', {
        startDelay: 0, duration: 400, strongMagnitude: level.strong, weakMagnitude: level.weak,
      })).catch(() => {});
    } catch { /* no haptics permission or support */ }
  }
  function reset() {
    clearInterval(timer); timer = 0;
    for (const index of levels.keys()) play(index, {strong:0,weak:0});
    levels.clear();
  }
  return {
    receive(message) {
      if (!allowed()) { reset(); return; }
      const value = rumbleValues(message);
      if (!value) return;
      if (value.strong || value.weak) levels.set(value.index, value);
      else levels.delete(value.index);
      play(value.index, value);
      if (!levels.size) { clearInterval(timer); timer = 0; }
      else if (!timer) timer = setInterval(() => {
        if (!allowed()) { reset(); return; }
        for (const [index, level] of levels) play(index, level);
      }, 250);
    },
    reset,
  };
}
