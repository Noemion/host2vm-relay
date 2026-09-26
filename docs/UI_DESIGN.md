# Desktop interface

## Visual structure

Host2VMRelay uses a restrained light desktop workspace: a cool off-white canvas, white cards, graphite text and a muted green primary action. The application retains the native Windows title bar, window management, text editing, password masking and file dialogs rather than emulating another operating system's window chrome.

`src/UI/UiTheme.cs` owns colours and the reusable card, input frame, action button, navigation button and status badge. `UiLayout.cs` owns typography, grouping, spacing and explicit-font tracking. Connection and networking behaviour remain separate from presentation.

- Body and editor baseline: 12 pt; page title: 22 pt; card title: 14 pt.
- Logical corner radii: cards 14, input frames 8, buttons 9.
- Cards use 22 logical pixels of internal padding and 18 between groups.
- Inputs keep native controls inside a softly bordered frame; keyboard focus adds a visible outline.
- Primary actions use the accent colour. Secondary actions have quiet borders and hover, press, disabled and keyboard-focus states.
- High Contrast uses system colours and native button painting. It requires separate visual acceptance on the target Windows theme.

## Adaptive navigation

The four destinations are Connection, Rules, Clash integration and Logs. A sidebar is shown when the window has at least 860 logical pixels of client width. Below that threshold the same navigation controls move to a wrapping top bar. No destination is removed and text is not reduced to fit.

The selected page has a distinct navigation state, heading and description. Alt+1 through Alt+4 switch pages; Ctrl+Tab and Ctrl+Shift+Tab cycle them. The connection state remains in the page header and rule activation remains in the footer.

## Pages

Connection groups SSH identity and tunnel preferences into two cards. Private-key browsing is disclosed only for private-key authentication. Sensitive values still use the existing current-user encryption and host-key verification.

Rules place Save and Copy above the editor and show rule count and unsaved changes inline. Examples are separated from the editable content. Saving does not require a success modal.

Clash integration is split into script generation, TUN setup and verification. Detailed TUN instructions are expandable. The script continues to respect application-managed TUN values. Copying a route exclusion only changes the clipboard, not the system routing configuration.

Logs provide copy, export and clear actions. Export is explicit and uses a Save dialog; clear affects only the current log. Users should review host addresses and usernames before sharing exported logs.

## Script workspace

An optional source editor and complete output preview appear side by side when merging and the viewport has at least 840 logical pixels of width. Narrow windows stack these cards vertically. With merging disabled only the output card is shown. The action row stays outside the scrollable editors.

Ctrl+Enter generates and copies. Changing source or merge mode invalidates the previous output and disables Copy/Save. CR/LF conversion remains limited to display preparation. Original script extraction and native edit-control line counts remain covered by regression tests.

## Acceptance

`build.ps1 -Test` retains the independent 100/125/150/175/200 percent DPI message tests and the strict native-monitor guard. It also checks navigation state, private-key disclosure and expanded TUN instructions. Screenshots and assertions are stored under `artifacts/checks`, not source directories.

A hosted 96-DPI desktop with injected messages is not evidence of physical 200% or multi-monitor acceptance. Refer to `DPI_VALIDATION.md` for the native test procedure. Full dark mode and custom title-bar behaviour are not part of this interface revision.
