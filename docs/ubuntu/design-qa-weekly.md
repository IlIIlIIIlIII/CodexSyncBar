# Ubuntu weekly usage comparison — design QA

Date: 2026-10-06. Scope: the selected weekly-only option 3, implemented in the existing native GTK application. The previous account-transition report is archived in [design-qa-transition.md](docs/ubuntu/design-qa-transition.md).

## Visual truth and evidence

- Source: [selected-design.png](docs/ubuntu/qa-evidence/weekly/selected-design.png), the third displayed weekly-only mock selected by the user; original image `exec-a171b702-4350-4876-a463-bb48239db3fd.png`.
- Render: [desktop.png](docs/ubuntu/qa-evidence/weekly/desktop.png), real GTK widgets using the production UI and synthetic fixture data.
- Full comparison: [comparison.png](docs/ubuntu/qa-evidence/weekly/comparison.png), source and implementation together.
- Focused comparison: [comparison-detail.png](docs/ubuntu/qa-evidence/weekly/comparison-detail.png), account identity, percentage, bars and reset text.
- Viewport: 1040×760 logical pixels. Source 1467×1072 pixels normalized to 1040×760. GTK output is 1040×760 pixels via WidgetPaintable/CairoRenderer; the current Wayland session reports GTK scale factor 2 but this capture uses logical widget dimensions. Browser CSS size does not apply.
- Same state: dark theme, weekly overview, four synthetic accounts, 68% / 24% / 5% / unavailable, active first account. Reset dates match. Fixture refresh times reflect capture time on October 6, not the mock's illustrative 15:42.

## Comparison history

1. **P2 — bars started to the right of their axis.** [Initial comparison](docs/ubuntu/qa-evidence/weekly/comparison-initial.png) showed expanding identity cells pushing the bar start inward. Set the identity column to non-expanding and share its width with the axis. The final comparison shows a common baseline and equal-length tracks.
2. **P2 — compact heading pushed the first quota below the fold.** Initial 600×480 capture had a separate refresh row and oversized footer. Kept an icon refresh action in the heading and a horizontal footer at the minimum supported width. [Narrow final](docs/ubuntu/qa-evidence/weekly/narrow.png) shows the first account's remaining percentage and reset time above the footer; [bottom](docs/ubuntu/qa-evidence/weekly/narrow-bottom.png) shows the last account reachable by scrolling.
3. **P2 — cached offline rows were too dim.** [Initial offline](docs/ubuntu/qa-evidence/weekly/offline-initial.png) disabled the entire list. Disable row activation while retaining readable content instead. [Final offline](docs/ubuntu/qa-evidence/weekly/offline.png) displays a service notice and explicit previous-value labels. The active marker says “마지막 적용 계정” when disconnected.
4. Rebalanced navigation padding, heading gaps and row typography. Final default capture reports scrollUpper = scrollPageSize = 579: all four accounts fit without scrolling at 1040×760. No P0/P1/P2 issues remain in the checked states.

## Required fidelity surfaces

| Surface | Result |
| --- | --- |
| Fonts and typography | Native system Korean font; 32px headline, 30px tabular percentages, 17px account names and 13px secondary text. Full email addresses wrap instead of truncating. Long-name evidence confirms readable wrapping at 600×480. Native font metrics differ slightly from the generated raster; hierarchy is retained. |
| Spacing and layout | One list with dividers, aligned identity/bar/value columns, fixed navigation and footer. Narrow rows place identity above the quota. Axis 100% aligns with the actual track endpoint, correcting the mock's scale extending into the percentage column. |
| Colors and tokens | Existing Yaru background and system accent preserved. Warning ≤25%, critical ≤10%, zero explicitly exhausted. Native theme warning/error colors adapt to light/dark and accompany text. Source neon green is represented by the configured system accent rather than hardcoding a different accent. |
| Image and icon quality | Native symbolic account and refresh icons; no custom raster illustrations required. All accounts use a person icon because account roles are not stored. No CSS art, generated mock raster embedded in UI, or arbitrary role inference. |
| Copy and content | Weekly quota only on the new page. Missing information is “—”, zero is “0% / 모두 소진”, stale/error/refreshing values explicitly say “이전 수치”. Per-account reset dates and the oldest available account query timestamp are used; a fresh snapshot poll never masquerades as fresh usage data. |

## Additional states and interactions

- [Light](docs/ubuntu/qa-evidence/weekly/light.png), [empty](docs/ubuntu/qa-evidence/weekly/empty.png), [stale/error/zero](docs/ubuntu/qa-evidence/weekly/stale.png), [long name](docs/ubuntu/qa-evidence/weekly/long-name.png), [16 accounts at scroll end](docs/ubuntu/qa-evidence/weekly/overflow-bottom.png).
- [GTK integration 1040×760](docs/ubuntu/qa-evidence/weekly/gtk-integration-1040x760.json) and [600×480](docs/ubuntu/qa-evidence/weekly/gtk-integration-600x480.json): 12 checks each passed against the installed compiled backend in explicitly isolated demo mode. Includes overview-first navigation, row/focus preservation, selecting without applying, all existing management dialogs, rapid account selection, A→B→A through demo IPC, footer navigation and refresh completion without changing the active account.
- Seven pure quota tests and seven native widget tests passed: weekly-only mapping, unknown vs zero, thresholds, cached failure states, old/expired windows, update/reorder/removal, offline recovery and responsive layout.
- Existing IPC tests (8), GTK capture test (1) and SSH configuration tests (6) passed. Python compilation and git diff whitespace checks passed.
- Native render logs were inspected; no GTK CSS or runtime errors were emitted during final captures. This is GTK rendering and app-internal signal testing, not browser automation or physical pointer/keyboard E2E.

## Follow-up polish and limits

P3: native header/segmented controls and semantic account icons intentionally retain Ubuntu styling instead of duplicating every decorative mock icon. Physical multi-monitor/fractional scaling and screen-reader speech output were not manually tested. Account rows have accessible quota/status labels and native keyboard focus.

No live account switch, package installation or remote push was performed for this UI change. The source application can be previewed in isolated demo mode.

## Implementation checklist

- [x] Weekly-only page is the default entry point.
- [x] Navigation and refresh use existing production paths.
- [x] Row activation opens the corresponding account preview; applying remains explicit.
- [x] Source and native render compared together; focused detail inspected.
- [x] P2 findings fixed and post-fix evidence inspected.
- [x] Missing, zero, stale, disconnected, empty and overflow states checked.
- [x] Existing unrelated working changes preserved.

final result: passed
