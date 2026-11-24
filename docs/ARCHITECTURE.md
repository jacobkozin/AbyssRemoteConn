# Architecture

Abyss Remote is one Windows process (C#, .NET 10, ASP.NET Core minimal APIs, a Windows Forms
window and tray icon) that serves a browser client (plain HTML, CSS and ES modules) over
HTTPS and WebSockets. There is no cloud service and no database, and the program uses no
third-party package.

## The big picture

```text
Browser                                     AbyssRemote.exe (one process)
-------                                     -----------------------------
index.html  (pairing, home, stream)         Program.cs        window, --console, --smoke-test or --probe-video
                                            App/              HostRuntime (owns the server), window, tray icon,
                                                              settings, QR code, phone-setup checks
app.js  (stream view, input, reconnect)     AbyssServer.cs    Kestrel, middleware, core endpoints
 |- jpeg-stream.js   Desktop mode  <--WSS--   |- JpegStream.cs      GDI capture -> JPEG
 |- video-stream.js  Game mode     <--WSS--   |- VideoStream.cs     credits/acks and bitrate for Game mode
 |- audio.js + audio-worklet.js    <--WSS--   |- audio socket       WASAPI loopback as PCM chunks
 |- gamepad.js, touch-controls.js  --WSS-->   |- control socket     -> IInputSink (SendInput, ViGEmBus)
 '- rumble.js                      <--WSS--   |                     <- notices and controller rumble
pairing.js, home.js, monitors.js,             '- REST               pairing, devices, clipboard
library.js, files.js  (api.js)   --HTTPS->  Features/         monitor previews, file transfer,
                                                              lock and sleep, Steam library
                                            Security.cs       pairing code, TLS certs, CSRF, origin, IP filter
                                            StateStore.cs     paired devices (hashes only)
                                            Sessions.cs       live sockets per device, close codes
                                            DesktopEngine.cs  Windows desktop: GDI, SendInput, cursor, monitor list
                                            GameMode/         DXGI, D3D11, Media Foundation, WASAPI, ViGEmBus
```

`AbyssServer.Build(ServerContext)` builds the whole web app from its dependencies. Production
passes the Windows implementations; the tests pass fakes (`IDesktop`, `IClipboard`,
`IVideoSource`, `IAudioSource`, `IHostActions`, a made-up Steam folder, a temporary transfer
folder) and a throwaway certificate. That seam is what makes the security properties testable
without touching the real mouse, keyboard or screen.

## The app around the server (`App/`)

`Program.cs` starts one of four things: the window (no option), the console screen
(`--console`), the HTTPS self-test (`--smoke-test`) or the Game mode check (`--probe-video`).
The first three sit on `HostRuntime`.

- **`HostRuntime`** owns the server: the state store, the certificates, the pairing code, the
  session registry and the web app. It can start, stop and restart with other options
  (another port, home-network mode on or off) while the paired devices and the pairing code
  stay. A start that fails does not throw: the status becomes `Failed` and carries a sentence
  for the user, such as "Port 8444 is already used by another program." It raises an event
  whenever something a person would want to see changes.
- **`HostEnvironment`** is everything the runtime takes from outside: the data folder, the web
  root, the certificate source, the desktop, the clipboard, the feature modules, the address
  list and the clock. The tests fill it with a temporary folder, throwaway certificates and
  fakes, and then start the real server.
- **`WindowApp`** connects the runtime to the window (`MainWindow` and its cards: Connect,
  Devices, Phone setup, Settings, Activity), the tray icon, the saved settings
  (`preferences.json`), "Start with Windows" (`Autostart`) and the single-instance check. The
  window is built in code; there are no designer files. The runtime's events arrive on other
  threads and are handed to the window's thread.
- **`QrCode`** and **`QrRenderer`** are a hand-written QR encoder and its drawing code. The QR
  code holds the address and, while the pairing code is usable, the code in the address's
  fragment (`#pair=`).
- **`PhoneSetup`** reads the firewall rules, the network type and the controller driver for
  the checklist. It only reads. The two changes it points to (a firewall rule, a Private
  network) are left to the user.
- **`ConsoleScreen`** is the classic console (`P`, `R`, `Q`) on top of the same runtime. The
  program is a windowed one, so the modes that print first attach to the console that started
  them (`ConsoleWindow`).
