# Security

Abyss Remote lets another device see your screen, type, move the mouse, use a virtual game
controller, read and write the clipboard, and hear the PC's sound. Anyone who gets a session
has all of that. This page describes what protects it, what was fixed for 1.0, and what is
still a known risk. **It has not been independently audited or penetration-tested.**

## Reporting a vulnerability

Use GitHub's private vulnerability reporting for this repository if it is enabled. Otherwise
open a minimal issue asking for a private contact, without exploit details, pairing codes,
cookies, certificates, logs or network addresses.

## Threat model

Defended against:

- **Other devices on the same network** that try to pair, guess codes, or use the API without
  a session.
- **Websites you visit** on a paired phone or on the PC, which could try cross-site requests,
  cross-site WebSockets, DNS rebinding or clickjacking.
- **Accidental exposure,** such as a router port forward set up by mistake.
- **A lost or retired device:** its access can be revoked instantly.

Out of scope: malware running on the PC or on a paired device; anyone who can read the host
console (it shows the pairing code); someone who controls your Wi-Fi equipment; denial of
service by a device you paired yourself.

## Protections

| Protection | How | Code | Tested by |
| --- | --- | --- | --- |
| Not reachable by default | Listens on 127.0.0.1 unless `--lan` is given | `AbyssServer.Build` | smoke test, `LaunchOptionsTests` |
| LAN mode refuses internet addresses | A Kestrel connection filter drops peers outside loopback, RFC 1918, link-local and IPv6 unique-local before the TLS handshake | `NetworkGuard` | `RequestProtectionTests` |
| Firewall | Optional rule: one TCP port, Private profile, local subnet only | `scripts/Firewall-Enable-LAN.ps1` | manual |
| TLS | TLS 1.2/1.3 only, server certificate from a private root CA, keys non-exportable in the Windows user store; reissued from the same root when near expiry or when the LAN address changed | `HostCertificates` | smoke test (pinned), `CertificateCoverage…` |
| DNS rebinding | Unknown `Host` headers get 421 | `RequestProtection.KnownHost` | integration + smoke test |
| Pairing code | 8 digits from a CSPRNG, shown only in the console, 10 minutes, single use, constant-time comparison | `PairingManager` | `PairingManagerTests` |
| Brute force | 5 tries per address per 5 minutes, and the code is destroyed after 20 wrong tries in total (any mix of addresses), so at most 20 guesses out of 100,000,000 | `PairingManager` | `TwentyWrongGuesses…` |
| Session | 256-bit random secret; cookie `Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`; only the SHA-256 hash is stored; constant-time check; 30-day lifetime; expired devices pruned | `StateStore` | `StateStoreTests`, `PairingSetsAHardenedSessionCookie` |
| Cross-site requests | Every state-changing request needs the cookie, the exact `Origin` and a per-device CSRF token (HMAC with a key that changes on every host start) | `AbyssServer` | `StateChangingRequestsNeedCsrf` |
| WebSockets | One gate for all four sockets: cookie + exact `Origin` + CSRF token as subprotocol; failures get HTTP 403 and never reach the desktop | `AbyssServer.RunSocketAsync` | `ControlSocketNeedsCookieOriginAndCsrf`, `VideoSocketHasTheSameSecurityGate`, `AnonymousClientsCannotSeeOrControlThePc` |
| Revocation | Revoking (or `R` on the console) closes the device's live sockets at once; every message also re-checks the device | `SessionRegistry`, `StateStore.IsActive` | `RevokingADeviceDisconnectsItImmediately` |
| Input validation | Control messages are small JSON (8 KB max); every number is clamped; unknown kinds are ignored; held keys, buttons and controllers are released on disconnect | `InputCommand`, `NativeInputSink` | `MalformedControlMessages…`, `GamepadTests` |
| Browser hardening | CSP `default-src 'none'` with same-origin scripts/styles only, `frame-ancestors 'none'`, `X-Frame-Options: DENY`, `nosniff`, `no-referrer`, `no-store`; no inline scripts; untrusted text rendered with `textContent` | `AbyssServer`, `wwwroot` | `UiIsServedWithSecurityHeaders`, client checks |
| No detailed errors | Always runs as Production, so no stack traces reach clients | `AbyssServer.Build` | — |
| Console output | Device names are stripped of control characters before they are stored or printed | `StateStore.CleanName` | `DeviceNamesLoseControlCharacters…` |
| Resource limits | Request bodies 64 KB, 64 connections, per-kind socket caps (6 total, 1–2 per device), at most 2 video frames in flight, bounded audio queue | `AbyssServer`, `SessionRegistry`, `VideoStream` | `AReconnectReplaces…`, `VideoIsFlowControlledByAcks` |
| No secrets in the repo | `settings.json`, certificates, keys, logs and builds are git-ignored; state lives in `%LOCALAPPDATA%\AbyssRemote` | `.gitignore` | review |

