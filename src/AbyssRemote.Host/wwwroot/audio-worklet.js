// Runs on the browser's audio thread and plays the PC's sound.
//
// PCM chunks (48 kHz stereo, 16-bit) arrive over the network a little unevenly, so they go into
// a small ring buffer first. Playback starts once about 40 ms is buffered. If the network runs
// dry, the target grows by 20 ms (up to 160 ms) so it happens less often; if the buffer ever
// exceeds 250 ms, the oldest audio is dropped so sound cannot drift behind the video.
const SOURCE_RATE = 48000;

class PcmPlayer extends AudioWorkletProcessor {
  constructor() {
    super();
    this.capacity = sampleRate * 2; // two seconds per channel
    this.left = new Float32Array(this.capacity);
    this.right = new Float32Array(this.capacity);
    this.read = 0;
    this.write = 0;
    this.size = 0;
    this.target = Math.round(0.04 * sampleRate);
    this.maximum = Math.round(0.25 * sampleRate);
    this.playing = false;
    // If the AudioContext could not run at 48 kHz, resample linearly on the way in. step is the
    // distance between output samples, measured in input samples (1 when both run at 48 kHz).
    this.step = SOURCE_RATE / sampleRate;
    this.phase = 0;
    this.lastLeft = 0;
    this.lastRight = 0;
    this.port.onmessage = (event) => this.push(event.data);
  }

  push(samples) { // Int16Array, interleaved left/right
    for (let i = 0; i + 1 < samples.length; i += 2) {
      const left = samples[i] / 32768, right = samples[i + 1] / 32768;
      while (this.phase < 1) {
        this.store(this.lastLeft + (left - this.lastLeft) * this.phase, this.lastRight + (right - this.lastRight) * this.phase);
        this.phase += this.step;
      }
      this.phase -= 1;
      this.lastLeft = left;
      this.lastRight = right;
    }
    if (this.size > this.maximum) { // far behind: skip ahead to the target latency
      const drop = this.size - this.target;
      this.read = (this.read + drop) % this.capacity;
      this.size -= drop;
    }
  }

  store(left, right) {
    this.left[this.write] = left;
    this.right[this.write] = right;
    this.write = (this.write + 1) % this.capacity;
    if (this.size < this.capacity) this.size++;
    else this.read = (this.read + 1) % this.capacity;
  }

  process(inputs, outputs) {
    const output = outputs[0];
    const left = output[0], right = output[1] || output[0];
    if (!this.playing && this.size >= this.target) this.playing = true;
    for (let i = 0; i < left.length; i++) {
      if (!this.playing || this.size === 0) {
        left[i] = 0;
        right[i] = 0;
        if (this.playing) { // ran dry mid-playback: buffer a little more from now on
          this.playing = false;
          this.target = Math.min(this.target + Math.round(0.02 * sampleRate), Math.round(0.16 * sampleRate));
          this.port.postMessage({ type: 'underrun', bufferMs: Math.round(1000 * this.target / sampleRate) });
        }
        continue;
      }
      left[i] = this.left[this.read];
      right[i] = this.right[this.read];
      this.read = (this.read + 1) % this.capacity;
      this.size--;
    }
    return true;
  }
}

registerProcessor('abyss-pcm-player', PcmPlayer);