- **`UiSnapshot`** (`--ui-snapshot <folder>`) draws the window with sample data into PNG files
  without showing it, for checking the layout.

## Feature modules (`Features/`)

Each module maps its own endpoints from `AbyssServer.MapApi` and uses the same two checks as
the core endpoints: `ServerContext.Device` for reading, `ServerContext.Authorized` for
anything that changes or does something. It reaches Windows through a small seam that the
tests replace:

| Module | What it does | Seam |
| --- | --- | --- |
| `DisplayPreviews`, `MonitorDetails` | A JPEG still and a full-size screenshot per monitor, from the Desktop-mode capture. Model names and refresh rates come from the Windows display configuration API (`QueryDisplayConfig`, `DisplayConfigGetDeviceInfo`) | `IDesktop` |
| `FileTransfer`, `TransferName` | List, upload, download and delete in one folder. Names are checked and matched against a listing, never turned into paths | `TransferFolder` (a temporary folder in tests) |
| `HostActions` | Lock and sleep | `IHostActions` |
| `Steam`, `SteamLibrary`, `KeyValues` | Reads Steam's library files with a hand-written reader and starts `steam.exe` with one `steam://` argument | `ISteam` (a made-up Steam folder, and a recorder in place of a real start) |

`DesktopEngine.Displays()` numbers the monitors in a fixed order: the primary one is 0, the
others follow from left to right. Windows itself lists them in an order that can change
between restarts.

## Security layers, outside in

1. **Network:** loopback only by default. In home-network mode (the window's setting, or
   `--lan`), a Kestrel connection filter refuses any peer that is not loopback, RFC 1918,
   link-local or IPv6 unique-local, before TLS starts. The Windows Firewall rule is also
   limited to the local subnet on Private networks.
2. **TLS 1.2/1.3** with a server certificate signed by a private root CA. Both live in the
   Windows user certificate store with non-exportable keys. The server certificate is reissued
   by the same root when it nears expiry or the LAN address changes, so phones keep trusting it.
3. **Host allowlist:** requests must name a host this PC really has (DNS-rebinding defense).
   Away access (1.2) adds one more name, the away address, which only counts with a proxy
   header on a loopback connection (`AwayRequests.Route`); proxy headers with any other name
   are refused. Away requests need the device's separate away key (`StateStore`).
4. **Pairing:** an 8-digit code from a cryptographic RNG, shown only in the Abyss Remote
   window (in home-network mode also inside its QR code), valid 10 minutes and once. Limits:
   5 tries per address per 5 minutes, and 20 wrong tries in total destroy the code.
5. **Session:** a random 256-bit secret in a `Secure; HttpOnly; SameSite=Strict` cookie. Only
   its SHA-256 hash is stored, and it is compared in constant time. It lasts 30 days.
6. **Request checks:** every state-changing request and every WebSocket needs the cookie, an
   exact `Origin` match and a CSRF token (an HMAC of the device id with a key that changes on
   every host start). Browsers cannot add custom headers to WebSockets, so the token travels
   as the second WebSocket subprotocol.
7. **Live revocation:** `SessionRegistry` tracks every socket by device; revoking a device
   closes them at once with close code 4001.
8. **Content Security Policy** `default-src 'none'` with scripts and styles only from the host
   itself; untrusted text (names of devices, games, files and monitors) is only ever set with
   `textContent`.

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
- **Bitrate:** the bitrate can change without restarting the stream. The browser sends
  `bitrate <kbps>` on the video socket. `VideoStream` checks the range (2,000 to 50,000) and
  lets one change through per second, the capture loop picks it up before the next frame, and
  `HardwareEncoder.TrySetBitrate` sets the encoder's mean bitrate through its codec API. When
  an encoder refuses, the host says so and the browser stops asking. In the browser,
  `adaptive-bitrate.js` decides when to ask. It reads the delivery time from the once-a-second
  stats, cuts the bitrate by a quarter above 80 ms, and adds 1 Mbps after eight readings in a
  row under 35 ms, between 2 and 35 Mbps.
- **Format:** encoders emit Annex B (start codes). The host converts it to AVCC
  (length-prefixed) and sends the `avcC` record once, because that is the form every
  WebCodecs implementation accepts, including Safari's.
