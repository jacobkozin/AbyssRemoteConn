# Contributing

Abyss Remote controls a Windows desktop, so security and clear validation boundaries matter
more than feature count.

## Before submitting a change

1. Never commit or attach `settings.json`, pairing codes, cookies, device names, certificate
   files, private keys, private IP addresses, logs, crash dumps or generated builds.
2. Keep loopback-only as the default. LAN access must stay explicit and limited to private
   networks.
3. Do not weaken TLS, the Origin and CSRF checks, cookie flags, pairing limits, revocation or
   the private-address filter for convenience.
4. Run everything with `BUILD-AND-TEST.bat` (or `.\scripts\Build-And-Test.ps1`): the .NET unit
   and integration tests, the browser client checks, the publish and the published host's
   self-test.
5. If you touch Game mode, also run `dist\win-x64\AbyssRemote.Host.exe --probe-video`.

## Pull requests

- Explain the threat-model impact of changes to authentication, transport, input, clipboard,
  sound, certificates or the firewall.
- New endpoints go through `AbyssServer.RunSocketAsync` (sockets) or the `Authorized` check
  (state-changing REST), and get an integration test that attacks them without a session.
- Native interop: take slot numbers, GUIDs and struct layouts from the Windows SDK headers and
  note the interface and slot next to each call.
- Say which Windows, browser and device combinations you tested.
- Keep `dist`, `bin` and `obj` out of the change; scrub personal paths and addresses from logs.

Do not use a public issue for a vulnerability that could expose someone's screen, input,
clipboard, sound or certificate trust. Follow [docs/SECURITY.md](docs/SECURITY.md).
