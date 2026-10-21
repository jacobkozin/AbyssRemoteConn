# Roadmap to v1.0

Abyss Remote started as a private-LAN remote desktop (R1 to R3.3, version 0.3.3). Version 1.0
has two goals:

1. **A remote desktop that is safe and dependable** on a home network.
2. **A Game mode good enough to play PC games from an iPhone or iPad**, in the spirit of
   Steam Link: hardware video encoding, 60 fps, game audio and controller support.

Each item below is checked only after it works on a real Windows 11 PC. Items marked
_(device)_ also need testing on a real iPhone or iPad, which this checklist cannot claim on
its own.

## Pairing and sessions

- [ ] One-time 8-digit pairing code shown only on the host console, valid for 10 minutes, single use
- [ ] Brute-force limits: 5 tries per address per 5 minutes, and the code is destroyed after 20 wrong tries in total
- [ ] Session cookie is `Secure`, `HttpOnly`, `SameSite=Strict`; only a SHA-256 hash is stored; 30-day lifetime; expired devices are pruned
- [ ] Revoking a device (or pressing `R` on the host) disconnects its live video, audio and input immediately
- [ ] Exact `Origin` check and a per-device CSRF token on every state-changing request and every WebSocket
- [ ] `Host` header allowlist, so a DNS-rebinding page cannot talk to the host

## Network exposure and TLS

- [ ] Loopback-only by default; LAN mode is an explicit choice
- [ ] In LAN mode the host refuses connections from public IP addresses, even if a router port is forwarded by mistake
- [ ] Configurable port; the default moves off 8443, which collided with another local HTTPS server on this PC
- [ ] Local root CA and server certificate in the Windows user certificate store; private keys are never exported
- [ ] The server certificate renews itself when it is close to expiry or the PC's LAN address changes (the phone keeps trusting the same root)

## Screen streaming

- [ ] Desktop mode (JPEG): correct frame pacing (R3.3 delivered about 8 fps on this PC), unchanged frames are skipped, mouse cursor is visible
- [ ] Game mode: GPU capture (DXGI Desktop Duplication), hardware H.264 through Windows Media Foundation (NVENC on NVIDIA), WebCodecs decoding in the browser
- [ ] Game mode targets 60 fps with a cap on frames in flight so latency cannot pile up; live stats for fps, bitrate, encode time and round trip
- [ ] A locked screen or UAC prompt shows a clear message instead of a reconnect loop
- [ ] Browsers without WebCodecs, or PCs without a hardware encoder, fall back to Desktop mode

## Input

- [ ] Mouse (absolute and trackpad-style), wheel including smooth trackpad scrolling, keyboard with correct extended keys
- [ ] No stuck keys or mouse buttons when the connection drops or the browser loses focus
- [ ] Text entry from phone keyboards, including line breaks and tabs
- [ ] Controllers: Gamepad API in the browser drives a virtual Xbox 360 pad on the PC (requires the ViGEmBus driver, installed by the user) _(device)_

## Audio

- [ ] PC audio (WASAPI loopback) plays in the browser with a small jitter buffer _(device)_

## Monitors, clipboard and UI

- [ ] Any monitor can be selected; pointer mapping is correct with mixed DPI scaling
- [ ] Text clipboard both ways, reachable from the phone controls panel
- [ ] Phone and tablet layout, touch gestures and immersive view _(device)_
- [ ] "Add to Home Screen" web app for real full screen on iPhone; Fullscreen API on iPad and desktop browsers _(device)_

## Reconnect, settings and errors

- [ ] Automatic reconnect with backoff; picks up a fresh CSRF token after the host restarts (R3.3 never reconnected input after a restart)
- [ ] Dead connections are detected by a WebSocket keepalive timeout; a reconnect replaces the old session instead of failing with "too many sessions"
- [ ] Viewer settings are remembered per browser
- [ ] Clear messages for: port already in use, damaged settings file, too many sessions, desktop unavailable, missing controller driver, browser without WebCodecs

## Build, tests and docs

- [ ] `BUILD-AND-TEST.bat` builds a clean checkout, runs unit and integration tests, publishes, and smoke-tests the real HTTPS host
- [ ] Integration tests prove no endpoint accepts input or shows the screen without pairing, origin and CSRF checks
- [ ] README, ARCHITECTURE, SECURITY and API docs describe what exists, including honest limitations

## Not planned for v1.0

- Access from outside the home network (relay, NAT traversal, port forwarding)
- Multiple users or roles, MFA, signed installer, auto-update
- HDR, H.265/AV1, UDP or WebRTC transport, on-screen touch gamepad
