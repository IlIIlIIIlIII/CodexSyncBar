# Codex SyncBar Ubuntu — design QA

**Final result: passed**

2026-10-03. This result covers the rendered native GTK design and the explicitly recorded GTK integration checks. It does **not** certify live two-account/SSH E2E, global pointer/keyboard input, or fractional Wayland scaling.

## Visual truth and evidence

- Source: `/home/sunggu/.codex/generated_images/01a0fda5-ffc0-7493-b460-72f294cd70fa/exec-1b4b356b-00ed-4718-8260-0dbda14af4cb.png` (selected option 3), preserved as [selected-mock.png](docs/ubuntu/qa-evidence/selected-mock.png).
- Render: [native-dark.png](docs/ubuntu/qa-evidence/native-dark.png), real GTK4/libadwaita widgets connected to the compiled isolated demo service. The source raster is not embedded in the app.
- Full comparison: [comparison.png](docs/ubuntu/qa-evidence/comparison.png), both source and rendered implementation in one image.
- Focused comparison: [comparison-controls.png](docs/ubuntu/qa-evidence/comparison-controls.png), current-account and target-account controls. This was needed to inspect typography, wrapping, spacing, and dropdown affordance.
- Default: [native-default.png](docs/ubuntu/qa-evidence/native-default.png), 1040×760.
- Compact: [native-small.png](docs/ubuntu/qa-evidence/native-small.png) and [native-small-bottom.png](docs/ubuntu/qa-evidence/native-small-bottom.png), 600×480, top and bottom of scrollable body.
- Light: [native-light.png](docs/ubuntu/qa-evidence/native-light.png), 1040×760.
- 100% attempt: [native-scale-100.png](docs/ubuntu/qa-evidence/native-scale-100.png), GDK_SCALE=1 (also attempted GDK_DPI_SCALE=0.5), 1040×760. The current 200% Wayland session retained a different DPI for native header/button fonts than pixel-sized content; this capture is density-inconsistent and **inconclusive**, not a 100% pass or a proven normal-display defect. Physical 100%/150% checks remain pending.
- HiDPI: [native-hidpi.png](docs/ubuntu/qa-evidence/native-hidpi.png), logical 600×480 with GDK_SCALE=2. This is a normalized widget render, not proof of a physical 200% monitor capture.
- Offline state: [native-offline.png](docs/ubuntu/qa-evidence/native-offline.png).

The source is 1487×1058 pixels. The comparison normalizes it to 1440×1024 beside the 1440×1024 GTK render, with labels outside each image. GTK captures use logical widget dimensions, GTK WidgetPaintable, and Gsk CairoRenderer. The live helper also supports a real GTK child snapshot with the native CSS background; it rejects missing child content and blank image pixels. The accepted final live run used WidgetPaintable for all three screens, which were opened and visually inspected. CSS viewport terminology does not apply to this native app. No desktop/browser frame is introduced. The comparison uses the same current account, selected account, 5-hour/week percentages, and three-device scenario. Native titlebar, font metrics, symbolic icons, and theme colors are intentional substitutions required by the accepted implementation plan. The user subsequently requested full email display; production snapshot/preview labels and all GTK views now preserve the full email, with word/character wrapping. Historical demo evidence contains intentionally synthetic starred fixture values; live captures reflect the updated requirement.

## Findings and iterations

1. **Initial result: blocked.** [initial.png](docs/ubuntu/qa-evidence/initial.png) had an overly wide account pane, fixture content that did not match the selected reference, and a weak typography hierarchy (P2). The implementation changed the two-column proportions to approximately 1:2, restored the reference data in the demo service, and adjusted title/section/account font hierarchy. [revised.png](docs/ubuntu/qa-evidence/revised.png) records the intervening layout; the final full comparison above uses matching data.
2. **Default-window result: blocked.** The first responsive implementation requested more than the planned 600-pixel minimum and crowded the device rows at 1040×760 (P2). Initial reflow now runs before presentation; shorter windows use compact spacing and font sizes. [native-default.png](docs/ubuntu/qa-evidence/native-default.png) shows all three rows and the complete fixed apply footer. [native-small.png](docs/ubuntu/qa-evidence/native-small.png) establishes an actual 600×480 window.
3. **Post-fix result: passed.** The combined source/render and focused comparison show no remaining actionable P0/P1/P2 mismatch within the accepted native substitutions. Compact bottom evidence and the 9/9 GTK integration report establish access to the final device row, selector focus, and body scrolling. The footer remains visible independently of body scroll.

