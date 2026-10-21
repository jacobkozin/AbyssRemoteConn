# Roadmap to v1.0

Abyss Remote started as a private-LAN remote desktop (R1 to R3.3, version 0.3.3). Version 1.0
has two goals:

1. **A remote desktop that is safe and dependable** on a home network.
2. **A Game mode good enough to play PC games from an iPhone or iPad**, in the spirit of
   Steam Link: hardware video encoding, 60 fps, game audio and controller support.

An item is checked when it works on the development PC (Windows 11, RTX 4080, three monitors
with mixed DPI scaling, Chromium-based browser). The note says how it was verified. Items
marked _(device)_ still need a real iPhone or iPad, and that has not happened yet.

## Pairing and sessions

- [x] One-time 8-digit pairing code shown only on the host console, valid 10 minutes, single use. *Unit and integration tests; paired live in the browser.*
- [x] Brute-force limits: 5 tries per address per 5 minutes, and the code is destroyed after 20 wrong tries in total. *Unit tests.*
- [x] Session cookie is `Secure`, `HttpOnly`, `SameSite=Strict`; only a SHA-256 hash is stored; 30-day lifetime; expired devices are pruned. *Unit and integration tests.*
- [x] Revoking a device (or pressing `R` on the host) disconnects its live video, audio and input immediately. *Integration test (close code 4001); live sign-out check.*
- [x] Exact `Origin` check and a per-device CSRF token on every state-changing request and every WebSocket. *Integration tests attack each endpoint without them.*
- [x] `Host` header allowlist, so a DNS-rebinding page cannot talk to the host. *Integration test and smoke test.*

## Network exposure and TLS

- [x] Loopback-only by default; LAN mode is an explicit choice. *Smoke test, live.*
- [x] In LAN mode the host refuses connections from public IP addresses, even if a router port is forwarded by mistake. *Unit tests of the address filter. LAN mode was not run on the dev PC, because that needs a firewall change Jacob makes himself.*
- [x] Configurable port; the default moves off 8443, which collided with another local HTTPS server on this PC. *Live: 8444 works while 8443 is taken.*
- [x] Local root CA and server certificate in the Windows user certificate store; private keys are never exported. *Live, reusing the existing root.*
- [x] The server certificate renews itself when it is close to expiry or the PC's LAN address changes. *Coverage check unit-tested; the renewal itself was not triggered live.*

## Screen streaming

- [x] Desktop mode (JPEG): correct frame pacing, unchanged frames skipped, mouse cursor visible. *Live: R3.3 managed about 8 fps; 1.0 gets 26–27 fps at the 30 fps setting.*
- [x] Game mode: GPU capture (DXGI Desktop Duplication), hardware H.264 through Windows Media Foundation (NVENC here), WebCodecs decoding in the browser. *Live on all three monitors; `--probe-video`.*
- [x] Game mode reaches 60 fps with at most two frames in flight, plus live stats. *Live: 60 fps at 1080p, about 2 ms encode, about 1.4 ms network+decode on localhost; flow control integration-tested.*
- [x] A locked screen or UAC prompt shows a message instead of a reconnect loop. *Protocol tested with fakes; not triggered live (the PC was in use).*
- [x] Browsers without WebCodecs, or PCs without a hardware encoder, fall back to Desktop mode. *Integration test (4415) and client logic.*

## Input

- [x] Mouse (absolute and trackpad-style), smooth wheel, keyboard with correct extended keys. *Live: pointer mapping exact on all monitors; a remote click focused a test window and Shift+A, 1, Space, B arrived as "A1 b"; key mapping unit-tested.*
- [x] No stuck keys or buttons when the connection drops or the browser loses focus. *Integration test; client sends `release` on blur.*
- [x] Text entry from phone keyboards, including line breaks and tabs. *Live: line break, tab, accented letters and an emoji arrived byte-for-byte in a test window.*
- [ ] Controllers: Gamepad API in the browser drives a virtual Xbox 360 pad on the PC. *Implemented; mapping and driver IOCTLs unit-tested; the browser-to-host path and the "install ViGEmBus" notice were checked live with a simulated pad. Needs the ViGEmBus driver and a real controller to finish.* _(device)_

## Audio

- [x] PC audio (WASAPI loopback) plays in the browser with a small jitter buffer. *Live: a −60 dBFS 1 kHz test tone was captured back at exactly 1000 Hz; 100 chunks/s reach the AudioWorklet.* Listening on a phone is still _(device)_.

## Monitors, clipboard and UI

- [x] Any monitor can be selected; pointer mapping is correct with mixed DPI scaling. *Live, all three monitors.*
- [x] Text clipboard both ways, reachable from the phone controls panel. *Integration tests; live read.*
- [ ] Phone and tablet layout, touch gestures and immersive view. *Layout measured in an emulated 375×812 phone, portrait and landscape; the R3.3 panel clipping is fixed. Touch gestures are unchanged from R3.3, which Jacob tested.* _(device)_
- [ ] "Add to Home Screen" web app for full screen on iPhone; Fullscreen API on iPad and computers. *Manifest and icons served correctly.* _(device)_

## Reconnect, settings and errors

- [x] Automatic reconnect with backoff, picking up a fresh CSRF token after the host restarts. *Live (R3.3 never got input back).*
- [x] Dead connections are detected by a WebSocket keepalive timeout; a reconnect replaces the old session instead of failing with "too many sessions". *Integration test; seen live with two tabs.*
- [x] Viewer settings are remembered per browser.
- [x] Clear messages for: port already in use, damaged settings file, too many sessions, desktop unavailable, missing controller driver, browser without WebCodecs. *Unit and integration tests; live for port, driver and fallback.*

## Build, tests and docs

- [x] `BUILD-AND-TEST.bat` builds a fresh clone, runs 102 unit and integration tests plus the browser checks, publishes, and smoke-tests the real HTTPS host.
- [x] Integration tests prove no endpoint accepts input, shows the screen, streams sound or starts an encoder without pairing, origin and CSRF checks.
- [x] README, ARCHITECTURE, SECURITY and API docs describe what exists, including honest limitations.

## Next (after 1.0)

- Test on Jacob's iPhone and iPad: pairing and root trust, touch, Game mode decode, sound,
  controller, Home Screen app.
- Controller rumble (ViGEmBus notifications to the Gamepad API's vibration actuator).
- Name constraints on newly created root certificates; code signing.
- HEVC/AV1 where the device decodes them; adaptive bitrate from the delivery-time stats.

## Not planned

- Access from outside the home network (relay, NAT traversal, port forwarding).
- Multiple users or roles, MFA, signed installer, auto-update.
- HDR, UDP or WebRTC transport, on-screen touch gamepad.
