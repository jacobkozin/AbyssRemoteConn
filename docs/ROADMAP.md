# Roadmap

Abyss Remote started as a private-LAN remote desktop (R1 to R3.3, version 0.3.3). Version 1.0
made it safe and added Game mode. Version 1.1 turns the host into a Windows app and gives the
browser a home screen with monitors, Steam games and files.

An item is checked when it works on the development PC (Windows 11, RTX 4080, three monitors
with mixed DPI scaling, Chromium-based browser). The note says how it was verified. Items
marked _(device)_ still need a real iPhone or iPad, and that has not happened yet.

## Version 1.0

Version 1.0 has two goals:

1. **A remote desktop that is safe and dependable** on a home network.
2. **A Game mode good enough to play PC games from an iPhone or iPad**, in the spirit of
   Steam Link: hardware video encoding, 60 fps, game audio and controller support.

### Pairing and sessions

- [x] One-time 8-digit pairing code shown only on the host console, valid 10 minutes, single use. *Unit and integration tests; paired live in the browser.*
- [x] Brute-force limits: 5 tries per address per 5 minutes, and the code is destroyed after 20 wrong tries in total. *Unit tests.*
- [x] Session cookie is `Secure`, `HttpOnly`, `SameSite=Strict`; only a SHA-256 hash is stored; 30-day lifetime; expired devices are pruned. *Unit and integration tests.*
- [x] Revoking a device (or pressing `R` on the host) disconnects its live video, audio and input immediately. *Integration test (close code 4001); live sign-out check.*
- [x] Exact `Origin` check and a per-device CSRF token on every state-changing request and every WebSocket. *Integration tests attack each endpoint without them.*
- [x] `Host` header allowlist, so a DNS-rebinding page cannot talk to the host. *Integration test and smoke test.*

### Network exposure and TLS

- [x] Loopback-only by default; LAN mode is an explicit choice. *Smoke test, live.*
- [x] In LAN mode the host refuses connections from public IP addresses, even if a router port is forwarded by mistake. *Unit tests of the address filter. LAN mode was not run on the dev PC, because that needs a firewall change Jacob makes himself.*
- [x] Configurable port; the default moves off 8443, which collided with another local HTTPS server on this PC. *Live: 8444 works while 8443 is taken.*
- [x] Local root CA and server certificate in the Windows user certificate store; private keys are never exported. *Live, reusing the existing root.*
- [x] The server certificate renews itself when it is close to expiry or the PC's LAN address changes. *Coverage check unit-tested; the renewal itself was not triggered live.*

### Screen streaming

- [x] Desktop mode (JPEG): correct frame pacing, unchanged frames skipped, mouse cursor visible. *Live: R3.3 managed about 8 fps; 1.0 gets 26–27 fps at the 30 fps setting.*
- [x] Game mode: GPU capture (DXGI Desktop Duplication), hardware H.264 through Windows Media Foundation (NVENC here), WebCodecs decoding in the browser. *Live on all three monitors; `--probe-video`.*
- [x] Game mode reaches 60 fps with at most two frames in flight, plus live stats. *Live: 60 fps at 1080p, about 2 ms encode, about 1.4 ms network+decode on localhost; flow control integration-tested.*
- [x] A locked screen or UAC prompt shows a message instead of a reconnect loop. *Protocol tested with fakes; not triggered live (the PC was in use).*
- [x] Browsers without WebCodecs, or PCs without a hardware encoder, fall back to Desktop mode. *Integration test (4415) and client logic.*

### Input

- [x] Mouse (absolute and trackpad-style), smooth wheel, keyboard with correct extended keys. *Live: pointer mapping exact on all monitors; a remote click focused a test window and Shift+A, 1, Space, B arrived as "A1 b"; key mapping unit-tested.*
- [x] No stuck keys or buttons when the connection drops or the browser loses focus. *Integration test; client sends `release` on blur.*
- [x] Text entry from phone keyboards, including line breaks and tabs. *Live: line break, tab, accented letters and an emoji arrived byte-for-byte in a test window.*
- [ ] Controllers: Gamepad API in the browser drives a virtual Xbox 360 pad on the PC. *Implemented; mapping and driver IOCTLs unit-tested; the browser-to-host path and the "install ViGEmBus" notice were checked live with a simulated pad. Needs the ViGEmBus driver and a real controller to finish.* _(device)_

### Audio

