// PC sound in the browser. The host sends 10 ms chunks of 48 kHz stereo PCM; they are handed
// to audio-worklet.js, which buffers and plays them on the audio thread.

let context = null;
let workletReady = null;

// Browsers (iOS Safari especially) only start audio after a tap or click, so this must be
// called from one. Later calls just resume a context the system paused.
export async function unlockAudio() {
  if (!context) {
    const AudioContextClass = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextClass || typeof AudioWorkletNode === 'undefined') throw new Error('This browser cannot play streamed sound.');
    try { context = new AudioContextClass({ sampleRate: 48000, latencyHint: 'interactive' }); }
    catch { context = new AudioContextClass({ latencyHint: 'interactive' }); } // the worklet resamples
    workletReady = context.audioWorklet.addModule('/audio-worklet.js');
  }
  if (context.state !== 'running') await context.resume();
  await workletReady;
  return context.state === 'running';
}

export const audioUnlocked = () => context?.state === 'running';

export function startAudio({ url, protocols, onClose, onStatus }) {
  const player = new AudioWorkletNode(context, 'abyss-pcm-player', { numberOfInputs: 0, outputChannelCount: [2] });
  player.connect(context.destination);
  player.port.onmessage = (event) => onStatus?.(event.data);
  const socket = new WebSocket(url, protocols);
  socket.binaryType = 'arraybuffer';
  let stopped = false;
  socket.onmessage = (event) => {
    if (typeof event.data === 'string') return; // the config message: always 48 kHz stereo s16le
    player.port.postMessage(new Int16Array(event.data), [event.data]);
  };
  socket.onclose = (event) => {
    if (stopped) return;
    stopped = true;
    player.disconnect();
    onClose(event.code, event.reason);
  };
  return {
    stop() {
      stopped = true;
      socket.onclose = null;
      try { socket.close(); } catch { /* already closed */ }
      player.disconnect();
    },
  };
}
