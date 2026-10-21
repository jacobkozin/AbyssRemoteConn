// Game mode video: H.264 from the PC's hardware encoder, decoded by the browser's own video
// decoder through WebCodecs (hardware-accelerated on phones and tablets). The host sends an
// avcC "description" once and then length-prefixed (AVCC) frames, the form every WebCodecs
// implementation accepts.
//
// Flow control: the host keeps at most two frames in flight and waits for "ack <id>" after the
// browser has decoded each one, so a slow device or a Wi-Fi hiccup can never build a backlog.
// Same callbacks as jpeg-stream.js, plus onConfig, onCursor, onCursorImage and onStats.

const PROBE_CODEC = 'avc1.64002a'; // H.264 High profile, level 4.2 (1080p at 60 fps)

export async function gameModeSupported() {
  if (typeof VideoDecoder === 'undefined' || typeof EncodedVideoChunk === 'undefined') return false;
  try {
    const result = await VideoDecoder.isConfigSupported({ codec: PROBE_CODEC });
    return result.supported === true;
  } catch {
    return false;
  }
}

export function startVideoStream({ url, protocols, onFrame, onStatus, onClose, onConfig, onCursor, onCursorImage, onStats }) {
  const socket = new WebSocket(url, protocols);
  socket.binaryType = 'arraybuffer';
  let stopped = false, decoder = null, config = null, needKeyframe = true;
  const inDecoder = new Set(); // frame ids handed to the decoder that have not come out yet

  const send = (text) => { if (!stopped && socket.readyState === WebSocket.OPEN) socket.send(text); };
  const ack = (id) => send(`ack ${id}`);

  function closeDecoder() {
    if (decoder && decoder.state !== 'closed') { try { decoder.close(); } catch { /* already closed */ } }
    decoder = null;
  }

  // Starts a fresh decoder. Frames still inside the old one will never come out, so their
  // credits are handed back; after an error the host is asked for a keyframe to restart from.
  function resetDecoder(requestKeyframe, reason) {
    for (const id of inDecoder) ack(id);
    inDecoder.clear();
    closeDecoder();
    needKeyframe = true;
    if (!config || stopped) return;
    decoder = new VideoDecoder({
      output: (frame) => {
        const id = frame.timestamp;
        inDecoder.delete(id);
        try { if (!stopped) onFrame(frame, { width: frame.displayWidth, height: frame.displayHeight }); }
        finally { frame.close(); ack(id); }
      },
      error: (error) => resetDecoder(true, `Video decoder error (${error.message}). Recovering…`),
    });
    decoder.configure({ codec: config.codec, description: config.description, optimizeForLatency: true, hardwareAcceleration: 'prefer-hardware' });
    if (requestKeyframe) send('key');
    if (reason) onStatus({ type: 'error', message: reason });
  }

  socket.onmessage = (event) => {
    if (typeof event.data === 'string') {
      let message;
      try { message = JSON.parse(event.data); } catch { return; }
      switch (message.type) {
        case 'config':
          config = { ...message, description: base64ToBytes(message.description) };
          onConfig?.(config);
          resetDecoder(false); // the host always starts with a keyframe
          break;
        case 'cursor': onCursor?.(message); break;
        case 'cursorImage': onCursorImage?.(message); break;
        case 'stats': onStats?.(message); break;
        default: onStatus(message); // unavailable, resumed, restart, error
      }
      return;
    }
    const view = new DataView(event.data);
    if (event.data.byteLength < 12 || view.getUint8(0) !== 1) return;
    const keyframe = (view.getUint8(1) & 1) === 1;
    const id = view.getUint32(4, true);
    if (!decoder || (needKeyframe && !keyframe)) { ack(id); return; } // cannot decode until a keyframe
    needKeyframe = false;
    inDecoder.add(id);
    try {
      decoder.decode(new EncodedVideoChunk({ type: keyframe ? 'key' : 'delta', timestamp: id, data: new Uint8Array(event.data, 12) }));
    } catch (error) {
      resetDecoder(true, `Could not decode video (${error.message}). Recovering…`);
    }
  };
  socket.onclose = (event) => {
    if (stopped) return;
    stopped = true;
    closeDecoder();
    onClose(event.code, event.reason);
  };

  return {
    stop() {
      stopped = true;
      socket.onclose = null;
      try { socket.close(); } catch { /* already closed */ }
      closeDecoder();
    },
  };
}

function base64ToBytes(text) {
  const binary = atob(text || '');
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}
