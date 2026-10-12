# Codex SyncBar native tray — option 1

Date: 2026-10-06. Scope: the first displayed image of the new **weekly-only** design set, implemented as a GNOME Shell 50 extension. The previous GTK overview QA is preserved in [design-qa-weekly.md](docs/ubuntu/design-qa-weekly.md).

## Source and visual evidence

- Source: [selected-option-1.png](docs/ubuntu/qa-evidence/tray/selected-option-1.png), 1182×1330 pixels. Popup crop (77,94)–(1104,1242), normalized proportionally to 598×668 for comparison.
- Implementation: [selected-design.png](docs/ubuntu/qa-evidence/tray/final/selected-design.png), actual GNOME Shell 50.1, private headless Wayland compositor, 1280×900 at scale 1. Popup visible crop 598×671.
- Same state: dark popup, four accounts, 68% / 84% / 5% / unavailable, first account active. Fixture timestamps follow capture time; the production header says “이 PC” instead of the source's demo annotation.
- [Combined final comparison](docs/ubuntu/qa-evidence/tray/comparison-final.png), viewed with both images together. [Initial comparison](docs/ubuntu/qa-evidence/tray/comparison-initial.png) retains the pre-fix evidence. [Popup only](docs/ubuntu/qa-evidence/tray/popup.png).
- [Large text](docs/ubuntu/qa-evidence/tray/large-text/selected-design.png): 1280×720, text scale 1.25; [100% and 0%](docs/ubuntu/qa-evidence/tray/large-text/quota-extremes.png), [scroll end](docs/ubuntu/qa-evidence/tray/large-text/overflow-bottom.png).

## Findings resolved

1. **P1 — quota fill was centered, then disappeared in the first allocation fix.** The initial comparison showed a centered low-quota dot. Replaced the custom allocation with GNOME's native accessible `BarLevel`. Final meters share a left origin, show the expected 68/84/5 values, and have equal 150px tracks. Final logs contain no allocation-loop warnings.
2. **P2 — row density and missing dividers drifted from option 1.** Enlarged avatars, account names and header, added explicit native separators, aligned identity/meter/action columns and reserved the same border in active/inactive rows. The normalized popup heights are now 668px source versus 671px implementation.
3. **P1 — 100% and enlarged text could be ellipsized.** Changed percentage, identity and action widths to font-relative units. Added runtime checks rejecting any ellipsized quota in captured scenes, including 0% and 100%. Long real email addresses wrap at the domain boundary without inserting hyphens.
4. **P2 — long account lists could crowd persistent controls.** Only the account list scrolls; the header, refresh and app button remain visible. Verified sixteen accounts at both tested sizes.

## Required fidelity surfaces

| Surface | Assessment |
| --- | --- |
| Typography | Native system Latin/Korean font, bold account names and numeric percentages, subdued email/reset captions. 100% remains fully visible at 1.25 text scale. Font rendering differs from the generated raster; hierarchy and reading order are retained. |
| Spacing/layout | Charcoal anchored popup, brand header, weekly heading, olive active row, aligned quota columns, dividers, footer. Active account moves to the top of this presentation without changing stored account order. |
| Color | #242424 surface, #373d29 active row, green normal quota and amber ≤25%, muted unavailable/stale values. Numeric labels accompany colors. |
| Image/icon quality | Reuses the installed Codex SyncBar PNG logo. Account, refresh, check and open icons are native GNOME symbolic icons. No embedded mock image or hand-drawn replacement assets. Native `BarLevel` supplies the progress control and accessibility semantics. |
| Copy/content | Weekly only. Missing values never become 0%. Login/error/stale reasons are visible. Update time comes from usage data, not snapshot polling. Switching is explicitly scoped to this PC. |

The full combined comparison is readable at native density; individual quota-extreme, large-text, error and scroll-end captures supply the focused state checks. Native GNOME omits the illustrative arrow and raster glow from the generated reference. The footer button and check icon differ slightly in width/stroke; these are accepted P3 native-widget differences.

## Functional evidence

- 16 pure model/command assertions: weekly/unknown/zero/stale semantics, unavailable accounts, busy state and fail-closed local-only preview validation.
- Native GNOME integration through the compiled demo backend: tray popup, A→B→A, SSH accounts unchanged, weekly refresh, visible quota meters, login-disabled row, failed switch notice, empty/overflow states, launcher callback, extension disable/re-enable.
- Separate private Wayland pointer/keyboard run clicks the tray and account button, then restores account A with Enter. Input devices belong to that test compositor only.
- Six installer tests passed; built Debian package includes all six extension files. Source and installed extension hashes match.
- Real production service: six accounts rendered read-only in the native popup. Identity hashes, active account and weekly preference remained unchanged after installation. Private screenshots are outside the repository under `/tmp/syncbar-tray-install/native-readonly/`.

## Installation boundary

The extension is installed at `~/.local/share/gnome-shell/extensions/codex-syncbar@griplabs.io` and is included in the enabled-extension list. Other extension settings were preserved. The current GNOME session's `GetExtensionInfo` still returns an empty dictionary: **the new tray is not active in this desktop session yet**. It will load at the next login. No logout, compositor restart, global input change or real-account switch was performed.

The visual and isolated interaction gate passed. Live tray activation and real-account switching from that live tray remain acceptance checks after re-login; the demo switch is not evidence of a new real-account A→B→A run.

Implementation checklist: complete. Deployment activation: pending next login.

final result: passed
