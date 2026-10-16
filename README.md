# Abyss Remote

[![Build and test](https://github.com/jacobkozin/AbyssRemoteConn/actions/workflows/build.yml/badge.svg)](https://github.com/jacobkozin/AbyssRemoteConn/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D4.svg)](https://www.microsoft.com/windows)

Abyss Remote is a local-first Windows remote-desktop host with a responsive browser client for phones, tablets, and computers. It streams the signed-in Windows desktop over first-party HTTPS/WebSockets and accepts authenticated pointer, keyboard, and clipboard input—without a cloud account, hosted relay, CDN, or telemetry service.

> **Security warning:** This is experimental LAN software, not an internet-facing remote-access product. Use it only on a trusted private network. Never port-forward TCP 8443 or expose the host directly to the public internet.

## Development status

Version 0.3.3 (R3.3) is a functional prototype. The source builds and includes automated static checks plus a Windows HTTPS startup smoke test. Actual iPhone/iPad behavior, long-running reliability, hostile-network resistance, and public-internet exposure have not been independently audited. See [Development status](docs/DEVELOPMENT_STATUS.md) for the current validation boundary.

## Highlights

- Self-hosted ASP.NET Core/Kestrel server for Windows 10/11
- Browser client designed for Safari, iPhone, iPad, and desktop browsers
- Client-paced JPEG streaming that prevents stale-frame backlogs
- Up to 24 requested FPS with display-aware resolution and reusable GDI capture buffers
- Multi-monitor selection with Fit, Fill, Stretch, zoom, and pan modes
- Direct-touch and trackpad input, mouse buttons, scrolling, shortcuts, and Unicode text entry
- Touch gestures for tap-to-click, two-finger/right-click, pause-then-drag, and two-finger scrolling
- Immersive mobile view with a compact floating control panel and safe-area-aware desktop margins
- Clipboard transfer and paired-device revocation
- One-time eight-digit pairing code with expiration and per-address rate limiting
- Secure, HttpOnly, SameSite cookies; hashed session secrets; same-origin and CSRF checks
- Local certificate generation using the Windows Current User certificate store
- Loopback-only default; LAN binding and firewall access require explicit actions

## How it works

```text
Mobile/desktop browser
        |
        | HTTPS + authenticated WSS on port 8443
        v
ASP.NET Core host
   |-- pairing, session, CSRF, and device revocation
   |-- JPEG screen-stream WebSocket
   |-- keyboard/mouse control WebSocket
   |-- clipboard REST endpoints
   |
   +--> Windows GDI capture + Win32 SendInput/Clipboard
```

The video socket is client-paced: the browser requests a frame only after it has decoded and rendered the previous one. This bounds in-flight work and prioritizes responsiveness over accumulating delayed frames. More detail is available in [Architecture](docs/ARCHITECTURE.md) and the [API contract](docs/API.md).

## Quick start

### Requirements

- Windows 10 or 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A trusted private network for LAN mode
- A modern browser; mobile controls are primarily designed around Safari on iOS/iPadOS

### Build and verify

1. Clone or download this repository.
2. Double-click `BUILD-AND-TEST.bat`.
3. The script publishes a self-contained Windows host and runs its HTTPS startup smoke test.
4. Use `START-LOCAL-ONLY.bat` first, then open `https://127.0.0.1:8443` on the same PC.

### Use on your private LAN

1. Confirm the Windows network profile is **Private**.
2. Run `ENABLE-LAN-FIREWALL.bat` once to add a Private/LocalSubnet-only firewall rule.
3. Run `START-LAN.bat`.
4. On the host console, note the URL, certificate fingerprint, and one-time pairing code.
5. Trust only the public Abyss Remote root certificate you independently verified, then browse to `https://YOUR-PC-LAN-IP:8443` from the device on the same LAN.

The root certificate is powerful trust material even though the exported `.cer` contains no private key. Review [Security notes](docs/SECURITY.md) before installing it on another device.

## Testing

Static checks require Node.js but no npm packages:

```powershell
node --check src/AbyssRemote.Host/wwwroot/app.js
node tests/source-integrity.mjs
node tests/r3-regression.mjs
node tests/r31-regression.mjs
node tests/r33-regression.mjs
```

Windows build and HTTPS startup verification:

```powershell
.\scripts\Build-Windows.ps1
.\scripts\Smoke-Test.ps1
```

GitHub Actions runs both groups and retains the unsigned Windows build briefly as a workflow artifact.

## Configuration and local data

No `.env` file is required. Runtime state is stored outside the repository under `%LOCALAPPDATA%\AbyssRemote` and in the Windows Current User certificate store.

Local state may contain paired-device names, hashed session secrets, certificate thumbprints, and generated public certificates. It must never be committed, attached to issues, or included in source archives. The repository's `.gitignore` excludes common runtime, certificate, build, and IDE artifacts.

## Technology

| Area | Implementation |
| --- | --- |
| Host | C# / .NET 10 / ASP.NET Core minimal APIs |
| Transport | HTTPS, REST, and authenticated WebSockets |
| Browser client | Vanilla HTML, CSS, and JavaScript |
| Desktop capture | Windows GDI / `System.Drawing` JPEG encoding |
| Input | Win32 `SendInput`, cursor APIs, and Windows Clipboard |
| TLS | Locally generated X.509 root and server certificates |
| Build and automation | PowerShell, batch launchers, GitHub Actions |

## Repository layout

```text
.
|-- .github/workflows/       Static checks and Windows smoke-test CI
|-- docs/                    API, architecture, security, and status notes
|-- scripts/                 Build, launch, firewall, fingerprint, and smoke-test tools
|-- src/AbyssRemote.Host/    Windows host and bundled browser client
|-- tests/                   Dependency-free source/regression checks
|-- BUILD-AND-TEST.bat       One-click build and HTTPS verification
|-- START-LOCAL-ONLY.bat     Safe loopback-only launcher
`-- START-LAN.bat            Explicit private-LAN launcher
```

## Known limitations

- GDI/JPEG capture is CPU-bound and is not GPU H.264, WebRTC, or HDR capture.
- Requested FPS is a ceiling, not a guaranteed delivered frame rate.
- Windows secure desktop, lock/login screens, UAC prompts, and some elevated windows cannot be controlled.
- iOS browser/system edge gestures cannot be disabled by a webpage.
- There is no hosted relay, NAT traversal, role-based access, MFA, audit log, signed binary, or independent penetration test.

## Contributing and security

Read [CONTRIBUTING.md](CONTRIBUTING.md) before making changes. Security reports must follow [docs/SECURITY.md](docs/SECURITY.md); never post pairing codes, session cookies, `settings.json`, certificate private keys, or private network details in a public issue.

## License

Abyss Remote is available under the [MIT License](LICENSE).
