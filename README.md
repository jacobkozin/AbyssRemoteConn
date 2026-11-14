# Abyss Remote

[![Build and test](https://github.com/jacobkozin/AbyssRemoteConn/actions/workflows/build.yml/badge.svg)](https://github.com/jacobkozin/AbyssRemoteConn/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D4.svg)](https://www.microsoft.com/windows)

Abyss Remote lets a phone, tablet or second computer see and control a Windows PC from a
browser, over your own home network. On the PC it is a normal Windows app with a window and a
tray icon. On the other device it is a web page, so there is nothing to install there. It has
two ways to stream:

- **Desktop mode** sends sharp JPEG images of the screen. It works in any modern browser and
  suits documents, browsing and general remote control.
- **Game mode** captures the screen on the graphics card, encodes it with the GPU's hardware
  H.264 encoder (NVENC on NVIDIA) and decodes it in the browser with WebCodecs. Add PC sound
  and a game controller, and it works like a self-hosted Steam Link that runs in a browser.

Everything runs on your PC: no account, no cloud relay, no telemetry, no third-party code.

> **Security warning:** this is for trusted private networks. Never port-forward it or expose
> it to the internet. Read [Security](docs/SECURITY.md) before turning on home-network mode.

## Status

Version 1.1.0. Built and tested on one PC (Windows 11, RTX 4080, three monitors, mixed DPI)
with a Chromium-based browser on the same PC. Streaming numbers from that machine, measured
for 1.0:

| Streaming a 1440p/4K monitor at 1080p | R3.3 (before) | 1.0 Desktop mode | 1.0 Game mode |
| --- | --- | --- | --- |
| Frame rate | ~8 fps (18 fps setting) | 26–27 fps (30 fps setting) | 60 fps |
| Bandwidth with a busy screen | not measured | ~42 Mbps | ~11 Mbps |
| Hardware encode time per frame | none | none | ~2 ms |

Those numbers are over localhost; Wi-Fi adds network delay. Game mode was run again through
the 1.1 client and held 60 fps (about 3 ms encode, under 1 ms network+decode on localhost).

What 1.1 was checked with on 2026-11-14: 414 automated tests pass and 2 are skipped, the eight
browser check files pass, and the published `AbyssRemote.exe` (the default build) passes its
HTTPS self-test. The real host was also used from the browser on the same PC with **Watch
only** on: pairing through a `#pair=` link (the address the QR code holds), the home screen
with the three real monitors and the installed Steam games, Game mode, Desktop mode, a file
sent to the PC and fetched back unchanged, and sign-out.

**Not tested yet:**

- **A real iPhone or iPad, Safari, or any touch screen.** Phone layout, touch gestures, the
  on-screen gamepad and the Home Screen app were only tried in a phone-sized Chromium window.
  No phone has scanned the QR code or installed the certificate from the download address.
- **Home-network mode on the development PC.** Everything above ran on `127.0.0.1`; the
  network checks are unit tests.
- **Controllers.** They need the ViGEmBus driver, which is not installed on the development
  PC. Physical controllers, the on-screen gamepad's effect in a game, and rumble have never
  run against the driver.
- **Lock, sleep, starting a Steam game and Big Picture** have never been run for real. Their
  tests use stand-ins.
- **Mouse and keyboard on the real desktop with the 1.1 client.** That was verified live for
  1.0; after the client rework it was only checked against a stand-in host.
- **The CI workflow.** The GitHub Actions build has not run since the program was renamed from
  `AbyssRemote.Host.exe`. The standalone build (`-SelfContained`) was made once on the
  development PC and passed the self-test, and so did the default build.

The [roadmap](docs/ROADMAP.md) says how each feature was verified.

## Features

- **A Windows app:** the window shows the address to open, the pairing code, the paired
  devices, a "Phone setup" checklist and the settings. A tray icon keeps it running after the
  window is closed.
- **Pairing:** an 8-digit one-time code shown in the Abyss Remote window. In home-network mode
  the window also shows a QR code that opens the address with the code filled in. Paired
  devices get a 30-day session; revoke one from the browser or the window, or all of them with
  **Revoke all**.
- **Home screen:** after pairing, the browser shows every monitor, your Steam games, file
  transfer, lock and sleep, the clipboard, and this device's pairing.
- **Monitor picker:** each monitor with its model name, resolution, refresh rate and a live
  preview. Tap one to open it in Desktop mode, or tap its **Game mode** button. Switch
  monitors from inside a stream; the choice is remembered. Pointer mapping is exact with mixed
  DPI scaling. **Screenshot** saves a full-resolution picture of the monitor you are looking
  at.
- **Desktop mode:** JPEG frames, one in flight at a time, skipped entirely while the screen is
  unchanged, mouse pointer included.
- **Game mode:** GPU capture (DXGI Desktop Duplication), GPU color conversion, hardware H.264
  (Media Foundation), 30/60/120 fps, 720p to 4K, live stats for encode time and delivery time.
  The bitrate is automatic (2–35 Mbps) or fixed (10, 20, 35 or 50 Mbps). It falls back to
  Desktop mode when the PC or browser cannot do it.
- **Steam library:** the PC's installed Steam games with their cover art and a filter. Tap a
  game to start it (the browser asks first), or open Steam Big Picture.
- **File transfer:** send files from the device to `Downloads\Abyss Remote` on the PC, and
  download or delete what is in that folder. Up to 2 GB per file; nothing is ever overwritten.
- **Lock and sleep:** lock the PC or put it to sleep from the device. Nothing else: no
  shutdown, no restart, and no way to run a command.
- **Input:** mouse (pointer or trackpad style), smooth wheel, physical keyboard mapped by scan
  code, text entry from phone keyboards, "Lock mouse" for games, touch gestures (tap,
  two-finger tap or long press for right-click, hold-then-drag, two-finger scroll, pinch to
  zoom from 100% to 250%).
- **Special keys and shortcuts:** the keyboard sheet has Ctrl, Alt, Shift and Win as keys that
  stay held until tapped again; Esc, Tab, Del, Ins, Home, End, PgUp, PgDn, the arrows, PrtSc
  and F1–F12; and one-tap Alt+Tab, Task Manager, Show desktop, Close window, Snip and Steam
  overlay.
- **Controllers:** a controller paired with the phone or tablet becomes a virtual Xbox 360
  controller on the PC, with rumble passed back where the browser supports it. **Touch
  gamepad** puts an on-screen controller over the picture instead. Both need ViGEmBus, see
  [Controllers](#controllers).
- **Watch only:** see and hear the PC without sending any mouse, keyboard or controller input.
- **Sound:** what the PC plays, streamed to the browser with a small adaptive buffer.
- **Clipboard:** text in both directions, on demand.
- **Phones and tablets:** full-screen layout with a floating Controls button, Fit/Fill/Stretch,
  zoom, an on-screen keyboard sheet, and an installable Home Screen app.
- **Reliability:** reconnects by itself (also after the host restarts, for example when you
  change its port), detects dead connections, releases held keys and buttons when a
  connection drops, remembers your settings.

## How it works

```text
 Phone / tablet / computer browser                         Windows PC (AbyssRemote.exe, one .NET 10 process)
 ---------------------------------                         -------------------------------------------------
  index.html + app.js                 HTTPS + WSS          window + tray icon: address, pairing code, settings
  |- jpeg-stream.js   <== JPEG frames ==============  /api/v1/stream   GDI capture + JPEG
  |- video-stream.js  <== H.264 (AVCC) =============  /api/v1/video    DXGI -> D3D11 -> NVENC
  |- audio.js         <== 48 kHz PCM ===============  /api/v1/audio    WASAPI loopback
  |- input, gamepad.js == mouse/keys/pad (JSON) ===>  /api/v1/control  SendInput, ViGEmBus
  |- rumble.js        <== notices, rumble (JSON) ===  /api/v1/control
  '- pairing.js, home.js: REST ====================>  /api/v1/*        pairing, monitors, Steam,
                                                                       files, lock/sleep, clipboard
```

Every connection needs the paired session cookie, the exact page origin and a per-device CSRF
token; see [Architecture](docs/ARCHITECTURE.md) for the full picture.

## Requirements

- Windows 10 or 11, x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build. The program the
  default build makes runs on the .NET 10 runtime that comes with the SDK. A standalone build
  for PCs without .NET is described under [Quick start](#quick-start).
- Optional: [Node.js](https://nodejs.org/) 22 for the browser client checks.
- A current browser on the other device. The client uses features Safari has had since
  version 15.4. Only Chromium has been tested so far.
- Game mode: a GPU with a hardware H.264 encoder that Windows exposes (NVIDIA, AMD or Intel),
  and a browser with WebCodecs (Chrome or Edge, Safari 16.4+ on iPhone/iPad).
- Optional: Steam, for the games list, and the ViGEmBus driver, for controllers.

## Quick start

1. **Build and test:** double-click `BUILD-AND-TEST.bat`. It runs the unit and integration
   tests, the browser checks, publishes `dist\win-x64\AbyssRemote.exe` and runs the published
   program's HTTPS self-test. The program is small (under 1 MB) because it uses the .NET 10
   runtime installed on the PC. Building it downloads nothing; only the tests need their
   MSTest packages from NuGet, once on a fresh clone.
2. **Start it:** double-click `dist\win-x64\AbyssRemote.exe`, or run
   `CREATE-DESKTOP-SHORTCUT.bat` once to put an "Abyss Remote" shortcut on the Desktop. The
   Abyss Remote window opens and shows an address and a pairing code.
3. **Try it on this PC:** open `https://127.0.0.1:8444` in a browser on the PC, give the
   device a name and type the pairing code from the window. Until you turn on home-network
   mode, only this PC can connect.
4. **Check Game mode:** in a Command Prompt in the project folder, run
   `START-LOCAL-ONLY.bat --probe-video`. It encodes a few seconds on every monitor and prints
   the encoder, frame rate and timings.

Closing the window does not stop Abyss Remote: it stays in the tray so devices can connect
(Settings: "Keep running in the tray when I close the window"). Click the tray icon to get
the window back, or right-click it and choose **Quit**. Starting the program a second time
shows the running window.

**For a PC without .NET:** `BUILD-WINDOWS.bat -SelfContained` (or `BUILD-AND-TEST.bat
-SelfContained`) makes a standalone program with the .NET runtime inside, about 135 MB. The
first such build fetches Microsoft's runtime packs from nuget.org. The GitHub Actions build
uses it, so its artifact is that standalone program. The default build, copied to another PC,
needs the .NET 10 Desktop Runtime and ASP.NET Core Runtime there.

### Use it from a phone or tablet

1. Make sure Windows calls your network **Private** (Settings > Network & internet).
2. In the Abyss Remote window, under Settings, turn on **Allow devices on my home network**.
   The first time, it explains what that means and asks you to confirm. (`START-LAN.bat` does
   the same for one run.) The window now shows the address to open, for example
   `https://192.168.1.20:8444`, and a QR code.
3. Open the firewall once. "Phone setup" in the window says whether Windows Firewall has a
   rule for the port. **Add firewall rule…** adds it after Windows asks for administrator
   rights; `ENABLE-LAN-FIREWALL.bat` does the same. It is one rule: TCP 8444, Private networks
   only, local subnet only.
4. Trust the Abyss Remote root certificate on the phone or tablet (needed once):
   - Get the certificate onto the device. Either open the download address shown under
     "Phone setup" in Safari (`https://<pc-address>:8444/AbyssRemote-RootCA.cer`; Safari warns
     first, because it does not trust the PC yet), or click **Save certificate file…** in the
     window and AirDrop or email the file to yourself.
   - iPhone/iPad: Settings > General > VPN & Device Management > install the profile, then
     Settings > General > About > Certificate Trust Settings > turn on full trust for
     "Abyss Remote Personal Root CA".
   - Before you turn on trust, check that the SHA-256 fingerprint the device shows is the one
     under "Phone setup" in the window. The download is not protected until the device trusts
     the certificate, so the fingerprint is what proves you got the right file.
5. Scan the QR code with the phone's camera. It opens the address with the pairing code
   filled in. Enter a name for the device and tap **Pair this device**. You can also type the
   address into Safari and the code by hand.
6. Optional: Share > **Add to Home Screen** gives a full-screen app. It keeps its own login,
   so pair once more inside it.

Steps 4 and 5 have not been tried on a real iPhone or iPad yet. If Safari does not offer to
install the profile after the download, send the saved file to the device instead.

### Game mode

On the home screen, tap a monitor's **Game mode** button. Inside a stream, pick **Mode >
Game · 60 fps** under Settings (or use the Controls button on a phone). Resolution, frame rate
and bitrate are in the same settings. The stats line shows:

- **encode:** how long the GPU took to compress a frame.
- **network+decode:** time from the PC sending a frame until this device has decoded it and its
  acknowledgement is back.

**Bitrate** starts on **Automatic (2–35 Mbps)**. The stream begins at 20 Mbps, drops by a
quarter when network+decode goes above 80 ms, and climbs 1 Mbps after eight good readings in
a row (under 35 ms, about one reading a second). Pick a fixed value to turn that off.

On a computer, **Lock mouse** captures the pointer and sends raw movement, which first-person
games need; press Esc to release it. Turn on **Sound** with the speaker button. Browsers only
start audio after a tap, so after reloading the page, tap once more.

### Stream controls

The controls are in the top bar on a computer and behind the floating **Controls** button on
a phone:

- **Keyboard** opens a sheet for typing text, with the special keys and shortcuts listed under
  [Features](#features).
- **Touch gamepad** shows two sticks, a D-pad, A/B/X/Y, bumpers, triggers, View, Guide, Menu
  and both stick clicks over the picture.
- **Watch only** stops all input from this device. A badge on the picture shows it is on, and
  it stays on until you turn it off, also after a reload.
- **Monitors**, **Screenshot**, **Sound** and **Home**.

Pinch with two fingers to zoom the picture; drag with two fingers to move around while zoomed.

### Controllers

Pair the controller (Xbox, PlayStation or MFi) with the phone, tablet or computer, open a
stream and press a button so the browser sees it. Without a controller, turn on **Touch
gamepad** for an on-screen one; while it is on, it replaces a physical controller. On the PC,
both need the free **ViGEmBus** driver, the same one Sunshine and DS4Windows use:

1. Download the installer from <https://github.com/nefarius/ViGEmBus/releases> (latest
   release) and run it. It needs administrator rights; reboot if the installer asks.
2. Reconnect Abyss Remote. Games then see an Xbox 360 controller. "Phone setup" in the window
   says whether the driver was found.

When a game makes the controller vibrate, the PC passes that back and the browser plays it on
the physical controller, where the browser and the controller support it.

Abyss Remote never installs drivers itself. ViGEmBus is no longer actively developed, but its
last release is still widely used. Without it, the browser shows a short notice and
everything else keeps working. The driver is not installed on the development PC, so
controllers, the Touch gamepad and rumble are covered by tests of their messages only, not by
a run with the driver.

## Command-line options

| Option | Meaning |
| --- | --- |
| *(none)* | Open the window with the saved settings (at first: this PC only, port 8444) |
| `--lan` | Home-network mode for this run: also accept devices on the local network (private addresses only) |
| `--local` | This PC only for this run, even when home-network mode is saved |
| `--port <n>` | Use another port (1024–65535) for this run; the `.bat` launchers pass this through |
| `--renew-cert` | Issue a fresh server certificate from the same root |
| `--console` | The classic console screen (keys `P`, `R`, `Q`) instead of the window |
| `--minimized` | Start hidden in the tray, without a pairing code ("Start with Windows" uses this) |
| `--probe-video` | Test Game mode on every monitor and exit |
| `--smoke-test` | Run the HTTPS self-test on a free port and exit |

Options on the command line apply to one run and are not saved; the window's Settings are.
`--lan` together with `--local`, or an option the program does not know, is an error (exit
code 2).

`AbyssRemote.exe` is a windowed program, so a console does not wait for it. Start the three
options that print through a launcher, which waits and shows the output:
`START-LOCAL-ONLY.bat --console`, `START-LOCAL-ONLY.bat --probe-video` and `TEST-HOST.bat`.

Other examples: `START-LOCAL-ONLY.bat --port 8555`, `START-LAN.bat --port 8555` (then
`ENABLE-LAN-FIREWALL.bat 8555`). `START-LOCAL-ONLY.bat` and `START-LAN.bat` rebuild the program
first when the source changed.

## Security model

In short: loopback only unless you choose home-network mode. In that mode, public IP
addresses are refused before TLS even starts. TLS uses a private root CA whose keys stay in
the Windows user certificate store. Pairing uses a one-time code with brute-force limits,
shown only in the window on the PC, and the session cookie is Secure, HttpOnly and
SameSite=Strict, with only its hash stored. Every WebSocket and every state-changing request
needs that cookie, the exact Origin and a per-device CSRF token. Revoking a device disconnects
it immediately. File transfer is limited to one folder, the PC actions are a fixed list of
two, and a Steam game can only be started by the number of an installed game. Details, the
R3.3 issues fixed in 1.0, what 1.1 added and the known limits are in
[docs/SECURITY.md](docs/SECURITY.md).

## Troubleshooting

- **The build stops with "The .NET 10 runtime part ... is not installed on this PC":** install
  the .NET 10 SDK, or build the standalone program with `BUILD-WINDOWS.bat -SelfContained`.
- **"Port 8444 is already used by another program":** close that program and click **Try
  again**, or choose another port under Settings (or start with `--port`).
- **The phone cannot open the address:** look at "Phone setup" in the window. The firewall
  needs a rule for the port, and Windows blocks incoming connections while the network is
  marked Public.
- **Certificate warning in the browser:** the root certificate is not trusted on that device
  yet (see step 4 above), or you opened an address that is not in the certificate. Quit Abyss
  Remote and start it again after changing networks; it reissues the server certificate
  automatically.
- **Game mode missing from the settings:** the browser lacks WebCodecs H.264 or the PC has no
  hardware encoder. Run `--probe-video` to see why.
- **Taps and keys do nothing:** check that **Watch only** is off. It is remembered between
  visits.
- **"Access is denied" or the program closes after a few seconds right after building:** some
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
- The bitrate is a target for the encoder, not a hard cap. On the development PC (NVENC), a
  picture that was new noise in every frame could not be pushed below about 10 Mbps.
- Windows does not let a normal program see or control the lock screen, UAC prompts or
  elevated windows; the stream shows a message while they are up. After **Lock the PC** you
  cannot unlock from the device, and after sleep someone has to wake the PC by hand.
- iPhone Safari cannot hide its own bars or system gestures (use the Home Screen app).
- File transfer: 2 GB per file, one upload at a time, uploads cannot be resumed, and a delete
  is permanent (not the Recycle Bin).
- The Steam list can include installed software that is not a game; only a few known tools
  are left out.
- One user, full control: no read-only devices, roles, MFA or audit log. Watch only is a
  switch in the browser, not a permission. Unsigned binaries.
- Several parts have not been tested on real hardware yet: phones and tablets, home-network
  mode, controllers, and the real lock, sleep and Steam start. See [Status](#status).

## Development

```powershell
.\scripts\Build-And-Test.ps1                       # everything BUILD-AND-TEST.bat does
dotnet test tests\AbyssRemote.Tests                # unit and integration tests (414 pass, 2 skipped)
node tests\web\client-checks.mjs                   # one of the eight browser client check files
```

The integration tests start the real HTTPS server with fake desktop, input, video and audio
sources, a made-up Steam folder and a temporary transfer folder, which is how they prove that
nothing reaches the PC without passing the security checks. A demo server serves the browser
client against the same fakes for trying it by hand. See [tests/README.md](tests/README.md),
[CONTRIBUTING.md](CONTRIBUTING.md) and the [API](docs/API.md).

```text
src/AbyssRemote.Host/           Windows app: web server, security, capture, input
src/AbyssRemote.Host/App/       Window, tray icon, settings, QR code, the runtime that owns the server
src/AbyssRemote.Host/Features/  Monitor previews, file transfer, lock and sleep, Steam library
src/AbyssRemote.Host/GameMode/  DXGI, Direct3D 11, Media Foundation, WASAPI, ViGEmBus interop
src/AbyssRemote.Host/wwwroot/   Browser client (no frameworks, no build step): app.js for the
                                stream, pairing.js, home.js, monitors.js, library.js, files.js,
                                touch-controls.js, adaptive-bitrate.js, rumble.js and helpers
tests/AbyssRemote.Tests/        MSTest unit + integration tests, demo server
tests/web/                      Browser client checks (Node)
tools/make_icon.ps1             Draws the app icon: the Windows .ico and the web-app PNGs
scripts/, *.bat                 Build, test, launch, shortcut and firewall helpers
docs/                           Architecture, security, API, roadmap
```

## License

[MIT](LICENSE).
