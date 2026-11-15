// Cursor coordinates are Windows physical pixels, independent of video resolution.
// Predict only positions we actually send. Old host snapshots must never pull a moving
// finger backwards; a sequence echo lets us accept the first up-to-date correction.
export class PointerMotion {
  constructor() { this.reset(); }
  reset() { this.x = 0; this.y = 0; this.visible = false; this.sequence = 0; this.sentSequence = 0; this.known = false; }
  receive(snapshot) {
    if (snapshot.sequence < this.sequence) return false;
    this.x = snapshot.x; this.y = snapshot.y; this.visible = snapshot.visible;
    this.known = true;
    return true;
  }
  absolute(x, y, monitor) {
    this.x = monitor.x + Math.max(0, Math.min(monitor.width - 1, x));
    this.y = monitor.y + Math.max(0, Math.min(monitor.height - 1, y));
    this.visible = true; this.known = true; this.sequence++;
    return this.command(monitor);
  }
  relative(dx, dy, monitor) {
    // A phone controls its selected monitor. Start at its centre if the PC cursor is
    // currently on a different screen; do not move it merely by selecting a monitor.
    const inside = this.known && this.x >= monitor.x && this.x < monitor.x + monitor.width &&
      this.y >= monitor.y && this.y < monitor.y + monitor.height;
    return this.absolute((inside ? this.x - monitor.x : monitor.width / 2) + dx,
      (inside ? this.y - monitor.y : monitor.height / 2) + dy, monitor);
  }
  command(monitor) {
    return { x: (this.x - monitor.x) / monitor.width, y: (this.y - monitor.y) / monitor.height,
      display: monitor.id, sequence: this.sequence };
  }
  sent() { this.sentSequence = this.sequence; }
  cancel() { this.sequence = this.sentSequence; this.visible = false; }
}

// Coalesce to the latest absolute position, or accumulate ALL relative deltas. Never
// discard movement just because an event arrived within the current display frame.
export function movementQueue(send, schedule = requestAnimationFrame, cancel = cancelAnimationFrame) {
  let pending = null, frame = null, remainderX = 0, remainderY = 0;
  function flush() {
    if (frame !== null) cancel(frame);
    frame = null;
    if (!pending) return;
    const next = pending; pending = null;
    if (next.kind === 'move') {
      const x = next.data.dx + remainderX, y = next.data.dy + remainderY;
      next.data.dx = Math.round(x); next.data.dy = Math.round(y);
      remainderX = x - next.data.dx; remainderY = y - next.data.dy;
      if (!next.data.dx && !next.data.dy) return;
    }
    send(next.kind, next.data);
  }
  return {
    push(kind, data) {
      if (kind === 'move' && pending?.kind === 'move') {
        pending.data.dx += data.dx; pending.data.dy += data.dy;
      } else pending = { kind, data: { ...data } };
      if (frame === null) frame = schedule(flush);
    },
    flush,
    cancel() { if (frame !== null) cancel(frame); frame = null; pending = null; remainderX = remainderY = 0; },
  };
}
