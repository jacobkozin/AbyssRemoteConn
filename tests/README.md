# Tests

## .NET tests: `dotnet test tests\AbyssRemote.Tests`

MSTest. On the development PC 414 tests pass and 2 are skipped. Everything runs on loopback;
no second device and no driver is needed. The MSTest packages come from NuGet and are
restored once on a fresh clone. After that the tests run without a package source.

- **Unit tests:** pairing limits and expiry (with a manual clock), session cookie checks and
  storage, CSRF, origin matching, host allowlist, the private-address filter, certificate
  coverage, command-line options, keyboard scan codes, controller mapping and IOCTL codes,
  H.264 Annex B / AVCC / avcC conversion, stream sizing. Since 1.1 also the QR encoder (read
  back by a separate reader written for the tests), the settings file, "Start with Windows"
  with a fake registry, the single-instance check, the firewall and network readings of
  "Phone setup", the file-name rules, and the reader for Steam's files on made-up folders.
- **Integration tests** (`ServerIntegrationTests`, `GameModeProtocolTests`, and a class per
  feature: `DisplayPreviewTests`, `FileTransferTests`, `FileTransferAttackTests`,
  `HostActionTests`, `SteamEndpointTests`): each test starts the real web server (Kestrel, TLS
  with a throwaway certificate, all middleware and endpoints) on a free loopback port, with
  fake desktop, input, clipboard, video, audio and host-action sources that record what
  reaches them, a made-up Steam folder and a temporary transfer folder. The tests then act as
  a browser, or as an attacker without a session, a wrong origin, a wrong CSRF token, a
  revoked device or oversized messages, and check that nothing reaches the fakes unless every
  check passed. `ForgedRequests` sends each new state-changing request in seven forged forms.
  They also cover pairing, cookies, revocation, reconnect replacement, input release on
  disconnect, and the JPEG/Game-mode/audio protocols including flow control and bitrate
  changes.
- **Runtime and window tests** (`HostRuntimeTests`, `WindowLayoutTests`, `ConsoleScreenTests`):
  the runtime is started, stopped and restarted with throwaway certificates; the window is
  built off screen at 100, 125, 150 and 200% scaling and checked for clipped or overlapping
  controls; the whole app is started hidden and quit again. No window appears, and the real
  registry is not touched.

A few tests read the real PC without changing it: the monitor list and its order, and the
installed Steam library (the test prints numbers only). Some tests skip themselves when the
PC cannot run them:

| Test | Runs only when |
| --- | --- |
| `ServesTheBrowserClientUntilToldToStop` (the demo server) | `ABYSS_REMOTE_DEMO=1` is set |
| `LinksAreNeverFollowed` | the account may create symbolic links (Developer Mode or an elevated prompt) |
| `TheSteamLibraryOfThisPcCanBeRead` | Steam is installed |
| `AnOldStyleShortNameIsNotASecondWayIn` | the drive gives files 8.3 short names |
| `UploadsCarryTheDownloadMarkAndAreOrdinaryFiles` | the temporary folder is on NTFS |

The first two are the 2 skipped tests on the development PC.

## Browser client checks: `tests\web\*-checks.mjs`

Eight files. Each runs on its own with Node 22 and needs no packages, for example
`node tests\web\client-checks.mjs`. To run them all in PowerShell, as the build script does:

```powershell
Get-ChildItem tests\web\*-checks.mjs | ForEach-Object { node $_.FullName }
```

