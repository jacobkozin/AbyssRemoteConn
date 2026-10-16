# Contributing

Abyss Remote controls a Windows desktop, so security and clear validation boundaries matter more than feature count.

## Before submitting a change

1. Never commit or attach `settings.json`, pairing codes, cookies, device names, certificate files, private keys, private IP addresses, logs, crash dumps, or generated builds.
2. Preserve loopback-only behavior as the default. LAN access must remain explicit and limited to trusted private networks.
3. Do not weaken HTTPS, origin validation, CSRF checks, cookie flags, pairing expiry, rate limits, or revocation behavior for convenience.
4. Run the static checks:

   ```powershell
   node --check src/AbyssRemote.Host/wwwroot/app.js
   node tests/source-integrity.mjs
   node tests/r3-regression.mjs
   node tests/r31-regression.mjs
   node tests/r33-regression.mjs
   ```

5. On Windows with the .NET 10 SDK, run `BUILD-AND-TEST.bat`.

## Pull requests

- Explain the threat-model impact of authentication, transport, input, clipboard, certificate, or firewall changes.
- Add regression coverage for browser/host protocol changes.
- Document which Windows, browser, and device combinations were tested.
- Keep generated `dist`, `bin`, and `obj` trees out of the change.
- Scrub personal paths and network details from diagnostics.

Do not use a public issue for a vulnerability that could expose a user's screen, input, clipboard, credentials, or certificate trust. Follow the private-reporting guidance in `docs/SECURITY.md`.
