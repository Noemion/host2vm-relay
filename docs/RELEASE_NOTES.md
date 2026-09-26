# Host2VMRelay v0.3.0

A Windows desktop app that transparently routes selected host requests through a virtual machine using SSH tunnels and domain/IP rules, with Clash TUN integration.

## Downloads

- **[Download Windows installer](https://github.com/Noemion/host2vm-relay/releases/download/v0.3.0/Host2VMRelay-0.3.0-Setup.exe)** — recommended offline installer; automatically selects x64, x86, or ARM64. Includes the .NET desktop runtime and application dependencies.
- **win-x64 / win-x86 / win-arm64 Portable.zip** — portable packages for the specified architecture.
- **SHA256SUMS.txt** — checksums for all installer and portable assets.

No preinstalled .NET, Python, Node.js, OpenSSH client, compiler, or network download is required to install the application. The installer creates a per-user installation, optional desktop shortcut, and uninstall entry.

## Changes in this release

- Use a local Clash file provider so Clash stays quiet when Host2VMRelay is not running.
- Store user configuration under Documents\\Host2VMRelay and migrate the previous LocalAppData settings on first use.
- Route local build output to artifacts/ and add a root PowerShell build entry point.
- Uninstall an existing installation before installing the new version.
- Use the compact Host2VMRelay application name consistently.

## Features

- Password and private-key SSH authentication.
- Windows DPAPI encrypted credential storage.
- Tray operation and automatic reconnection.
- Domain, IP and CIDR rules with a local file provider for Clash Verge.
- Fake-IP DNS integration for selected domains.

## Requirements and validation

- Windows 10 (build 14393 or later) or Windows 11 desktop environment; use a maintained, updated Windows version.
- The VM must run an SSH server with TCP forwarding enabled. Clash TUN must be configured separately for transparent routing.
- TCP only; UDP forwarding is not supported.
- x64 and x86 packages tested on Windows 11 x64; installer, application launch and uninstall tested locally. ARM64 is packaged but has not been tested on ARM64 hardware.
- This installer is not code-signed.

After installing, open **接入 Clash** in the application and apply the generated extension script once. [Clash Verge Rev](https://github.com/clash-verge-rev/clash-verge-rev) is required for TUN integration.