- **Mouse pointer:** desktop duplication reports the pointer separately. The host sends its
  position and shape (as PNG), and the browser draws it on top of the video, so it stays sharp
  and moves without waiting for a new frame.
- **Timing:** Windows normally wakes threads in 15.6 ms steps, which turned a 60 fps target
  into about 32 fps. While a stream runs, the host requests 1 ms timer resolution
  (`timeBeginPeriod`).

Bitrate changes were measured on the development PC (NVENC, 1080p60, a raw video socket):
asked for 20 Mbps, the stream ran at 21.3–22.0; after `bitrate 30000`, at 29.1–29.5; after
`bitrate 4000` with a game-like scrolling picture, at 2.3. The bitrate is a target, not a
cap: a picture that was new noise in every frame could not be pushed below about 10 Mbps.

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

Rumble travels the other way. The driver handle is opened for overlapped I/O, so a request
that waits can sit beside the input reports. Each virtual controller keeps one notification
request pending. When a game changes the vibration, the request completes with the two motor
levels, and the host sends `{ "type": "rumble", index, strong, weak }` on the control socket.
`rumble.js` plays it on the physical controller with the same index and repeats it while a
level is above zero, because a browser effect has a fixed length. It stops when input is
released, the page loses focus or Watch only is turned on.

The on-screen gamepad (`touch-controls.js`) builds the same `gamepad` message from touches,
at most one per animation frame, so the host cannot tell it from a physical controller. The
special keys and shortcuts in the keyboard sheet are ordinary `key` messages. Typing in the
sheet's box is sent as it happens: the box is compared with what it held before, what went
away becomes Backspace `key` messages and what is new a `text` message. That one rule also
covers a correction made by the phone's keyboard. Everything that sends input goes through
one function in `app.js`, and that is where Watch only blocks it.

## The browser client

The client is plain ES modules with no build step. `index.html` holds three screens and
`app.js` switches between them:

- **Pairing** (`pairing.js`): the form, the code from a QR address, a suggested device name.
- **Home** (`home.js`): monitor cards with live previews (`monitors.js`), the Steam games
  (`library.js`), file transfer (`files.js`), lock and sleep, the clipboard, paired devices.
- **Stream** (`app.js`): the picture (`jpeg-stream.js` or `video-stream.js`), sound
  (`audio.js`), input, controllers (`gamepad.js`, `rumble.js`, `touch-controls.js`) and the
  stream's switches. Two modules hold arithmetic only, so Node can check them: `view-zoom.js`
  (how far the picture may be zoomed, where to scroll, trackpad and scroll speed) and
  `live-typing.js` (what a change in the typing box becomes on the PC).

The stream view zooms by layout, not by a transform: the canvas gets a larger CSS size on a
surface inside a scrolling box, and `app.js` remembers which point of the picture is in the
middle of the stage. That point is put back after every change of size (rotation, the phone's
keyboard opening), a pinch keeps the point between the fingers where it is, and Trackpad
scrolls just far enough to keep the pointer in view. Desktop mode asks the host for a wider
picture as the zoom grows, up to the monitor's own width. The zoom at which text is readable
comes from the monitor's Windows scaling, which the host reports with each monitor.

`api.js` makes every HTTP call and adds the CSRF header to each one that changes something.
`dom.js`, `dialog.js` and `format.js` are small helpers. Logic that needs no page (tidying
file names, filtering games, monitor labels, the bitrate rule, stick and shortcut arithmetic)
is exported and checked in Node.

## Reliability

- The browser reconnects with backoff and fetches a fresh CSRF token first (the host issues
  new ones after a restart).
- WebSocket keepalive pings drop peers that stopped answering after about 25 s. A reconnect
  from the same device replaces its older socket (close code 4008) instead of being refused.
- Close codes tell the browser what happened: 4001 revoked, 4008 replaced, 4029 busy, 4415 not
  supported (Game mode falls back to Desktop mode), 1012 restart. The host also sends 1012 to
  every socket when it stops or restarts because of a changed setting, so browsers reconnect
  instead of asking to pair again.