- [x] PC audio (WASAPI loopback) plays in the browser with a small jitter buffer. *Live: a −60 dBFS 1 kHz test tone was captured back at exactly 1000 Hz; 100 chunks/s reach the AudioWorklet.* Listening on a phone is still _(device)_.

### Monitors, clipboard and UI

- [x] Any monitor can be selected; pointer mapping is correct with mixed DPI scaling. *Live, all three monitors.*
- [x] Text clipboard both ways, reachable from the phone controls panel. *Integration tests; live read.*
- [ ] Phone and tablet layout, touch gestures and immersive view. *Layout measured in an emulated 375×812 phone, portrait and landscape; the R3.3 panel clipping is fixed. Touch gestures are unchanged from R3.3, which Jacob tested.* _(device)_
- [ ] "Add to Home Screen" web app for full screen on iPhone; Fullscreen API on iPad and computers. *Manifest and icons served correctly.* _(device)_

### Reconnect, settings and errors

- [x] Automatic reconnect with backoff, picking up a fresh CSRF token after the host restarts. *Live (R3.3 never got input back).*
- [x] Dead connections are detected by a WebSocket keepalive timeout; a reconnect replaces the old session instead of failing with "too many sessions". *Integration test; seen live with two tabs.*
- [x] Viewer settings are remembered per browser.
- [x] Clear messages for: port already in use, damaged settings file, too many sessions, desktop unavailable, missing controller driver, browser without WebCodecs. *Unit and integration tests; live for port, driver and fallback.*

### Build, tests and docs

- [x] `BUILD-AND-TEST.bat` builds a fresh clone, runs 102 unit and integration tests plus the browser checks, publishes, and smoke-tests the real HTTPS host.
- [x] Integration tests prove no endpoint accepts input, shows the screen, streams sound or starts an encoder without pairing, origin and CSRF checks.
- [x] README, ARCHITECTURE, SECURITY and API docs describe what exists, including honest limitations.

## Version 1.1

Checked on 2026-11-14 on the same PC. The whole suite: 414 tests pass and 2 are skipped (one
needs the Windows right to create symbolic links, one is the demo server, which only runs
when asked), and the eight browser check files pass. "Live" below means the real host on
loopback, used from a Chromium browser on the same PC with Watch only on. In this list an
item also stays open when its real effect on the PC has never been run.

### The Windows app

- [x] A window instead of a console: the address, the pairing code with its remaining time and **New code**, paired devices with **Revoke** and **Revoke all**, a "Phone setup" checklist, settings and an activity list. *Layout tests at 100, 125, 150 and 200% scaling; off-screen renders at 100% and 150% were looked at; one short real launch showed the window at the right size.*
- [x] A tray icon that keeps the app running when the window is closed; starting the program again shows the running window. *The real launch: the close button hid the window while the server kept running, and a second start brought the window back. The tray menu was only drawn off screen, not clicked.*
- [x] Saved settings: home-network mode, port, "Start with Windows", close-to-tray. New options `--local`, `--console` and `--minimized`. *Unit tests (settings file, a fake registry for "Start with Windows") and runtime tests that restart with new options. The switches were not clicked in the real window.*
- [ ] QR pairing: in home-network mode the window shows a QR code that opens the address with the pairing code filled in. *The encoder is tested against published Reed-Solomon vectors and read back by a separate reader in the tests. Live: pairing through a `#pair=` link. No phone has scanned the code.* _(device)_
- [ ] The root certificate can be downloaded from the host, and the window shows its fingerprint. *Integration tests and the self-test: the download is the public certificate only. Safari's handling of the download is untested.* _(device)_
- [x] The app icon, drawn by `tools/make_icon.ps1` as the Windows `.ico` and the web-app icons. *Seen in the window's title bar in the real launch and on the pairing screen.*

### Monitors

- [x] Monitor picker: every monitor with its model name, resolution, refresh rate and a live preview, on the home screen and inside a stream. *Live: the three real monitors with names, refresh rates and previews. Integration tests and Node checks for the rest.*
- [x] Monitor numbers are fixed: the primary monitor is 0, the others follow from left to right. The browser remembers a monitor by its Windows name. *A test reads the real monitors and checks the order.*
- [x] Screenshot: save a full-resolution picture of a monitor. *Tests captured the real monitors through the same code as full-size JPEGs. The download was checked in Chromium against the demo server. Saving to Photos on a phone is untested.*
- [x] Watch only: see and hear the PC without sending input. *Chromium against the demo server: after the switch, taps, keys and buttons sent nothing. The live checks of 1.1 ran with it on.*

