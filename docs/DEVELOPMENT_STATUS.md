# Development status

Version: 0.3.3 (R3.3)

Primary platform: Windows 10/11 x64

Status: experimental private-LAN prototype

## Implemented

- Self-contained .NET Windows host and bundled responsive browser client
- Local HTTPS certificate chain and certificate-fingerprint display
- Single-use physical pairing code with expiry and per-address attempt limiting
- Hashed session-secret persistence, secure cookie, origin checks, CSRF, and revocation
- Client-paced JPEG screen streaming and reusable GDI capture buffers
- Multi-monitor selection and responsive Fit, Fill, Stretch, zoom, and pan behavior
- Direct-touch and relative-trackpad pointer input
- Mouse, wheel, key, Unicode text, and clipboard controls
- Paired-device listing, per-device revocation, sign-out, and console-wide revocation
- Loopback default and explicit private-LAN firewall helper
- Dependency-free source/regression tests and an in-process HTTPS startup smoke test

## Automated validation

- JavaScript syntax validation
- Browser HTML/JS/CSS wiring and security-invariant checks
- R3 streaming, layout, input, and repository-safety regressions
- R3.1 clarity, immersive-layout, keyboard, and version regressions
- R3.3 touch-gesture, Fit-default, safe-area, and version regressions
- Windows self-contained publish
- Real Kestrel HTTPS startup with certificate pinning
- Public route/UI response checks and anonymous protected-route rejection

## Manual validation still required

- Pairing and trust setup on current iOS/iPadOS and Safari versions
- Pointer accuracy across multiple DPI scales and monitor arrangements
- Long-running stream/reconnect behavior under poor Wi-Fi
- Certificate renewal and trust migration
- Device revocation during active screen and control sessions
- Behavior across UAC boundaries, sleep/resume, lock, and network changes

## Known gaps

- No independent security audit or penetration test
- No GPU capture/encoding, WebRTC, HDR, or protected-content support
- No MFA, permissions/roles, audit log, signed binary, installer, or reproducible release attestation
- No built-in relay, NAT traversal, or internet-safe deployment model

The default loopback restriction and private-LAN warning are deliberate product boundaries, not temporary setup inconveniences.