- Viewer settings live in `localStorage` (no secrets). The chosen monitor is remembered by its
  Windows device name, because its number can change. A flag in `sessionStorage` brings a
  reloaded page back to the stream it was showing.
- Host state is in `%LOCALAPPDATA%\AbyssRemote`: `settings.json` (paired devices, certificate
  thumbprints) and `preferences.json` (the window's settings). Both are written atomically.

## Building

`scripts/Build-Windows.ps1` publishes one program file, `dist\win-x64\AbyssRemote.exe`, with
the browser client in `wwwroot` next to it. `BUILD-WINDOWS.bat` and `BUILD-AND-TEST.bat` run
it. So do the launchers and the self-test script when the source changed, and the shortcut
script when the program has not been built yet.

- **Default:** a small program (under 1 MB) that runs on the .NET 10 runtime installed on the
  PC. The script first checks that the runtime's three parts are there
  (`Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`) and
  stops with a clear message if one is missing. This build downloads nothing, because the
  host project has no packages.
- **`-SelfContained`:** a standalone program with the .NET runtime inside, about 135 MB, for
  PCs without .NET. The first such build fetches Microsoft's runtime packs from nuget.org.
  The GitHub Actions build uses it, so its artifact is the standalone program.

The test project is the only one with NuGet packages (MSTest). They are restored once on a
fresh clone.

## Testing

- `tests/AbyssRemote.Tests` (MSTest; on the development PC 414 tests pass and 2 are skipped):
  - unit tests for pairing limits, expiry, cookie checks, CSRF, origin, host allowlist, private
    address filter, key and controller mapping, H.264/AVCC conversion and stream sizing, and
    since 1.1 the QR encoder, the settings file, "Start with Windows" (with a fake registry),
    the single-instance check, the phone-setup readings, the file-name rules, the reader for
    Steam's files and the monitor order;
  - integration tests that start the real HTTPS server with fakes and attack it from outside
    (no cookie, wrong origin, wrong CSRF, revoked device, oversized messages), plus the
    video/audio protocols and flow control. Every 1.1 route that changes something is sent in
    seven forged forms, and file transfer is attacked with about 90 spellings of path traversal
    and about 80 unsafe names;
  - runtime and window tests: start, stop and restart through `HostRuntime`, the window built
    off screen at four scales (nothing clipped, nothing overlapping), and the whole app started
    hidden and quit again.
- `tests/web/*-checks.mjs` (eleven files, run with Node): `client-checks.mjs` checks the syntax
  of every browser module, element ids, imports, no inline scripts (CSP), no external URLs and
  version consistency. The other ten check the logic of pairing, monitors, the games list,
  files, touch controls, the bitrate rule, rumble, pointer prediction, the stream view's zoom
  and scrolling, and live typing.
- The demo server (`DemoServer.cs`, started with `ABYSS_REMOTE_DEMO=1`) serves the browser
  client against the test fakes, so every button can be tried without touching the real PC.
- `AbyssRemote.exe --smoke-test`: the published program with its real certificate on a free
  port, checked from outside with certificate pinning. The default build passed it on the
  development PC.
- Manual, on the development PC with a Chromium browser on loopback. For 1.0: capture, pointer
  mapping on all monitors, Game mode at 60 fps, sound loopback, reconnect after a host
  restart, controller notice. For 1.1, with Watch only on: pairing through a `#pair=` link,
  the home screen with the real monitors and Steam games, Game mode, Desktop mode, a file
  round trip and sign-out. Not done yet: a real iPhone or iPad, home-network mode, the
  ViGEmBus driver, and real runs of lock, sleep and a Steam start (see the roadmap).

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
  more interop code, which is kept in one place and documented against the SDK headers. It is
  also why the QR encoder and the reader for Steam's files are written by hand, and why the
  default build needs no download.
- **Why Windows Forms for the window?** It ships with .NET on Windows, so the window, the tray
  icon and the dialogs need no package either.
- **Why a runtime under the window?** The window, the console screen and the self-test all
  start the same `HostRuntime`. The tests start it too, so what they check is the server the
  program really runs.
- **Why features behind seams?** Lock, sleep and a Steam start cannot be run for real in a
  test without changing the PC. Each sits behind a small interface, and the tests check
  everything up to the last step before Windows. File transfer does run for real in the
  tests, in a temporary folder.
