# Changelog

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
