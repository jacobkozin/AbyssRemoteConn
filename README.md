# Abyss Remote

[![Build and test](https://github.com/jacobkozin/AbyssRemoteConn/actions/workflows/build.yml/badge.svg)](https://github.com/jacobkozin/AbyssRemoteConn/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D4.svg)](https://www.microsoft.com/windows)

Abyss Remote lets a phone, tablet or second computer see and control a Windows PC from a
browser, over your own home network. It has two ways to stream:

- **Desktop mode** sends sharp JPEG images of the screen. It works in any modern browser and
  suits documents, browsing and general remote control.
- **Game mode** captures the screen on the graphics card, encodes it with the GPU's hardware
  H.264 encoder (NVENC on NVIDIA) and decodes it in the browser with WebCodecs. Add PC sound
  and a game controller, and it works like a self-hosted Steam Link that runs in a browser.

Everything runs on your PC: no account, no cloud relay, no telemetry, no third-party code.

> **Security warning:** this is for trusted private networks. Never port-forward it or expose
> it to the internet. Read [Security](docs/SECURITY.md) before turning on LAN mode.

## Status

Version 1.0.0. Built and tested on Windows 11 (RTX 4080, three monitors, mixed DPI) with a
Chromium-based browser on the same PC. Results from that machine:

| Streaming a 1440p/4K monitor at 1080p | R3.3 (before) | 1.0 Desktop mode | 1.0 Game mode |
| --- | --- | --- | --- |
| Frame rate | ~8 fps (18 fps setting) | 26–27 fps (30 fps setting) | 60 fps |
| Bandwidth with a busy screen | not measured | ~42 Mbps | ~11 Mbps |
| Hardware encode time per frame | none | none | ~2 ms |

Those numbers are over localhost; Wi-Fi adds network delay. **Not yet tested on a real iPhone
or iPad**: phone layout, touch gestures and the Home Screen app were checked in an emulated
phone viewport, and real-device testing is listed in the [roadmap](docs/ROADMAP.md).
Controllers need the ViGEmBus driver, which was not installed on the development PC, so that
path is covered by unit tests but not yet by hardware.

## Features

- **Pairing:** an 8-digit one-time code shown only on the PC. Paired devices get a 30-day
  session; revoke one from the browser or press `R` on the PC to revoke all.
- **Desktop mode:** JPEG frames, one in flight at a time, skipped entirely while the screen is
  unchanged, mouse pointer included.
- **Game mode:** GPU capture (DXGI Desktop Duplication), GPU color conversion, hardware H.264
  (Media Foundation), 30/60/120 fps, 720p to 4K, 10–50 Mbps, live stats for encode time and
  delivery time. It falls back to Desktop mode when the PC or browser cannot do it.
- **Input:** mouse (pointer or trackpad style), smooth wheel, physical keyboard mapped by scan
  code, text entry from phone keyboards, "Lock mouse" for games, touch gestures (tap, two-finger
  tap or long press for right-click, hold-then-drag, two-finger scroll).
- **Controllers:** a controller paired with the phone or tablet becomes a virtual Xbox 360
  controller on the PC (needs ViGEmBus, see [Controllers](#controllers)).
- **Sound:** what the PC plays, streamed to the browser with a small adaptive buffer.
- **Monitors:** any monitor can be streamed; pointer mapping is exact with mixed DPI scaling.
- **Clipboard:** text in both directions, on demand.
- **Phones and tablets:** full-screen layout with a floating Controls button, Fit/Fill/Stretch,
  zoom, an on-screen keyboard sheet, and an installable Home Screen app.
- **Reliability:** reconnects by itself (also after the PC restarts the host), detects dead
  connections, releases held keys and buttons when a connection drops, remembers your settings.

## How it works

```text
 Phone / tablet / computer browser                         Windows PC (one .NET 10 process)
 ---------------------------------                         --------------------------------
  index.html + app.js                 HTTPS + WSS
  |- jpeg-stream.js   <== JPEG frames ===============  /api/v1/stream   GDI capture + JPEG
  |- video-stream.js  <== H.264 (AVCC) =============  /api/v1/video    DXGI -> D3D11 -> NVENC
  |- audio.js         <== 48 kHz PCM ===============  /api/v1/audio    WASAPI loopback
  |- input, gamepad.js == mouse/keys/pad (JSON) ===>  /api/v1/control  SendInput, ViGEmBus
  '- REST: pairing, monitors, devices, clipboard  ->  /api/v1/*        ASP.NET Core minimal APIs
```

Every connection needs the paired session cookie, the exact page origin and a per-device CSRF
token; see [Architecture](docs/ARCHITECTURE.md) for the full picture.

## Requirements

- Windows 10 or 11, x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build (the published
  host is self-contained).
- Optional: [Node.js](https://nodejs.org/) 22 for the browser client checks.
- Game mode: a GPU with a hardware H.264 encoder that Windows exposes (NVIDIA, AMD or Intel),
  and a browser with WebCodecs (Chrome or Edge, Safari 16.4+ on iPhone/iPad).

## Quick start

1. **Build and test:** double-click `BUILD-AND-TEST.bat`. It runs the unit and integration
   tests, the browser checks, publishes `dist\win-x64\AbyssRemote.Host.exe` and runs the
   published host's HTTPS self-test.
2. **Try it on this PC:** run `START-LOCAL-ONLY.bat` and open `https://127.0.0.1:8444`. Type
   the pairing code shown in the console window. Only this PC can connect in this mode.
3. **Check Game mode:** `dist\win-x64\AbyssRemote.Host.exe --probe-video` encodes a few
   seconds on every monitor and prints the encoder, frame rate and timings.

### Use it from a phone or tablet

1. Make sure Windows calls your network **Private** (Settings > Network & internet).
2. Run `ENABLE-LAN-FIREWALL.bat` once. It asks for administrator rights and adds one rule:
   TCP 8444, Private networks only, local subnet only.
3. Run `START-LAN.bat`. The console lists the addresses to open, for example
   `https://192.168.1.20:8444`.
4. Trust the Abyss Remote root certificate on the phone or tablet (needed once):
   - Copy `%LOCALAPPDATA%\AbyssRemote\AbyssRemote-RootCA.cer` to the device (AirDrop or email
     it to yourself) and open it.
   - iPhone/iPad: Settings > General > VPN & Device Management > install the profile, then
     Settings > General > About > Certificate Trust Settings > turn on full trust.
   - Check that its SHA-256 fingerprint matches "Root CA" in the console before trusting it.
5. Open the address in Safari, enter a name for the device and the pairing code.
6. Optional: Share > **Add to Home Screen** gives a full-screen app. It keeps its own login,
   so pair once more inside it.

### Game mode

Pick **Mode > Game · 60 fps** (or use the Controls button on a phone). Resolution, frame rate
and bitrate are in the same settings. The stats line shows:

- **encode:** how long the GPU took to compress a frame.
- **network+decode:** time from the PC sending a frame until this device has decoded it and its
  acknowledgement is back.

On a computer, **Lock mouse** captures the pointer and sends raw movement, which first-person
games need; press Esc to release it. Turn on **Sound** with the speaker button. Browsers only
start audio after a tap, so after reloading the page, tap once more.

### Controllers

Pair the controller (Xbox, PlayStation or MFi) with the phone, tablet or computer, open Abyss
Remote and press a button so the browser sees it. On the PC, controllers need the free
**ViGEmBus** driver, the same one Sunshine and DS4Windows use:

1. Download the installer from <https://github.com/nefarius/ViGEmBus/releases> (latest
   release) and run it. It needs administrator rights; reboot if the installer asks.
2. Reconnect Abyss Remote. Games then see an Xbox 360 controller.

Abyss Remote never installs drivers itself. ViGEmBus is no longer actively developed, but its
last release is still widely used. Without it, the browser shows a short notice and
everything else keeps working.

## Command-line options

| Option | Meaning |
| --- | --- |
| *(none)* | This PC only, on `127.0.0.1:8444` |
| `--lan` | Also accept devices on the local network (private addresses only) |
| `--port <n>` | Use another port (1024–65535); the `.bat` launchers pass this through |
| `--renew-cert` | Issue a fresh server certificate from the same root |
| `--probe-video` | Test Game mode on every monitor and exit |
| `--smoke-test` | Run the HTTPS self-test on a free port and exit |

Examples: `START-LOCAL-ONLY.bat --port 8555`, `START-LAN.bat --port 8555` (then
`ENABLE-LAN-FIREWALL.bat 8555`).

## Security model

In short: loopback only unless you choose LAN mode. In LAN mode, public IP addresses are
refused before TLS even starts. TLS uses a private root CA whose keys stay in the Windows user
certificate store. Pairing uses a one-time code with brute-force limits, and the session cookie
is Secure, HttpOnly and SameSite=Strict, with only its hash stored. Every WebSocket and every
state-changing request needs that cookie, the exact Origin and a per-device CSRF token.
Revoking a device disconnects it immediately. Details, the R3.3 issues fixed in 1.0, and the
known limits are in [docs/SECURITY.md](docs/SECURITY.md).

## Troubleshooting

- **"Port 8444 is already used by another program":** close it or start with `--port`.
- **Certificate warning in the browser:** the root certificate is not trusted on that device
  yet (see step 4 above), or you opened an address that is not in the certificate. Restart the
  host after changing networks; it reissues the server certificate automatically.
- **Game mode missing from the settings:** the browser lacks WebCodecs H.264 or the PC has no
  hardware encoder. Run `--probe-video` to see why.
- **"Access is denied" or the host closes after a few seconds right after building:** some
  antivirus products (Trend Micro, for one) block newly built unsigned programs. Add an
  exception for the `dist` folder.
- **No sound:** tap the page once (browser autoplay rules) and check the PC is playing to its
  default output device. The PC's own volume can affect the streamed level.
- **"Input is being used in another tab":** each device has one input connection. The newest
  tab wins; tap Reconnect to take it back.

## Known limitations

- Private networks only: no relay, NAT traversal or internet access.
- Video and audio travel over TCP (WebSockets), so Wi-Fi packet loss shows up as short stalls
  rather than glitches. A native app like Moonlight (UDP) can do better on poor Wi-Fi.
- No HDR, H.265 or AV1. Rotated monitors are not handled in Game mode.
- Windows does not let a normal program see or control the lock screen, UAC prompts or
  elevated windows; the stream shows a message while they are up.
- iPhone Safari cannot hide its own bars or system gestures (use the Home Screen app);
  real-device testing is still to do.
- Controllers: no rumble feedback yet. The driver path is untested without ViGEmBus installed.
- One user, full control: no read-only devices, roles, MFA or audit log. Unsigned binaries.

## Development

```powershell
.\scripts\Build-And-Test.ps1                       # everything BUILD-AND-TEST.bat does
dotnet test tests\AbyssRemote.Tests                # 102 unit and integration tests
node tests\web\client-checks.mjs                   # browser client wiring checks
```

The integration tests start the real HTTPS server with fake desktop, input, video and audio
sources, which is how they prove that nothing reaches the PC without passing the security
checks. See [tests/README.md](tests/README.md), [CONTRIBUTING.md](CONTRIBUTING.md) and the
[API](docs/API.md).

```text
src/AbyssRemote.Host/       Windows host: web server, security, capture, input, Game mode
src/AbyssRemote.Host/GameMode/  DXGI, Direct3D 11, Media Foundation, WASAPI, ViGEmBus interop
src/AbyssRemote.Host/wwwroot/   Browser client (no frameworks, no build step)
tests/AbyssRemote.Tests/    MSTest unit + integration tests
tests/web/                  Browser client checks (Node)
scripts/, *.bat             Build, test, launch and firewall helpers
docs/                       Architecture, security, API, roadmap
```

## License

[MIT](LICENSE).
