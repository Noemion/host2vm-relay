# DPI acceptance

## Automated regression gate

`./build.ps1 -Test` runs independent 100%, 125%, 150%, 175% and 200% checks. Each process uses isolated settings and rule files under `artifacts/checks/`; personal credentials, the live Clash rule file and Windows display settings are not changed.

The tests inject WM_DPICHANGED into the real WinForms windows, checking DeviceDpi and proportional text metrics, full button captions, labels, editor line height, sibling overlap, scroll reachability, keyboard focus, window work-area bounds and exact icon sizes. Both the four main pages and the script dialog are covered. Three DPI round trips exercise repeated scaling and font restoration. JSON metrics, top/scrolled screenshots and a Markdown summary are retained even when an assertion fails. A completed screenshot alone is not a pass.

Message injection is NOT a physical display change. Every report records both WinForms DeviceDpi and native GetDpiForWindow. Hosted CI additionally runs native 100% acceptance when its desktop is at 96 DPI. High-DPI native and cross-monitor checks must not be reported as passed on that runner.

## Native monitor acceptance

On a Windows test device, set the desired scale through Windows Display Settings and select the appropriate monitor. Then run, for example:

```powershell
./scripts/test-dpi.ps1 -Native -Scale 200 -Monitor 0
```

Repeat with 125, 150 and 175 after changing Windows settings. Native mode sends no synthetic DPI messages and requires the actual window DPI to be 120, 144, 168 or 192 respectively. A mismatch is reported as BLOCKED with a nonzero exit code, not silently substituted with a simulation. `-Monitor` is the zero-based Screen.AllScreens index; each report records the actual device name.

Inspect text sharpness, tooltips, native file dialogs, tray menus and all top/scrolled screenshots. Drag the main window and script dialog between displays with different scales and back; confirm text remains sharp, all actions are reachable and neither font size nor window size accumulates after repeated moves. These physical checks remain an operator acceptance item until evidence from the corresponding devices is recorded.

## Evidence

- `artifacts/checks/dpi-message-{100,125,150,175,200}/report.json`
- `artifacts/checks/dpi-message-summary.md`
- `artifacts/checks/native-{scale}/report.json`
- The same folders contain full-window top and scrolled PNGs.

Native and message-injection results are intentionally separate. A successful regression gate does not certify a native DPI plateau that was unavailable on the runner.
