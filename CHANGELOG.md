# Changelog

## 0.4.3 — Script preview rendering acceptance

- Normalize CR, LF and CRLF to Windows hard line breaks when displaying imported or generated scripts.
- Use the same preview preparation path for normal operation and tests; validate native edit-control line counts and lossless source extraction.
- Reset preview caret and report a generated result after stale-output invalidation is resolved.
- Retain the full 100/125/150/175/200 DPI matrix and the native-monitor validation guard introduced in 0.4.2.

## 0.4.2 — DPI acceptance and high-scale layout fixes

- Add independent 100%, 125%, 150%, 175% and 200% layout acceptance with DPI message injection, text metrics, caption-fit, overlap, scroll reachability, focus and repeated DPI round-trip assertions.
- Add strict native monitor checks and a negative guard that refuses native 200% certification on a 96-DPI desktop. Native and injected evidence remain distinct.
- Keep explicit title and editor fonts proportional to the form font across DPI changes.
- Fix inaccessible rule-save controls caused by fill docking inside a scrollable tab; wrap credential guidance separately.
- Produce exact requested icon dimensions, including 28/56 pixels at 175%, and update the script dialog icon on DPI changes.
- Capture painted desktop windows rather than un-clipped DrawToBitmap reconstructions; retain JSON metrics and screenshots under artifacts/checks.

## 0.4.1 — Clash settings compatibility and release maintenance

- Publish the managed TUN settings fix previously available only in main and Actions artifacts.
- Preserve incoming Clash Verge GUI-owned TUN values, including when importing legacy scripts that mutate arrays or replace the TUN object.
- Keep unrelated user configuration and custom TUN options intact; configure DNS hijacking and VM route exclusions in the Clash settings interface.
- Add regression coverage for managed TUN fields and clarify script regeneration after upgrading.
- Upgrade official workflow actions to Node.js 24 runtimes and pin their release commits; keep Node.js 22 for project script tests.
- Synchronize application, installer and manifest versions and show explicit published/skipped release summaries.

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
