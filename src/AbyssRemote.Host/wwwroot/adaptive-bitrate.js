// Delivery includes decode time: reduce promptly under pressure, recover slowly.
// Idle screens do not provide enough samples to estimate network capacity.
export function adaptiveBitrate(initial = 20000) {
  let current = initial, good = 0, cooldown = 0;
  return {
    sample({ fps, deliverMs }) {
      if (!Number.isFinite(fps) || !Number.isFinite(deliverMs) || fps < 5 || deliverMs <= 0) return null;
      if (cooldown > 0) { cooldown--; return null; }
      let next = current;
      if (deliverMs > 80) { next = Math.max(2000, Math.floor(current * 0.75 / 500) * 500); good = 0; }
      else if (deliverMs < 35) { if (++good >= 8) { next = Math.min(35000, current + 1000); good = 0; } }
      else good = 0;
      if (next === current) return null;
      current = next; cooldown = 2;
      return current;
    },
  };
}
