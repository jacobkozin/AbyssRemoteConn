# Architecture

Abyss Remote is one Windows process (C#, .NET 10, ASP.NET Core minimal APIs) that serves a
browser client (plain HTML, CSS and ES modules) over HTTPS and WebSockets. There is no cloud
service, no database and no third-party package.

## The big picture

```text
Browser                                     Host process
-------                                     ------------
app.js  (UI, pairing, input, reconnect)     Program.cs        startup, console keys (P / R / Q)
 |- jpeg-stream.js   Desktop mode  <--WSS-- AbyssServer.cs    Kestrel, middleware, all endpoints
 |- video-stream.js  Game mode     <--WSS--   |- JpegStream.cs      GDI capture -> JPEG
 |- audio.js + audio-worklet.js    <--WSS--   |- VideoStream.cs     credits/acks for Game mode
 |- gamepad.js                     --WSS-->   |- control socket     -> IInputSink (SendInput, ViGEmBus)
 '- fetch() REST                   --HTTPS->  '- REST               pairing, devices, clipboard
                                            Security.cs       pairing code, TLS certs, CSRF, origin, IP filter
                                            StateStore.cs     paired devices (hashes only)
                                            Sessions.cs       live sockets per device, close codes
                                            DesktopEngine.cs  Windows desktop: GDI, SendInput, cursor
                                            GameMode/         DXGI, D3D11, Media Foundation, WASAPI, ViGEmBus
```

`AbyssServer.Build(ServerContext)` builds the whole web app from its dependencies. Production
passes the Windows implementations; the tests pass fakes (`IDesktop`, `IClipboard`,
`IVideoSource`, `IAudioSource`) and a throwaway certificate. That seam is what makes the
security properties testable without touching the real mouse, keyboard or screen.

## Security layers, outside in

1. **Network:** loopback only by default. With `--lan`, a Kestrel connection filter refuses
   any peer that is not loopback, RFC 1918, link-local or IPv6 unique-local, before TLS starts.
   The Windows Firewall rule is also limited to the local subnet on Private networks.
2. **TLS 1.2/1.3** with a server certificate signed by a private root CA. Both live in the
   Windows user certificate store with non-exportable keys. The server certificate is reissued
   by the same root when it nears expiry or the LAN address changes, so phones keep trusting it.
3. **Host allowlist:** requests must name a host this PC really has (DNS-rebinding defense).
4. **Pairing:** an 8-digit code from a cryptographic RNG, shown only in the host console,
   valid 10 minutes and once. Limits: 5 tries per address per 5 minutes, and 20 wrong tries in
   total destroy the code.
5. **Session:** a random 256-bit secret in a `Secure; HttpOnly; SameSite=Strict` cookie. Only
   its SHA-256 hash is stored, and it is compared in constant time. It lasts 30 days.
6. **Request checks:** every state-changing request and every WebSocket needs the cookie, an
   exact `Origin` match and a CSRF token (an HMAC of the device id with a key that changes on
   every host start). Browsers cannot add custom headers to WebSockets, so the token travels
   as the second WebSocket subprotocol.
7. **Live revocation:** `SessionRegistry` tracks every socket by device; revoking a device
   closes them at once with close code 4001.
8. **Content Security Policy** `default-src 'none'` with scripts and styles only from the host
   itself; untrusted text (device names) is only ever set with `textContent`.

See [SECURITY.md](SECURITY.md) for the threat model and limitations.

## Desktop mode (JPEG)

`GdiFrameSource` copies one monitor with `BitBlt` into a reusable DIB section. It draws the
mouse cursor into it (`DrawIconEx`), compares the raw pixels with the previous frame, and only
encodes a JPEG (GDI+) when something changed.

The protocol keeps exactly one frame in flight: the browser sends `next` after it has drawn a
frame, and the host answers with one message, either a JPEG, `idle` after a second without
changes, or `unavailable` while Windows blocks capture (lock screen, UAC). A slow phone
therefore never builds a queue of stale frames.

Pacing counts from the start of each capture, so capture time overlaps the frame interval.
R3.3 waited a full interval after sending, which capped the dev PC at about 8 fps.

## Game mode (hardware H.264)

```text
DXGI Desktop Duplication -> CopyResource -> D3D11 video processor -> NV12 ring -> Media Foundation H.264 MFT -> AVCC -> WebSocket -> WebCodecs VideoDecoder -> canvas
        (GPU)                  (GPU)          (GPU: BGRA->NV12+scale)     (GPU)        (NVENC / AMD / Intel)     (CPU)                     (phone hardware decoder)
```

- **Capture:** `DesktopDuplication` finds the adapter that drives the chosen monitor, creates a
  Direct3D 11 device on it, and receives a GPU texture only when the screen changed. Mode
  changes, the lock screen and UAC make Windows report "access lost"; the stream shows a
  message and resumes by itself.
- **Conversion:** `VideoConverter` uses the GPU's fixed-function video processor to convert
  BGRA to NV12 and scale in one pass, as BT.709 limited range. The encoder writes that into
  the stream so browsers show the right colors.
- **Encoding:** `HardwareEncoder` drives an asynchronous Media Foundation transform on the same
  GPU. It runs in low-latency mode with CBR, no B-frames and keyframes on request. Hardware
  transforms are event driven: a dedicated thread handles "need input" and "have output".
- **Flow control:** `VideoStream` gives the encoder two credits. The browser sends
  `ack <frame id>` after decoding each frame, and only frames actually sent earn a credit back.
  The capture thread starts a new frame only when it has a credit, the encoder is ready, the
  frame-rate interval has passed and the screen changed, so latency cannot pile up.
- **Format:** encoders emit Annex B (start codes). The host converts it to AVCC
  (length-prefixed) and sends the `avcC` record once, because that is the form every
  WebCodecs implementation accepts, including Safari's.
- **Mouse pointer:** desktop duplication reports the pointer separately. The host sends its
  position and shape (as PNG), and the browser draws it on top of the video, so it stays sharp
  and moves without waiting for a new frame.
- **Timing:** Windows normally wakes threads in 15.6 ms steps, which turned a 60 fps target
  into about 32 fps. While a stream runs, the host requests 1 ms timer resolution
  (`timeBeginPeriod`).

The project has no binding library, so DirectX and Media Foundation are called through COM
vtable slots (`GameMode/Native.cs`). Each slot number, GUID, struct layout and constant was
taken from the Windows SDK headers and is commented with its interface and slot. It is the
same thing a C++ compiler does with those headers. `--probe-video` exercises the pipeline on
every monitor without a browser.

## Sound

`SystemAudioCapture` uses WASAPI loopback (what the speakers get). It folds surround to stereo,
resamples to 48 kHz and emits 10 ms chunks of 16-bit PCM. Silence becomes zero chunks, so the
browser can tell a quiet PC from a slow network, and a bounded queue drops old audio instead of
delaying new audio.

In the browser an AudioWorklet plays from a ring buffer that starts at about 40 ms, grows after
underruns (up to 160 ms) and skips ahead past 250 ms.

## Input and controllers

The control socket carries small JSON messages. `NativeInputSink`, one per connection, turns
them into `SendInput` calls and remembers everything it holds down (keys, mouse buttons,
virtual controllers). `release` (sent when the browser loses focus) and disconnecting both let
go of all of it.

Keyboard events use `KeyboardEvent.code` (a physical key) mapped to a PC/AT scan code, so the
PC's own keyboard layout decides the character, and games reading scan codes see WASD.
Controllers use the browser's Gamepad API "standard" layout. The host converts that to an
XInput report and feeds a virtual Xbox 360 pad through the ViGEmBus driver, called directly
with `DeviceIoControl`.

## Reliability

- The browser reconnects with backoff and fetches a fresh CSRF token first (the host issues
  new ones after a restart).
- WebSocket keepalive pings drop peers that stopped answering after about 25 s. A reconnect
  from the same device replaces its older socket (close code 4008) instead of being refused.
- Close codes tell the browser what happened: 4001 revoked, 4008 replaced, 4029 busy, 4415 not
  supported (Game mode falls back to Desktop mode), 1012 restart.
- Viewer settings live in `localStorage` (no secrets). Host state is
  `%LOCALAPPDATA%\AbyssRemote\settings.json`, written atomically.

## Testing

- `tests/AbyssRemote.Tests` (MSTest, 102 tests):
  - unit tests for pairing limits, expiry, cookie checks, CSRF, origin, host allowlist, private
    address filter, key and controller mapping, H.264/AVCC conversion and stream sizing;
  - integration tests that start the real HTTPS server with fakes and attack it from outside
    (no cookie, wrong origin, wrong CSRF, revoked device, oversized messages), plus the
    video/audio protocols and flow control.
- `tests/web/client-checks.mjs`: syntax of every browser module, element ids, imports, no
  inline scripts (CSP), no external URLs, version consistency.
- `AbyssRemote.Host.exe --smoke-test`: the published binary with its real certificate on a
  free port, checked from outside with certificate pinning.
- Manual: capture, pointer mapping on all monitors, Game mode at 60 fps, sound loopback,
  reconnect after a host restart, controller notice. Real iPhone/iPad testing is still open
  (see the roadmap).

## Design decisions worth explaining

- **Why a browser client?** Nothing to install on the phone, and one codebase for iPhone, iPad
  and computers. The cost is TCP transport and Safari's limits, which a native app (Moonlight)
  avoids.
- **Why two video modes?** JPEG works in every browser and keeps text sharp. H.264 needs
  WebCodecs and a hardware encoder, but on the dev PC it used about a quarter of the bandwidth
  at more than twice the frame rate.
- **Why acks instead of just streaming?** Over TCP, a frame the client is not ready for waits
  in a buffer and adds latency. Bounding frames in flight keeps the picture current.
- **Why no NuGet packages?** A remote-control program is security-sensitive; the trade-off is
  more interop code, which is kept in one place and documented against the SDK headers.
