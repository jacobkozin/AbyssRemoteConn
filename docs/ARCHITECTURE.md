# Architecture

Abyss Remote is a single Windows host process with a bundled browser client. It has no cloud control plane or third-party runtime service.

## Host startup

`Program.cs` creates a state store under `%LOCALAPPDATA%\AbyssRemote`, loads or creates the local certificate chain, and starts Kestrel on TCP 8443. The default listener is loopback. `--lan` explicitly binds all local interfaces; Windows Firewall access is managed separately by the LocalSubnet-only helper.

Static HTML, CSS, and JavaScript are served from the application's published `wwwroot`. The host adds restrictive content, frame, referrer, permissions, and cache headers before serving routes.

## Authentication and request protection

The host displays an eight-digit, single-use pairing code generated from a cryptographic RNG. A successful pairing returns a random 256-bit session secret in a Secure, HttpOnly, SameSite=Strict cookie. Only the SHA-256 hash is persisted.

Authenticated mutations require an exact same-origin request and a per-device HMAC-derived CSRF value. The control WebSocket carries the CSRF value in its requested subprotocol list. Device revocation is checked during active streams and control sessions.

## Video path

Each stream owns a `DesktopCaptureSession`. It captures the selected monitor with Windows GDI, optionally scales it, and encodes JPEG frames with reusable bitmap, graphics, encoder, and memory buffers.

The browser requests the next frame only after rendering the previous one. The host also enforces the configured FPS ceiling. This keeps at most one image in flight per client and avoids building a queue of stale desktop frames.

## Input and clipboard path

The authenticated control WebSocket accepts bounded JSON messages for absolute/relative pointer motion, buttons, wheel events, keyboard events, and Unicode text. The host maps them to Win32 `SendInput` calls. Clipboard REST endpoints use a bounded STA thread and limit text to 4,096 characters.

## Local persistence

`StateStore` writes paired-device metadata, token hashes, and certificate thumbprints to `%LOCALAPPDATA%\AbyssRemote\settings.json`. Certificate private keys remain in the Windows Current User certificate store. Exported `.cer` files contain public certificates only, but they still must not be committed because they identify a particular installation and trust root.

## Build integrity

The launcher fingerprints editable host source and automatically rebuilds when the published executable is missing or stale. The smoke test starts the actual HTTPS host, pins the exact generated server certificate, verifies the bundled UI, and confirms protected routes reject anonymous requests.
