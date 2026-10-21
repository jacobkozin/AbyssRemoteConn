// Desktop-mode video. The host answers each request with exactly one message: a JPEG frame,
// or a small JSON status ("idle" when the screen has not changed, "unavailable" while Windows
// blocks capture). The next frame is requested only after this one is drawn, so a slow
// device can never fall behind with a queue of stale frames.
//
// Every stream module returns { stop() } and reports through the same callbacks:
//   onFrame(image, { width, height, bytes })  draw it; the module closes the image afterwards
//   onStatus({ type, message })               idle / unavailable / error
//   onClose(code)                             the WebSocket closed (see CloseCodes on the host)

export function startJpegStream({ url, protocols, onFrame, onStatus, onClose }) {
  const socket = new WebSocket(url, protocols);
  socket.binaryType = 'blob';
  let stopped = false;
  const request = (verb) => { if (!stopped && socket.readyState === WebSocket.OPEN) socket.send(verb); };

  socket.onopen = () => request('full');
  socket.onmessage = async (event) => {
    if (typeof event.data === 'string') {
      let status = {};
      try { status = JSON.parse(event.data); } catch { /* ignore a malformed status */ }
      onStatus(status);
      request('next');
      return;
    }
    let image = null;
    try {
      image = await decode(event.data);
      if (!stopped) onFrame(image, { width: image.width, height: image.height, bytes: event.data.size });
      request('next');
    } catch (error) {
      onStatus({ type: 'error', message: `Could not show a frame (${error.message}).` });
      request('full');
    } finally {
      image?.close?.();
    }
  };
  socket.onclose = (event) => {
    if (stopped) return;
    stopped = true;
    onClose(event.code);
  };

  return {
    stop() {
      stopped = true;
      socket.onclose = null;
      try { socket.close(); } catch { /* already closed */ }
    },
  };
}

async function decode(blob) {
  if (typeof createImageBitmap === 'function') {
    try { return await createImageBitmap(blob); } catch { /* older Safari: fall back to <img> */ }
  }
  const url = URL.createObjectURL(blob);
  try {
    const image = new Image();
    await new Promise((resolve, reject) => {
      image.onload = resolve;
      image.onerror = () => reject(new Error('the image could not be decoded'));
      image.src = url;
    });
    return image;
  } finally {
    URL.revokeObjectURL(url);
  }
}