### Steam, files and the PC

- [x] Steam library: the installed games with cover art and a filter. *Live: every installed game appeared with its cover. The reader and its filters are unit-tested on made-up folders.*
- [ ] Start a Steam game, and open or close Big Picture. *Tests stop at the exact program and argument that would be started (`steam.exe` with one `steam://` argument, no shell), and forged requests are refused. Nothing has been started for real.*
- [x] File transfer through `Downloads\Abyss Remote`: send, list, download, delete. *Live: a file was uploaded, listed, downloaded back byte for byte and deleted, and a delete without the CSRF header was refused (403). Tests attack the real web server with about 90 spellings of path traversal and about 80 unsafe names.*
- [ ] Lock the PC or put it to sleep. *The endpoints are tested with a stand-in, including seven forged forms of each request. The two Windows calls have never been run.*

### Game mode and controllers

- [x] Game mode through the new client. *Live: 60 fps, about 3 ms encode, under 1 ms network+decode on localhost.*
- [x] The bitrate can change while streaming, and **Automatic (2–35 Mbps)** is the default. *Live on the hardware encoder (NVENC) with a raw video socket at 1080p60: asked for 20 Mbps, measured 21.3–22.0; after `bitrate 30000`, 29.1–29.5; after `bitrate 4000` with a game-like scrolling picture, 2.3 (easy pictures need less than the target). A picture that is new noise in every frame cannot go below about 10 Mbps, whatever is asked. The rule that decides when to change is checked in Node; it has not been watched on real Wi-Fi.*
- [ ] Controller rumble: vibration from the game reaches the physical controller. *The layout and code of the driver request are unit-tested, and the browser side is checked in Node. It has never run against the driver: ViGEmBus is not installed on the development PC.* _(device)_
- [ ] On-screen gamepad ("Touch gamepad"): two sticks, D-pad, A/B/X/Y, bumpers, triggers, View, Guide, Menu and stick clicks. *In a phone-sized Chromium against the demo server, A held while the left stick moves arrived at the host correctly. Not tried on a touch screen, and its effect in a game needs ViGEmBus.* _(device)_

### Input and the browser client

- [x] Home screen after pairing: monitors, games, files, This PC, This device. *Chromium against the demo server at four window sizes, and live.*
- [ ] Special keys and one-tap shortcuts in the keyboard sheet. *In a phone-sized Chromium against the demo server, the keys of the shortcuts arrived at the host in the right order. Not pressed against the real desktop.*
- [ ] Pinch zoom with two fingers (100–250%). *The arithmetic is checked in Node. The gesture has not been tried on a touch screen.* _(device)_
- [ ] Mouse and keyboard through the reworked client. *Verified live for 1.0. After the rework they were only checked in Chromium against the demo server.*

### Build, tests and docs

- [x] The program is `AbyssRemote.exe`. The default build makes a small program that runs on the installed .NET 10 runtime and downloads nothing. *Built on the development PC with no package source available; the published program passed its HTTPS self-test.*
- [x] The GitHub Actions workflow with the standalone build. *Passed on GitHub for the 1.1.0 commit: the tests, the standalone build (`-SelfContained`) and its HTTPS self-test on a clean Windows runner.*
- [x] 414 unit and integration tests pass (2 skipped), plus eight Node check files. Every new route that changes something is tested without the cookie, the Origin and the CSRF token.
- [x] A demo server for trying the browser client against pretend monitors, games and files. *Used for the Chromium checks of the client at four window sizes.*
- [x] README, ARCHITECTURE, SECURITY and API describe 1.1, including what has not been tested.

## Next

- Test on a real iPhone and iPad: certificate download and trust, scanning the QR code, touch
  gestures and pinch zoom, the on-screen gamepad, Game mode decode, sound, the Home Screen app.
- Install ViGEmBus and test a real controller, the on-screen gamepad in a game, and rumble.
- Run home-network mode on the home network: the firewall rule, the QR address, a second
  device.
- Run lock, sleep, a Steam game start and Big Picture for real, and mouse and keyboard through
  the new client.
- Code signing.
- Name constraints on newly created root certificates.
- HEVC or AV1 where the device decodes them.

## Not planned

- Access from outside the home network (relay, NAT traversal, port forwarding).
- Multiple users or roles, MFA, signed installer, auto-update.
- HDR, UDP or WebRTC transport.
