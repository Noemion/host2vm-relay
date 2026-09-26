# Changelog

## 0.4.0 — Readable desktop UI and complete script generation

- Add one integrated application icon, generated from a versioned vector source in nine sizes.
- Use DPI scaling, point-sized fonts and wrapping/scrolling layouts for laptop displays.
- Add optional existing-script import, full merged preview, one-click generation/copy and script export.
- Preserve original JavaScript scope and entry-point behavior; replace generated wrappers on regeneration.
- Keep connected rules active when copying a script and isolate diagnostic user data.
- Expand script, icon and synthetic scaled-layout regression checks.

## 0.3.0 — Windows workflow and Clash integration

- Replace the loopback HTTP rule provider with a local file provider.
- Disable forwarding rules while the SSH tunnel is disconnected or the application exits.
- Store settings in Documents/Host2VMRelay and migrate existing LocalAppData settings.
- Route build output to artifacts/ and add a root PowerShell build command.
- Uninstall an existing installation before installing a new version.
- Standardize the visible application name as Host2VMRelay.

## 0.2.1 — Project cleanup

- Organize source, tests, documentation and packaging into dedicated directories.
- Standardize application identifiers and remove prototype configuration migration.
- Use Host2VMRelay credential encryption parameters; previously saved secrets must be entered again.
- Add a direct installer download link and derive package versions from the project.

## 0.2.0 — Offline installer

- Introduce Host2VMRelay with an offline Windows installer.
- Bundle the complete .NET desktop runtime and managed dependencies for x64, x86 and ARM64.
- Add a universal per-user installer with architecture detection, shortcuts and uninstall support.
- Fix publishing options in a versioned profile; keep trimming and Native AOT disabled for WinForms compatibility.
- Include source build scripts, third-party notices, portable packages and SHA-256 checksums.

## 0.1.0 — Initial release

- Windows GUI for SSH SOCKS5 forwarding, tray operation and reconnect.
- Windows DPAPI encrypted credential storage and private-key authentication.
- Editable domain, IP and CIDR rules served locally to Clash Verge Rev.
- Fake-IP configuration for remote DNS through SSH forwarding.
- Fix: generate the TUN bypass from the configured VM IP instead of a fixed subnet.
- Fix: restore connection controls after a disconnect when reconnect is disabled.
- Fix: disable the private-key browse control while connected.
- Self-tests use an ephemeral port so they do not conflict with the running application.
- Public source defaults contain no personal connection details; diagnostic URL is configurable.