The real-login trial also exposed a misleading “적용 중” label during browser login. The UI now labels this “로그인 대기 중”, hides the applying spinner, and offers visible reopen/cancel controls. The added GTK regression passed.

## Required fidelity surfaces

| Surface | Evidence and assessment |
|---|---|
| Fonts and typography | Uses Ubuntu's native GTK font selection and Korean fallback, rather than a hard-coded web font. Display, section, account, and secondary text retain the source hierarchy. Default-size important labels and percentages neither overlap nor truncate; long account information wraps/ellipsizes in native controls. Native antialiasing and font metrics are accepted variations. |
| Spacing and layout | Approximately 1:2 account/detail regions at desktop width; the layout stacks below 900. Compact spacing keeps the three-device scenario visible at 1040×760. At 600×480 the body scrolls and the fixed footer remains accessible. Card radii, headerbar, borders, and controls follow native Yaru. |
| Colors and tokens | Window, card, foreground, accent, success/error colors use GTK/Yaru semantic colors. Current system Yaru Olive dark is reflected in the controls. Quota accent text measured approximately 4.82:1 against its background. Selected target text uses native foreground for readable contrast. Light-mode evidence shows the corresponding native surface and text palette. |
| Image quality and assets | No decorative raster assets are needed. Native symbolic device, account, refresh, and menu icons remain sharp. Replacing mock icons with native symbols is explicitly required by the accepted plan. Source image is used only as comparison evidence. |
| Copy and content | Current local account, selected account, and planned per-device account remain distinct. “현재 적용 · 이 Ubuntu PC” clarifies local scope. “예정”, “동일 계정 재적용”, unknown/connection state, and explicit “모든 장치에 적용” communicate preview versus mutation. Missing quota values remain absent rather than showing fabricated zero. |

## Interaction and remaining coverage

Latest production capture: [full-email live screen](docs/ubuntu/qa-evidence/live/live-profile-1-1790991581924570291.png), 1040×760, actual registered accounts and local/ml/rogally observations. It confirms the user-requested unmasked email labels, one available quota window (missing 5-hour window is hidden), all three device rows, and fixed footer without overlap. This is the final restored-A screen from the accepted live A→B→A run. Its current→same-account state differs from the original reference's A→B state, so it validates live content/layout rather than exact source-state matching.

[gtk-integration-600x480.json](docs/ubuntu/qa-evidence/gtk-integration-600x480.json) passed 9/9: account selection does not change active accounts; all five production dialogs plus SSH form construct; login waiting exposes reopen/cancel without an applying spinner; picker accepts focus; latest selection wins; apply disables duplicate activation; GTK button signals traverse IPC for A→B and B→A; final device row accepts focus and body scroll reaches the end. These are app-internal GTK actions against the real compiled demo service, not global native input automation.

Additional overflow evidence: [native-overflow.png](docs/ubuntu/qa-evidence/native-overflow.png) shows a long target account name and 24 devices at 600×480, scrolled to the last row. This synthetic visual fixture checks layout only.

User-confirmed native input: [native-manual.json](docs/ubuntu/qa-evidence/native-manual.json) records that the account dropdown/menu open with the mouse and Tab moves a visible focus indicator. This covers those actions only.

Remaining acceptance steps: full dialog keyboard traversal on GNOME Wayland; physical 100% and 150% fractional monitor scale; testing a different system accent without changing the user's current desktop preference; multiple physical monitors. Actual login and live A→B→A against ml and rogally, local desktop restarts, and original-state restoration passed separately in the locally retained live QA report. Those are tracked separately from visual fidelity and must not be reported as passed from these screenshots.

**Follow-up polish:** P3 — the native downward chevron between current and target accounts is subtler than the reference's direction arrow. The relationship is already explicit in section labels, so it is not blocking.

**Implementation checklist:**

- [x] Source and implementation captured and compared together.
- [x] All five required fidelity surfaces reviewed.
- [x] P2 layout findings fixed and recaptured.
- [x] Default and minimum dimensions verified with fixed footer.
- [x] Light/dark and normalized HiDPI evidence recorded.
- [x] GTK selection, focus, latest-response, apply and restoration signal flow recorded.
- [x] User confirms mouse dropdown/menu and visible Tab focus.
- [x] Actual two-account/SSH flow, three local Codex restarts and original-state restoration verified separately.
- [ ] Complete remaining physical scale and full-dialog native input checks.

final result: passed
