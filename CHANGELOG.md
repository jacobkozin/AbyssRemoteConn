# Changelog

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
