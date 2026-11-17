# Contributing

Abyss Remote controls a Windows desktop, so security and clear validation boundaries matter
more than feature count.

## Before submitting a change

1. Never commit or attach `settings.json`, pairing codes, cookies, device names, certificate
   files, private keys, private IP addresses, logs, crash dumps or generated builds. A
   screenshot of the real window shows the pairing code and its QR code; use the pictures
   from `--ui-snapshot` instead, which are drawn with sample data.
2. Keep loopback-only as the default. Home-network access must stay explicit and limited to
   private networks.
3. Do not weaken TLS, the Origin and CSRF checks, cookie flags, pairing limits, revocation or
   the private-address filter for convenience.
4. Run everything with `BUILD-AND-TEST.bat` (or `.\scripts\Build-And-Test.ps1`): the .NET unit
   and integration tests, the browser client check files (`tests\web\*-checks.mjs`), the
   publish and the published program's self-test. The publish makes a small
   `dist\win-x64\AbyssRemote.exe` that runs on the .NET 10 runtime installed on the PC, and it
   downloads nothing. Add `-SelfContained` for the standalone program that the GitHub Actions
   build makes; its first build fetches Microsoft's runtime packs from nuget.org.
5. If you touch Game mode, also run `START-LOCAL-ONLY.bat --probe-video`. It starts
   `dist\win-x64\AbyssRemote.exe --probe-video` and waits for it.
6. If you touch the browser client, try it against the demo server (`ABYSS_REMOTE_DEMO=1`,
   see [tests/README.md](tests/README.md)). It has pretend monitors, games and files, so every
   button can be pressed, Lock, Sleep and Delete included.
7. If you touch the window, look at it with the snapshot tool. After `dotnet build`, run
   `dotnet src\AbyssRemote.Host\bin\Debug\net10.0-windows\AbyssRemote.dll --ui-snapshot <folder>`.
   It draws the window into PNG files at 100% and 150% without showing anything on screen.

## Pull requests

- Explain the threat-model impact of changes to authentication, transport, input, clipboard,
  sound, certificates, the firewall, file transfer, host actions or anything that starts a
  program.
- New endpoints go through `AbyssServer.RunSocketAsync` (sockets) or the `Authorized` check
  (state-changing REST), and get an integration test that attacks them without a session.
  `ForgedRequests.AssertAllRefusedAsync` sends one request in seven forged forms.
- A new feature goes in `Features/`, maps its own endpoints, and reaches Windows through a
  small seam that the tests can replace. Nothing a request carries may become a path, a
  program name or a command line.
- New logic in the browser client gets a Node check in a `tests\web\<topic>-checks.mjs` file.
- The host project has no NuGet packages; only the test project has them (MSTest). Keep it
  that way: it is why the default build needs no download.
- Native interop: take slot numbers, GUIDs and struct layouts from the Windows SDK headers and
  note the interface and slot next to each call.
- Say which Windows, browser and device combinations you tested, and what you could not test.
- Keep `dist`, `bin` and `obj` out of the change; scrub personal paths and addresses from logs.

Do not use a public issue for a vulnerability that could expose someone's screen, input,
clipboard, sound, files or certificate trust. Follow [docs/SECURITY.md](docs/SECURITY.md).