## What the 1.0 review found and fixed

These were present in R3.3 (0.3.3) and are fixed in 1.0:

1. **Pairing guesses were only limited per address.** Many addresses together could keep
   guessing. Now 20 wrong guesses in total destroy the code.
2. **LAN mode accepted any source address.** With a mistaken port forward, the login page and
   API faced the internet. Now non-local peers are dropped before TLS.
3. **No `Host` header check.** TLS already stopped DNS rebinding in practice; there is now a
   second layer.
4. **Revocation waited for the next message,** and a revoked phone could keep watching an idle
   screen. Now revoking closes the sockets immediately.
5. **Keys held at a disconnect stayed down on the PC** (Ctrl, Alt, Shift, Windows), and all
   mouse buttons were released blindly, which can even confuse another connection. Now each
   connection releases exactly what it pressed, and also does so when the browser loses focus.
6. **Missing numeric fields defaulted to the minimum of their range:** a `move` message without
   `dx` moved the mouse 400 px left, and a controller message without `ly` would have meant
   "stick fully up". Missing values now mean 0.
7. **Dead connections held the only two video/input slots** for minutes, so a phone that
   switched networks was refused (HTTP 429) when it reconnected. Now there are keepalive
   timeouts, and a reconnect replaces the old socket.
8. **After the host restarted, input never came back** until the page was reloaded (the
   browser kept a stale CSRF token). It now fetches a fresh one on every reconnect.
9. **Device names were printed to the console unfiltered,** so control characters could spoof
   console output. They are now stripped.
10. **`ASPNETCORE_ENVIRONMENT=Development` would have shown stack traces** to clients. It is
    now forced to Production.
11. **Each certificate renewal left the old server certificate in the user store.** The
    previous one is now removed, matched by exact thumbprint, subject and issuer.
12. **The default port 8443 collided with another local HTTPS server** on the development PC,
    and the console told users to open `https://localhost:8443`, which browsers may resolve to
    `::1`, that other server. The default is now 8444, the console prints `127.0.0.1`, and a
    clear error appears when the port is taken.
13. **The CI "syntax check" (`node --check app.js`) did not actually check ES module syntax**
    (Node silently accepted a broken module). The checks now run on `.mjs` copies.

## Known risks and limitations

- **No independent audit or penetration test.**
- **Root certificate trust is powerful.** A device that trusts the Abyss Remote root will trust
  any certificate signed by it. The root's private key cannot be exported, but malware running
  as your Windows user could still use it. The root has no name constraints. Remove it from
  your devices if you stop using Abyss Remote.
- **Cookies are per host, not per port.** Another HTTPS service on the same address (for
  example a different local server on `https://192.168.1.20:8443`) receives the session
  cookie. Only run servers you trust on the same PC address.
- **A stolen session cookie is full access** for up to 30 days, until the device is revoked.
  Sessions are not bound to a device fingerprint.
- **Every paired device has full control.** There are no read-only devices, roles, MFA or audit
  log. The console announces new pairings, revocations and refused connections.
- **Anyone on the local network can reach the pairing page** in LAN mode. Guessing is capped
  as described above, but they can make you press `P` for a new code.
- **Sound streaming sends everything the PC plays** to the paired device, including voice
  chat. It is off until you turn it on in the browser.
- **Clipboard:** a paired device can read the PC's text clipboard whenever it asks.
- **Game mode uses hand-written native interop** (DirectX, Media Foundation, WASAPI, ViGEmBus).
  A bug there could crash the host. Only paired devices can start Game mode, sound or
  controllers.
- **ViGEmBus is a third-party kernel driver** that is no longer actively developed. Installing
  it is your decision; Abyss Remote works without it.
- **Lock screen, UAC prompts and elevated windows** cannot be seen or controlled. That is
  Windows' own protection, and it is a limitation and a safety property at the same time.
- **Binaries are unsigned,** and builds are not reproducible or attested.
- **Pairing limits live in memory.** They reset when the host restarts (which also creates a
  new code).

## Removing access

- Revoke one device from **Devices** in the browser, or press `R` in the host console to
  revoke all of them. Their connections close immediately.
- Stop the host with `Q` or by closing its window.
- Remove the firewall rule as Administrator:
  `Remove-NetFirewallRule -DisplayName 'Abyss Remote (Local Subnet, TCP 8444)'`
  (and `'Abyss Remote (Local Subnet)'` if a pre-1.0 rule exists).
- To retire Abyss Remote completely, also remove the "Abyss Remote Personal Root CA" from
  every device that trusts it and from the Windows user store (check thumbprints before
  deleting anything), and delete `%LOCALAPPDATA%\AbyssRemote`.
