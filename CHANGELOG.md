# Changelog

## 0.2.0 — Offline installer

- Rename application to Host2VM Relay; preserve legacy encrypted settings through migration.
- Bundle the complete .NET desktop runtime and managed dependencies for x64, x86 and ARM64.
- Add a universal per-user installer with architecture detection, shortcuts and uninstall support.
- Fix publishing options in a versioned profile; keep trimming and Native AOT disabled for WinForms compatibility.
- Include source build scripts, third-party notices, portable packages and SHA-256 checksums.

## 0.1.0 — Initial preview

- Windows GUI for SSH SOCKS5 forwarding, tray operation and reconnect.
- Windows DPAPI encrypted credential storage and private-key authentication.
- Editable domain, IP and CIDR rules served locally to Clash Verge Rev.
- Fake-IP configuration for remote DNS through SSH forwarding.
- Fix: generate the TUN bypass from the configured VM IP instead of a fixed subnet.
- Fix: restore connection controls after a disconnect when reconnect is disabled.
- Fix: disable the private-key browse control while connected.
- Self-tests use an ephemeral port so they do not conflict with the running application.
- Public source defaults contain no personal username or company domain; diagnostic URL is configurable.
