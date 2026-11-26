# Changelog

## 1.2.0 (2026-11-23)

- **Away from home.** A new setting, "Let added devices connect away from home", with an away
  address such as `remote.example.duckdns.org`. A reverse proxy on the same PC (on the
  development PC, the Gaming Hub's Caddy) forwards that public HTTPS address to Abyss Remote
  over loopback. That address works at home too. A device pairs there with the same one-time
  code as at home, or takes itself there from home with a code of its own, and has a separate
  away key there. The root certificate is not offered at the away address. Revoking a device
  ends both keys, and turning the setting off forgets every away key. See
  [Security](docs/SECURITY.md#what-12-added).
- **A new look for the browser client: the Abyss game style.** Ocean-coloured panels with one
  dark outline and a lip under everything that can be pressed; teal for the main action and
  for anything switched on, coral for what cannot be undone. Fixed along the way, on phones:
  - The home screen lists one monitor per row and shows three game covers across, which
    makes it about 30% shorter with the demo's three monitors and fourteen games.
  - The Controls panel is Keyboard, six even tiles, four even switches, the menus and a
    folded "Touch gestures". It fits the screen without scrolling, upright or sideways; it
    used to be cut off, with wrapped switch names and one menu left over.
  - The Monitors sheet over the stream shows three monitors without being cut off.
  - Buttons that belong together are one width and their labels fit: "Lock" and "Sleep"
    (the PC's own wording is the tooltip), "Get from PC", "Send to PC".
  - The keys under "More" stand in even rows.
  Checked in Chromium against the demo server at 375 and 812 px wide with touch and at
  1280 px, not yet on a real phone.
- **The Windows window has the same look.** Its cards are lit from above and stand on the
  ocean page, each with one dark outline and a lip. Buttons sink into their lip when pressed.
  Teal is the main button of a card and a ticked box, coral is "Revoke all" and the edge of
  the "not running" card. Labels are aqua, text boxes are set into their card, and on
  Windows 11 the title bar and the window's frame take the colours of the header. Along the
  way:
  - The Activity list has a scroll bar only while there is more than fits. It is scrolled
    to its newest line in a way that also works while the window is not on screen; the old
    way did nothing in a window that had not been shown yet.
  - The window opens 160 pixels taller (1100 x 1100 at 100%), where the screen has the
    room. The lips and the space under the header made the cards a little taller, and with
    away access and three devices everything is still visible without scrolling.
  Checked in off-screen pictures at 100% and 150%, including hovered and pressed buttons, a
  long activity list and the single column of a narrow window, and by the layout tests at
  100, 125, 150 and 200%. The rebuilt program was started on the development PC, where the
  window opened with every card in place and no scroll bar.
- The window lists which devices have away access, and the away address sits under the port
  in Settings.
- **Pairing is pairing, at home and away.** Away access first had a set-up of its own:
  several steps on the device, then an away code from a box in the window. Now there is one
  code and one screen.
  - The eight-digit pairing code also works at the away address. A device that pairs there
    is done: the address works at home too, and it needs no certificate.
  - While the away address works, the window's Connect card shows it as the address to
    open, and the QR code points to it.
  - The pairing screen at home has a switch, **Also use this device away from home**, which
    is on unless someone turns it off. After pairing, the browser goes on to the away
    address and is signed in there too. A browser that was paired before has the same as
    one button on its home screen.
  - An icon on an iPhone's or iPad's Home Screen keeps its own sign-in and cannot be sent to
    another address, so the pairing screen says to make the icon first, from the away
    address.
  - In the window, Devices shows "away access" or "no away access yet" for each device.
  This changes who can try a code. With away access on, anyone who knows the address can
  guess at the pairing code while one is valid. Five tries per address in five minutes and
  twenty wrong tries in total, after which the code is destroyed, leave a guess a chance of
  at most 20 in 100 million per code. So with away access on the window makes no code when
  it starts, and turning away access on drops the code that is on screen: a code exists only
  after **New code** was clicked. See [Security](docs/SECURITY.md#what-12-added).
  Tried in Chromium at phone size and at 1280 px against the demo server, through a stand-in
  for the proxy and with a pretend Home Screen icon. Not on a real phone.
- **Abyss Remote says when the away address does not work.** The address is forwarded by
  another program on the PC, the reverse proxy. When that was not running (after a restart
  of the PC, say), the address did not load and nothing said why. Now the host asks the
  address on the PC itself, at its start and every half minute, whether it leads to Abyss
  Remote. The window says so under Connect and Phone setup, the activity list notes each
  change, the tray icon announces when the address stops working, and meanwhile no browser
  is sent there: the switch and the button are not offered. While the address works, most
  rounds only look whether the proxy still listens, and ask through it every ten minutes,
  so the proxy's access log does not fill up. The check cannot see whether the address
  reaches the PC from outside.
- Requests that carry proxy headers (`X-Forwarded-For`, `Forwarded`) for any name other than
  the away address are refused with 421.
- The demo server takes `ABYSS_REMOTE_DEMO_AWAY=<address>` to try the away screens behind a
  test proxy, and a file called `away-down` in its folder pretends that the proxy is closed.

- Phone cursor feedback is drawn immediately, independently of desktop video. Host cursor
  snapshots reconcile the position without replaying stale movement.
- Trackpad gestures retain movement between display frames, including fractional pixels.
  They stay within the selected monitor and do not apply Windows mouse acceleration.
- Cursor snapshots use the existing authenticated control socket. Legacy clients retain
  the cursor embedded in Desktop-mode video. Actual screen updates still depend on
  capture rate, network latency and decoding; this change does not eliminate those delays.
- Fixed: the phone showed no mouse pointer at all while a cursor-hiding program ran on the
  PC. Such a program gives a system cursor a blank picture without changing its handle, and
  the host only looked at a cursor again when the handle changed. The host now looks about
  ten times a second and reports a blank picture as "no pointer", and the page keeps the
  pointer in the video until it has a picture it can draw.
- Zoom on phones. A pinch anywhere on the stage zooms around the fingers and carries the
  picture with them, up to twice the size at which text is as large as in a phone app (at
  least 400%; it was 250% and always went back to the middle). A zoom button goes to that
  readable size at the pointer, and back. Zoomed in, Trackpad keeps the pointer in view and
  Direct touch moves the picture with one finger; two fingers always scroll. Trackpad speed
  and two-finger scrolling follow the zoom, and Desktop mode asks for a sharper picture as
  the zoom grows, without the "Switching…" overlay.
- Typing on phones. The keyboard sheet starts as a slim bar above the phone's keyboard. Each
  key goes to the PC as it is typed, Backspace and Enter included; a correction made by the
  phone's keyboard is sent as Backspaces and the new ending. The picture ends above the bar
  and zooms in on the pointer (the new "Zoom to type" switch turns that off). A held Ctrl,
  Alt, Shift or Win makes a shortcut of the next typed letter and is then released. "More"
  holds F1–F12, the shortcuts, the box for longer text and the clipboard.
- Touches are told apart by their own time stamps, so a page that is busy for a moment cannot
  turn a swipe into a drag or a tap into a right-click. In Direct touch the pointer waits
  until a touch is known to be a tap, a hold or a swipe, so the first finger of a pinch no
  longer moves it.
- `GET /api/v1/displays` also reports each monitor's Windows scaling (`scale`). The picture's
  sharpness is adjusted in Desktop mode only; Game mode keeps the size chosen in its settings.
- 424 tests pass and 2 are skipped; eleven Node check files (`view-checks.mjs` and
  `typing-checks.mjs` are new). The demo server has a mouse pointer and, like the real
  screen, reports when nothing changed. The zoom, the typing bar and the pointer fix were
  tried in a phone-sized Chromium with emulated touches and then used on an iPhone, where
  they worked. That was everyday use, not a pass through every gesture.

## 1.1.0 — 2026-11-14

Windows app
- Abyss Remote is now a Windows app with a window and a tray icon instead of a console. The
  program is `AbyssRemote.exe` (was `AbyssRemote.Host.exe`). The window shows the address, the
  pairing code, paired devices, a "Phone setup" checklist and the settings.
- QR code in home-network mode: scanning it opens the address with the pairing code filled in.
- Settings are saved (`preferences.json`): home-network mode, port, keep running in the tray.
  "Start with Windows" is a separate switch, off by default. One copy runs at a time; a
  second start shows the running window.
- New options `--local`, `--console` (the old console screen) and `--minimized`.
- `GET /AbyssRemote-RootCA.cer` lets a phone download the root certificate's public part; the
  window shows the fingerprint to compare.
- New app icon (`tools/make_icon.ps1`) and `CREATE-DESKTOP-SHORTCUT.bat`.

Browser client
- New home screen after pairing: monitors, Steam games, files, lock and sleep, clipboard,
  paired devices. Reworked pairing screen with help for the first time on a phone.
- Monitor picker with model names, refresh rates and live previews, also inside a stream.
  Monitor numbers are fixed now: the primary monitor is 0, the others follow left to right.
- Screenshot: save a full-resolution picture of a monitor.
- Watch only: see and hear the PC without sending any input.
- Special keys (Ctrl, Alt, Shift and Win that stay held, Esc, Tab, Del, Ins, Home, End, PgUp,
  PgDn, arrows, PrtSc, F1–F12) and one-tap shortcuts in the keyboard sheet; pinch zoom with
  two fingers.

Steam, files and the PC
- Steam library: installed games with cover art; start a game or Big Picture from the browser.
- File transfer through one folder, `Downloads\Abyss Remote`: send, download, delete, up to
  2 GB per file, nothing overwritten.
- Lock the PC or put it to sleep.

Streaming and controllers
- The bitrate can change while streaming (`bitrate <kbps>` on the video socket). New default
  "Automatic (2–35 Mbps)", which follows the delivery time.
- Controller rumble: vibration from the game is sent to the browser and played on the physical
  controller.
- On-screen gamepad ("Touch gamepad") for phones and tablets.

Security
- Every new route that changes or does something on the PC needs cookie + exact Origin + CSRF.
- File names are checked and matched against a folder listing, never turned into paths;
  uploads carry the download mark; downloads are always sent as attachments.
- Host actions are a fixed list of two. A Steam game is started only by the number of an
  installed game, with `steam.exe` run directly and no shell.
- The pairing code in a QR address is a fragment, removed from the address bar at once.
- Sockets get close code 1012 when the host stops or restarts, so browsers reconnect.
- See docs/SECURITY.md for the new risks.

Tooling
- The local build now makes a small `AbyssRemote.exe` (under 1 MB) that runs on the installed
  .NET 10 runtime and downloads nothing. `BUILD-WINDOWS.bat -SelfContained` or
  `BUILD-AND-TEST.bat -SelfContained` makes the standalone program (about 135 MB), which is
  what the GitHub Actions build produces.
- 414 tests pass and 2 are skipped (was 102); eight Node check files (was one); a demo server
  for trying the browser client against pretend monitors, games and files; `--ui-snapshot`
  draws the window to PNG files.

Not yet tested
- Anything on a real iPhone, iPad or touch screen, including scanning the QR code.
- Home-network mode on the development PC.
- Controllers, the on-screen gamepad in a game and rumble: ViGEmBus is not installed there.
- Lock, sleep, starting a Steam game and Big Picture for real.
- Mouse and keyboard on the real desktop through the reworked client (verified live for 1.0).

## 1.0.0

Security
- Pairing code destroyed after 20 wrong guesses in total (on top of the per-address limit).
- LAN mode refuses public IP addresses before TLS; `Host` header allowlist (DNS rebinding).
- Every WebSocket requires cookie + exact Origin + CSRF; revocation closes live sockets at once.
- Device names stripped of control characters; always runs as Production (no stack traces).
- Server certificate reissued from the same root when the LAN address changes; old leaf removed.
- See docs/SECURITY.md for the full list of R3.3 issues fixed.

Streaming
- Desktop mode: frame pacing fixed (about 8 fps to 26–27 fps on the dev PC), unchanged frames
  skipped, mouse cursor drawn, clear message on the lock screen / UAC.
- New Game mode: DXGI Desktop Duplication, GPU color conversion, hardware H.264 through Media
  Foundation, WebCodecs in the browser, 60 fps with at most two frames in flight, pointer drawn
  by the browser, live stats, automatic fallback to Desktop mode. `--probe-video` diagnostics.
- New: PC sound (WASAPI loopback to an AudioWorklet with an adaptive buffer).
- 1 ms timer resolution while streaming (Windows' 15.6 ms default capped 60 fps at about 32).

Input
- Scan-code keyboard (layout- and game-friendly), extended keys, back/forward buttons, smooth
  wheel, horizontal wheel, Enter/Tab in typed text, "Lock mouse" for games.
- No stuck keys, buttons or controllers after a disconnect or focus loss.
- New: controllers through a virtual Xbox 360 pad (ViGEmBus, installed by the user).
- Missing numeric fields count as 0 (a `move` without `dx` used to jump 400 px left).

Reliability and UI
- Reconnect fetches a fresh CSRF token (input never came back after a host restart), keepalive
  timeouts, newest connection per device wins, clear close codes.
- Default port 8444 (8443 collided with another local server), `--port`, clear port-in-use error.
- Remembered viewer settings, clipboard in the phone keyboard sheet, Home Screen web app,
  Fullscreen API + Keyboard Lock, quick panel no longer clipped on narrow phones, stylesheet
  consolidated, ResizeObserver layout.

Tooling
- MSTest suite (102 unit and integration tests against the real HTTPS server with fakes)
  replaces the regex-on-source checks; Node client checks; one-command `BUILD-AND-TEST.bat`.

## 0.3.3 — R3.3

- Preserved Fit mode as the default through immersive-mode and orientation changes, preventing accidental edge cropping.
- Added tap-to-click, two-finger/right-click, pause-then-drag, and two-finger scroll gestures.
- Added cancellation cleanup so interrupted touch gestures cannot leave a mouse button held or produce an unintended click.
- Inset the immersive desktop and floating controls from device safe areas while keeping the stream dominant.
- Added cache-busted browser assets and R3.3 regression coverage.

## 0.3.2 — R3.2

- Reworked the mobile experience around an immersive, desktop-first view.
- Added a compact floating controls panel for input mode, display, scaling, keyboard, mouse buttons, scrolling, and reconnect actions.
- Improved portrait and landscape layouts while acknowledging browser-controlled iOS system chrome.

## 0.3.1 — R3.1

- Raised the browser client to a high-clarity JPEG profile with display-aware resolution up to 3,200 pixels wide.
- Added an immersive mobile frame with controls positioned away from iOS system gesture edges.
- Added a mobile keyboard drawer and text/Tab/Enter/Backspace controls through the authenticated input channel.
- Added R3.1 regression checks for clarity, immersive layout, keyboard wiring, and version consistency.

## 0.3.0 — R3

- Switched screen streaming to a client-paced, one-frame-in-flight protocol.
- Reused Windows capture and JPEG buffers across frames.
- Raised the requested frame-rate ceiling to 24 FPS.
- Added Fit, Fill, Stretch, zoom, and two-finger pan behavior.
- Added direct-touch and trackpad input modes, immersive controls, reconnect controls, and frame telemetry.
- Added safe source-integrity and regression checks.

## Earlier prototype milestones

- Added local X.509 certificate generation, physical pairing codes, hashed session secrets, secure cookies, CSRF/origin checks, device revocation, clipboard access, and Private/LocalSubnet firewall tooling.
