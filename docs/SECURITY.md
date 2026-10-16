# Abyss Remote security notes — R3.3 prototype

## Reporting a vulnerability

Use GitHub's private vulnerability-reporting feature if it is enabled for this repository. If it is unavailable, open a minimal issue requesting a private contact channel without including exploit details, pairing codes, cookies, device information, certificates, logs, or network addresses.

If a session secret, pairing code, or private certificate key is exposed, revoke affected devices and rotate the relevant material immediately.

This product is a remote-control server. Its threat model is substantially more sensitive than an ordinary personal web app. A paired client can see the desktop and inject keyboard/mouse input. The default binding is **localhost**. LAN mode is explicit, and the bundled firewall rule is limited to `LocalSubnet` under Windows **Private** network profile.

## Current protections

- Windows CurrentUser certificate store holds the private CA and leaf keys. The exported `.cer` files have public certificates **only**. Private root CA key never goes to mobile clients.
- Private root CA + signed server leaf with IP SAN: manual root trust on iPhone/Windows for browser WSS; no hosted CA service. Key rotation via new root re-trust (automatic root replacement only if missing or too near expiry); leaf renewal reuses same root.
- HTTPS/WSS enforced; secure, HttpOnly, Strict cookies; random 32-byte bearer credentials kept as SHA-256 hashes in OS-user-local settings; tokens cannot be accessed by browser JavaScript.
- One-time physical pairing PIN (8 digits, 10 min). Failed attempts limited to 5 per IP/5 min. Pressing `P` from host console rotates PIN; no remote rotation endpoint.
- Exact Origin validation for pairing, mutating REST, and both WebSocket channels. A per-device HMAC-CSRF challenge is required for control WebSocket and mutating authenticated REST endpoints.
- Session revocation and sign out (30-day token validity). Host console `R` revokes all paired devices without needing an existing authenticated client. Active sessions check device revocation periodically. Frame and input sizes limited. Up to 2 streams and 2 control channels concurrently.
- No third-party APIs, cloud telemetry or CDN assets at runtime.

## Known risks and missing protections

- **Not security-audited or penetration-tested.** Do not port-forward this beta server or expose it to the public internet.
- No full independent relay, NAT traversal, VPN engine, or rate limiting of all authenticated operations. The Windows host should only be used on trusted private networks or behind your own properly configured private VPN.
- PIN-rate-limit state is in memory and rotates after host restart; appropriate for an experimental LAN beta, not sufficient for internet exposure. Paired devices have broad control with no read-only role, MFA or device biometric gating.
- Private root CA trust is powerful: any certificate minted with the Windows-stored root private key could appear trustworthy on devices that install it. Protect the host computer and consider removing the root from trusted stores if you stop using the product.
- No Windows UAC secure desktop, Windows lock screen or login desktop capture/injection. Elevated UI can reject non-elevated injected input due to UIPI. The app is not a Windows service and must run as the signed-in user.
- GDI capture cannot guarantee recording of protected/DRM content and may fail with some GPU configurations. It is not hardware-accelerated video encoding and has no HDR-specific path.
- Host logs transport failures to console. Pairing codes and certificate hashes appear in the Windows console. Never publish screenshots of the console while codes are active.
- Host clipboard GET can reveal text secrets if you explicitly click **Copy PC text**. Mobile devices must be kept secure. Device revoke is recommended after a lost phone.
- No audit log, signed release binary, code signing or reproducible build pipeline attestation at this stage.
- One process listens on TCP 8443 when LAN mode is selected, even if firewall configuration fails or has broader conflicting rules. Review Windows Firewall before using it.
- Exported certificates include SAN IPs at issue time; if the LAN address changes you must renew the server leaf.

## Before considering public access

At minimum: security review and dependency audit, hardened session lifecycle, authenticated permissioned sessions, connection rate limiting, reliable secure relay/NAT traversal or VPN, host-to-client key verification, automated tests on Windows/iOS, performance monitoring, and independent vulnerability testing. The default restriction to LAN should remain even after these features are introduced.

## How to remove access

- Revoke paired devices from the **Devices** panel, or sign out to revoke the current device. Press `R` on the host console to revoke all paired devices at once.
- Shut down host with `Q`, or close console.
- Remove optional Windows firewall rule with admin PowerShell: `Remove-NetFirewallRule -DisplayName 'Abyss Remote (Local Subnet)'`.
- If retiring Abyss Remote completely, manually remove the private root CA from trusted stores on iOS/Windows and delete `%LOCALAPPDATA%\AbyssRemote` plus associated certificates from your CurrentUser personal store as appropriate. Never delete other certificates by pattern without checking thumbprints.
