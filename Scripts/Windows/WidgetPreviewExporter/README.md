# Widget picker preview and layout checks

The exporter calls production `WidgetTemplates.Render` using the same memory-only
`DemoDashboardRuntime` as app QA. It never opens the controller, credentials,
named pipe or real devices.

With .NET 10, Node.js, Playwright/Chromium and AdaptiveCards JS 3.0.6 available:

```powershell
dotnet run --project Scripts/Windows/WidgetPreviewExporter -- C:\Temp\syncbar-preview\medium.json medium normal
node Scripts/Windows/render-widget-preview.cjs --input C:\Temp\syncbar-preview\medium.json --output Windows/CodexSyncBar.Windows/Assets/WidgetPreview.png --size medium --theme light --scenario normal
```

Exporter arguments after the output path:

- Size: `medium` (default) or `large`.
- Scenario: `normal` (default), `stress`, `noaccount`, `busy`, `error`, `weekly`,
  `credits-multiple`, `credits-zero`, or `credits-unknown`.

`stress` uses 70-character Korean aliases, a 180-character offline message, 20
synthetic devices, a low quota and three expiration groups. `weekly` omits the
five-hour quota and has zero reset credits; `error` leaves the credit count and
expirations unknown. `credits-multiple` contains one expired credit, one expiring
in less than a minute, and one later group; `credits-zero` and `credits-unknown`
cover the two separate empty states without an unrelated error. The same state can be
rendered with `--theme light` or `--theme dark`; optional `--report <JSON path>`
saves dimensions and control bounds. Use separate output paths for stress cases.

Dependencies outside Node's module search path can be selected with
`--playwright <absolute module directory>`,
`--adaptive-cards <absolute adaptivecards.min.js path>`, and
`--browser <absolute Chromium executable path>`.

The renderer opens a fresh headless page and simulates a 300×304 or 300×620 host,
48px attribution area, 16px card padding, and device scale 1. The type ramp is
12/16 captions, 14/20 body, 18/24 medium, 20/28 subtitle, and 28/36 key metrics.
It validates the JSON as Adaptive Cards 1.5, checks control/content bounds and
captures a new PNG with transparent rounded corners. It does not shrink text,
edit a previous image, add unsupported progress elements, or scroll hidden
content into view to pass the size check.

The medium card shows the current alias, remaining percentages,
relative reset countdown and local `MM.dd (토) HH:mm` reset date,
update time/status and explicit choice/apply/refresh controls. Medium quota
values use the 18/24 type size so both reset lines fit. When the API omits
the five-hour window, that column disappears and weekly usage fills the width.
Unknown weekly usage is a dash; old values retain their timestamp.

The large card has a shaded quota section, a top-right app action, a device
section (`Windows PC 1대` denotes a count), and a separate `emphasis` reset-credit section: total count,
up to two expiration groups with their counts, remaining days/hours/minutes and
local `yyyy.MM.dd (요일) HH:mm` timestamp with the full Korean weekday. Further
groups are announced with an app-details hint. Zero and unknown counts are
distinct; missing expiration dates are not inferred. Medium cards reserve their
limited height for the quotas and three stable interaction targets.

The production JSON uses host-managed `Good`/`Accent` for the two quotas and
`Warning` for 20% or less remaining or imminent/expired credits. Labels and
numbers preserve meaning without color. No RGB values are embedded in the card;
light/dark preview palettes simulate the host's semantic color mapping.

Both sizes keep only two short footer actions, 모두 적용 and 새로고침.
Large cards put 앱 열기 in a header ActionSet; errors show a summary with details
in the app. Check both `--action-alignment left` (default) and `stretch`.
The renderer rejects controls extending beyond the right, left or bottom edge.

The generated images are synthetic previews, not Windows Widgets Board
screenshots. Actual host rendering, accessibility and 100/150/200% DPI require
separate QA. The catalog asset is always the normal medium light PNG.

Design references: [fundamentals](https://learn.microsoft.com/en-us/windows/apps/design/widgets/widgets-design-fundamentals),
[interaction](https://learn.microsoft.com/en-us/windows/apps/design/widgets/widgets-interaction-design),
and [picker preview requirements](https://learn.microsoft.com/en-us/windows/apps/design/widgets/widgets-picker-integration).