| File | Checks |
| --- | --- |
| `client-checks.mjs` | Every browser module parses as an ES module, imports resolve, every element id and icon the scripts use exists, there are no inline scripts or styles (the CSP would block them), nothing loads from other sites, every WebSocket carries the CSRF subprotocol, and the cache-busting version matches the project version |
| `pairing-checks.mjs` | A code in a `#pair=` address fills the form and leaves the address bar at once, the pairing screen never logs or stores it, and the suggested device name fits the device |
| `monitors-checks.mjs` | Monitor labels, shapes, preview sizes, and which monitor is picked when the remembered one is gone |
| `library-checks.mjs` | Filtering the games list and how many games are shown at first |
| `files-checks.mjs` | Tidying file names so the PC accepts them (the PC's own rule is written out again in the check), sizes and dates as text |
| `touch-checks.mjs` | The on-screen sticks, pinch zoom arithmetic and the key order of shortcuts |
| `pointer-checks.mjs` | The page's own pointer: movement shows before the host replies, old replies cannot pull it back, no travel is lost between frames, and the video keeps its pointer until a picture can be drawn |
| `view-checks.mjs` | How far the picture may be zoomed, keeping the point under a finger in place, following the pointer, telling a pinch from a scroll, trackpad and scroll speed |
| `typing-checks.mjs` | Live typing: the Backspaces and text a change in the typing box becomes, including a correction by the phone's keyboard, an emoji, Enter and Backspace in an empty box |
| `adaptive-checks.mjs` | The automatic bitrate rule |
| `rumble-checks.mjs` | Rumble values are bounded and reach only their controller |

`check.mjs` is their shared helper, not a check. It loads the browser's own modules into Node
through temporary `.mjs` copies.

## Demo server: try the browser client without the real PC

`DemoServer.cs` starts the test server with pretend monitors, games and files and keeps it
running. Whatever a browser does there reaches the fakes only, so every button can be
pressed, Lock, Sleep and Delete included.

```
set ABYSS_REMOTE_DEMO=1
set ABYSS_REMOTE_DEMO_DIR=C:\some\folder      (optional; default %TEMP%\abyss-remote-demo)
dotnet test tests\AbyssRemote.Tests --filter DemoServer
```

The folder gets `address.txt` (the address, then a pairing code) and `recorded.json` (what
reached the fakes). The certificate is a throwaway, so the browser warns about it once.
Creating a file in the folder steers the demo: `stop` ends it, `new-code` writes a fresh
pairing code, and while `locked` or `no-steam` exists the pretend PC is locked or has no
Steam. It has three monitors, fourteen games (two without a cover), four files and a 5 MB
upload limit. The client files are served straight from `src\AbyssRemote.Host\wwwroot`, so an
edit shows on reload without a rebuild.

## Published program self-test: `AbyssRemote.exe --smoke-test`

Starts the real published program with its real certificate on a free loopback port and
checks it from outside with the certificate pinned (no system trust changes). It confirms the
UI, the security headers and the root certificate download, that the API's read routes and
all four WebSockets refuse anonymous access, that pairing needs an Origin, and that an unknown
`Host` is refused. `TEST-HOST.bat` runs it and waits for it, rebuilding the program first if
the source changed. That rebuild is the default one: a small
`dist\win-x64\AbyssRemote.exe` that runs on the installed .NET 10 runtime and downloads
nothing. It passed on the development PC. The standalone build (`-SelfContained`, which the
GitHub Actions build uses) was made once there and passed the same self-test, and the
GitHub Actions workflow passes with it.

## Game mode on real hardware: `AbyssRemote.exe --probe-video`

Builds the GPU capture + hardware encoder pipeline on every monitor and reports the encoder,
frame rate, encode time and bitrate. `AbyssRemote.exe` is a windowed program, so start it
through a launcher that waits for it and shows the output: `START-LOCAL-ONLY.bat --probe-video`.

## The window as pictures: `--ui-snapshot <folder>`

A developer option. After `dotnet build`,
`dotnet src\AbyssRemote.Host\bin\Debug\net10.0-windows\AbyssRemote.dll --ui-snapshot <folder>`
draws the window with sample data into PNG files at 100% and 150% scaling, plus the tray
menu and a confirmation dialog. No server is started and nothing appears on screen. It also
writes `checks.txt` with the real, read-only "Phone setup" results of the PC.

## Manual testing still needed

- On an iPhone or iPad: the certificate download and trust, scanning the QR code, pairing,
  touch gestures and pinch zoom, the view following the pointer, the typing bar with the
  phone's own keyboard (how the page moves when it opens, corrections, Backspace in an empty
  box), the on-screen gamepad, Game mode decoding, sound, and the Home Screen app.
- With the ViGEmBus driver installed on the PC: a real controller, the on-screen gamepad in a
  game, and rumble.
- Home-network mode: the firewall rule, the address in the QR code, a second device.
- For real, on a PC where that is fine: lock, sleep, starting a Steam game and Big Picture.
- Mouse and keyboard on the real desktop through the reworked client.

See [docs/ROADMAP.md](../docs/ROADMAP.md).
