# Host2VMRelay v0.4.0

## Changes

- Add a unified graphite, mint-green and amber app emblem. The vector source produces nine ICO sizes (16–256 pixels) during the build, and the same icon is used for the executable, window, tray, shortcuts and installer.
- Enable per-monitor DPI layout with a 96-DPI design baseline and 12-point body/editor fonts. Content wraps or scrolls instead of shrinking the text to fit a laptop screen.
- Add direct script generation and an optional existing-script import/merge window with full preview, one-click generation/copy and Save As.
- Preserve user functions and lexical scope; run the original script before applying relay integration. Re-importing a generated script replaces its managed wrapper rather than nesting another copy.
- Report invalid return values, asynchronous entry points and missing main functions when Clash evaluates the script. User scripts are never executed by the desktop application. YAML subscription configuration is not an accepted script format.
- Fix script preparation incorrectly disabling forwarding rules while the tunnel is connected.
- Keep all generated icons, binaries, intermediate files, checks, screenshots and release packages under artifacts/.

## Upgrade

Install the new version, then generate and apply the complete Clash extension script once. For custom logic, choose the merge option and paste/import the original JavaScript. User configuration remains in the current Windows user's Documents/Host2VMRelay directory. Password encryption and the SSH host-key trust store are unchanged.

## Build and checks

Use `./build.ps1 -Test` in Windows PowerShell to build and run the checks (Node.js is needed only for JavaScript tests). The existing Windows workflow also creates 100%, 125%, 150% and 200% synthetic layout screenshots. These layout tests are not a substitute for native DPI changes across physical monitors. End-to-end routing through the user's VM still requires their own environment.

The runtime remains bundled in release packages. The application and installer are not code-signed.
