# Desktop UI design

## Direction

A restrained desktop workspace: neutral canvas, white surfaces, graphite text and a muted green accent. The visual language is inspired by simple utility applications, while retaining Windows title bars, native input behavior and keyboard access. Do not imitate macOS window controls or use platform-restricted fonts and symbols.

## Compact design tokens

- Body and editor baseline: 9.6pt; page title: 17.6pt; card title: 11.2pt.
- ContentScale=0.8 adjusts logical design dimensions; the operating system effective DPI stays unchanged.
- Static whitespace uses SpacingScale=0.6. Cards use 13 logical pixels of padding and 11 between groups.
- Typography, input frames, action buttons, editor frames and cards share factories. Native title-bar and taskbar icon sizes remain platform-driven.
- Text is not shrunk further when the window gets narrow; layout reflows or scrolls.
- Keep visible focus indication, hover/pressed/disabled states and normal Windows input/password/file picker behavior.

## Navigation and pages

Five destinations: Connection, Rules, Clash integration, Logs and Settings. At less than 688 logical pixels of client width, navigation moves above the content; none of the destinations is hidden. Alt+1 through Alt+5 and Ctrl+Tab/Ctrl+Shift+Tab navigate pages.

Connection groups identity and tunnel preferences. Private-key fields appear only for key authentication. UDP is a separately selectable capability; connection state distinguishes partial availability and host fallback.

Rules place actions above the editor and show normalized unique counts, unsaved changes and invalid-input feedback. Log actions copy/export/clear without modifying connection settings.

Clash integration separates script generation, TUN instructions and verification. Long setup instructions use disclosure rather than permanently occupying the page. UDP DNS prerequisites are documented separately; an HTTP SOCKS check is not advertised as a UDP or TUN test.

Settings shows the active profile path and offers open/change/default operations. Migration is gated while connected, confirms replacements and retains the original profile.

## Script workspace

The editor region alone scrolls; generation, copy, save and close controls remain outside it. At sufficient width, source and preview sit side-by-side; narrow layouts stack them. Updating input invalidates stale output. The Windows preview normalizes hard line breaks; composition preserves user logic and reports managed-region conflicts. Ctrl+Enter generates and copies.

## Acceptance

Tests cover actual generated scripts, profile storage, native line counts, five pages, authentication-field disclosure, keyboard focus, caption fit, scrolling and repeated DPI transitions. Desktop screenshots are retained for 100/125/150/175/200%. Native 100% and injected high-DPI results are distinct. Real high-DPI monitors, cross-display moves, high-contrast themes and native file dialogs still require their respective physical environments.
