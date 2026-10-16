# Testing and validation

`node --check src/AbyssRemote.Host/wwwroot/app.js` validates JavaScript syntax.

`node tests/source-integrity.mjs` checks HTML/JavaScript link integrity and presence of critical security defenses. These are **static smoke checks**, not proof that the C# code compiles or that the system is secure.

On Windows with .NET 10 SDK:

```powershell
.\scripts\Build-Windows.ps1
.\scripts\Smoke-Test.ps1
```

The PowerShell wrapper invokes the built host with `--smoke-test`. The host itself starts the real HTTPS server, checks the `info` endpoint and bundled homepage, ensures protected APIs return `401`, and stops. The managed test HTTP client **pins the exact certificate the running host generated**. No PowerShell TLS callbacks or global trust-policy changes are used, and browser trust is unaffected.

Additional **manual** testing needed on Windows + Safari/iOS: persistent TLS private key on restart, root trust, pairing success and expiration, screen update, mouse/keyboard, clipboard, multi-monitor selection, stream reconnect, and revoked-device denial. No Windows runtime access was available during initial code generation.

R3 checks: `node tests/r3-regression.mjs` verifies frame pacing, captured buffer reuse, immersive/fit UI, input behavior, and repository exclusions.

R3.1 checks: `node tests/r31-regression.mjs` verifies the high-clarity stream profile, immersive mobile frame, and keyboard controls.

R3.3 checks: `node tests/r33-regression.mjs` verifies the current release version, Fit-preserving layout, safe-area-aware margins, and touch gestures.
