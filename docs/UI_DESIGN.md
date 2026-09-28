# Desktop UI design

## Direction

A restrained desktop workspace: neutral canvas, white surfaces, graphite text and a muted green accent. The visual language is inspired by simple utility applications, while retaining Windows title bars, native input behavior and keyboard access. Do not imitate macOS window controls or use platform-restricted fonts and symbols.

## Design tokens

- Body and editor default: 9pt, configurable from 8 to 10.5pt. Heading sizes follow the body font proportionally.
- ContentScale=0.8 adjusts logical design dimensions; the operating system effective DPI stays unchanged.
- Static whitespace uses SpacingScale=0.6. Cards use 13 logical pixels of padding and 11 between groups.
- Typography, input frames, action buttons, editor frames and cards share factories. Native title-bar and taskbar icon sizes remain platform-driven.
- The main window and standalone script workspace share a minimum design size of 1100 × 700, adjusted by content scale, font size and Windows DPI and bounded by the monitor work area. Reducing spacing cannot shrink the window below the space required by larger text.
- Keep visible focus indication, hover/pressed/disabled states and normal Windows input/password/file picker behavior.

## Navigation and pages

Six destinations: Connection, Rules, Clash integration, Logs, Settings and About. Navigation stays on the left, with About at the bottom. Alt+1 through Alt+6 and Ctrl+Tab/Ctrl+Shift+Tab navigate pages. About returns to the previous page and provides repository links, release checks and verified installer downloads.

`PageHost` owns persistent page panels and switches their visibility in one layout transaction. The shell owns navigation labels and shortcuts. No hidden native tab header or `TCM_ADJUSTRECT` override is involved. Pages retain input and scroll position when hidden.

Navigation reveals the new page with a 120 ms ease-out fade. `PageFade` renders one transient viewport snapshot over the canvas; live native input controls remain underneath without opacity or geometry changes. A UI timer requests frames only during the transition. New navigation replaces the previous transition; input, hiding, resizing and disposal cancel it immediately. The snapshot is capped at 32 MiB and released at completion. Failed or slow captures use normal painting. Windows reduced-motion and high-contrast preferences disable the fade. No screen capture or per-frame layout is involved.

`PageScrollPanel` delegates scrolling and clipping to Windows. After visibility, scroll or size changes, it queues one invalidation of the complete child hierarchy. Multiple events share a pending callback; there is no redraw timer or synchronous `Refresh`/`DoEvents` inside layout. Rounded controls own their local painting only. Avoid manual content offsets and page-wide compositing styles that interfere with native input windows.

Connection groups identity and tunnel preferences. Private-key fields appear only for key authentication. UDP is a separately selectable capability; connection state distinguishes partial availability and host fallback.

The badge, tray and inline details use the same `ConnectionPresentation`. A closed SSH session overrides any cached healthy lease and displays a red connection failure. UDP failure with TCP available is an explicit amber partial failure, including the startup/probe reason. Disabled UDP is not an error. Startup confirms the helper's protocol greeting before accepting its private SOCKS connection; remote exit 126 is reported as an execution-permission failure, and a startup timeout asks users to check pending VM security authorization.

Rules place actions above the editor and show normalized unique counts, unsaved changes and invalid-input feedback. Log actions copy/export/clear without modifying connection settings.

Clash integration separates script generation, TUN instructions and verification. Long setup instructions use disclosure rather than permanently occupying the page. UDP DNS prerequisites are documented separately; an HTTP SOCKS check is not advertised as a UDP or TUN test.

Settings shows the active profile path and offers open/change/default operations. Migration is gated while connected, confirms replacements and retains the original profile.

## Script workspace

The tray menu retains native keyboard navigation and dismissal. Its renderer reads the Windows app theme when opening, uses DPI-aware padding and subdued separators, and falls back to the system renderer in high-contrast mode. Windows 11 owns the outer rounded corners and shadow. Connection actions are enabled from the current session state; menu themes have isolated-desktop screenshot coverage.

The editor scrolls independently; generation, copy, save and return controls remain outside it. The workspace can scroll when larger fonts or Windows DPI require more space. Two selectors switch between editable source and read-only output in one borderless host. Both documents retain selection, undo and scroll state. The rounded frame fits the editor host without clipping its bottom edge.

Empty input generates a new script. Existing input is inspected and merged automatically, including complete programs, function-body fragments and prior managed output. Updating input invalidates stale output. The Windows preview normalizes hard line breaks; composition preserves user logic and reports managed-region conflicts. Ctrl+Enter generates and copies; Ctrl+Tab switches documents inside the workspace.

## Acceptance

Tests cover actual generated scripts, profile storage, native line counts, six pages, authentication-field disclosure, keyboard focus, caption fit, scrolling and DPI transitions. Desktop screenshots are retained for 100/125/150/175/200%. Native 100% and injected high-DPI results are distinct. Real high-DPI monitors, cross-display moves, high-contrast themes and native file dialogs still require their respective physical environments.

Startup diagnostics observe natural paint events after repeated Settings/Connection switches without forcing a refresh or taking a `PrintWindow` capture first. Layout checks include the embedded script workspace, minimum window size and containment of the complete editor frame. `tests/run-startup-desktop.ps1` runs on an isolated desktop; `-SimulateDpi -ScalePercent 150` exercises synthetic scaling without changing the user's display settings.
