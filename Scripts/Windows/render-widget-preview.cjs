// A fresh, isolated card renderer. It does not capture/control the user's desktop.
const fs = require('node:fs');
const path = require('node:path');
function options(argv) {
  const result = { size: 'medium', theme: 'light', 'action-alignment': 'left' };
  const allowed = new Set(['input', 'output', 'playwright', 'adaptive-cards', 'browser', 'size', 'theme', 'report', 'scenario', 'action-alignment']);
  for (let i = 0; i < argv.length; i += 2) {
    const key = argv[i]?.replace(/^--/, '');
    if (!allowed.has(key) || !argv[i + 1]) throw new Error(`Invalid argument: ${argv[i]}`);
    result[key] = argv[i + 1];
  }
  if (!result.input || !result.output || !['medium', 'large'].includes(result.size) || !['light', 'dark'].includes(result.theme))
    throw new Error('Required: --input JSON --output PNG [--size medium|large] [--theme light|dark]');
  return result;
}
(async () => {
  const args = options(process.argv.slice(2));
  const { chromium } = require(args.playwright || 'playwright');
  const adaptiveCards = args['adaptive-cards'] || require.resolve('adaptivecards/dist/adaptivecards.min.js');
  const payload = JSON.parse(fs.readFileSync(args.input, 'utf8'));
  if (payload.type !== 'AdaptiveCard' || payload.version !== '1.5') throw new Error('Expected a production Adaptive Card 1.5 template.');
  const height = args.size === 'large' ? 620 : 304;
  const dark = args.theme === 'dark';
  // These colors simulate theme-specific host semantics. The production JSON
  // selects Accent/Good/Warning/Emphasis, never literal RGB values.
  const palette = dark
    ? { background: '#202020', emphasis: '#2b3038', text: '#ffffff', subtle: '#bdbdbd', border: '#8a8a8a', control: '#333333',
      accent: '#60cdff', good: '#6ccb5f', warning: '#fce100', primaryText: '#001a24' }
    : { background: '#ffffff', emphasis: '#f3f5f7', text: '#1a1a1a', subtle: '#606060', border: '#8a8a8a', control: '#ffffff',
      accent: '#005fb8', good: '#0f6b3a', warning: '#825d00', primaryText: '#ffffff' };
  const browser = await chromium.launch({ headless: true, ...(args.browser ? { executablePath: args.browser } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 300, height }, deviceScaleFactor: 1, colorScheme: args.theme });
    await page.setContent(`<!doctype html><html lang="ko"><meta charset="utf-8"><style>
      html,body{margin:0;background:transparent;font-family:"Segoe UI",sans-serif}
      #widget{width:300px;height:${height}px;border-radius:16px;overflow:hidden;background:${palette.background};color:${palette.text}}
      #attribution{height:48px;box-sizing:border-box;padding:0 16px;display:flex;align-items:center;justify-content:space-between;
        font-size:12px;line-height:16px;color:${palette.subtle}}
      #card{width:300px}
      select{box-sizing:border-box;max-width:100%;min-width:0;height:32px;padding:4px 8px;font:14px "Segoe UI",sans-serif;
        border-radius:4px;border:1px solid ${palette.border};background:${palette.control};color:${palette.text}}
      button{min-height:32px;box-sizing:border-box;border:1px solid ${palette.border}!important;border-radius:4px!important;
        padding:5px 8px!important;font:14px "Segoe UI",sans-serif!important;color:${palette.text}!important;background:${palette.control}!important}
      button.style-positive{background:${palette.accent}!important;color:${palette.primaryText}!important;border-color:${palette.accent}!important}
      p{margin:0!important}
    </style><div id="widget"><div id="attribution"><span>Codex SyncBar</span><span aria-hidden="true">···</span></div><div id="card"></div></div></html>`);
    await page.addScriptTag({ path: adaptiveCards });
    await page.evaluate(({ cardJson, palette, alignment }) => {
      const card = new AdaptiveCards.AdaptiveCard();
      const foregroundColors = Object.fromEntries(['default', 'accent', 'good', 'warning'].map(name => [name,
        { default: name === 'default' ? palette.text : palette[name], subtle: name === 'default' ? palette.subtle : palette[name] }]));
      card.hostConfig = new AdaptiveCards.HostConfig({
        fontFamily: 'Segoe UI',
        fontSizes: { small: 12, default: 14, medium: 18, large: 20, extraLarge: 28 },
        lineHeights: { small: 16, default: 20, medium: 24, large: 28, extraLarge: 36 },
        spacing: { small: 4, default: 8, medium: 16, large: 24, extraLarge: 32, padding: 16 },
        separator: { lineThickness: 1, lineColor: palette.border },
        containerStyles: {
          default: { backgroundColor: palette.background, foregroundColors },
          emphasis: { backgroundColor: palette.emphasis, foregroundColors },
        },
        actions: { maxActions: 3, spacing: 'default', buttonSpacing: 4, actionsOrientation: 'horizontal', actionAlignment: alignment },
        supportsInteractivity: true,
      });
      const context = new AdaptiveCards.SerializationContext(AdaptiveCards.Versions.v1_5);
      card.parse(cardJson, context);
      const errors = Array.from({ length: context.eventCount }, (_, i) => context.getEventAt(i));
      if (errors.length) throw new Error(`Adaptive Card parse errors: ${JSON.stringify(errors)}`);
      document.querySelector('#card').append(card.render());
    }, { cardJson: payload, palette, alignment: args['action-alignment'] });
    await page.evaluate(() => document.fonts.ready);
    await page.waitForFunction(() => [...document.images].every(img => img.complete && img.naturalWidth > 0));
    const layout = await page.evaluate(() => {
      const frame = document.querySelector('#widget');
      const card = document.querySelector('#card');
      const bounds = card.getBoundingClientRect();
      const controls = [...card.querySelectorAll('button, select')].map(el => ({
        type: el.tagName, text: el.tagName === 'SELECT' ? 'account choice' : el.textContent.trim(),
        top: el.getBoundingClientRect().top, bottom: el.getBoundingClientRect().bottom,
        left: el.getBoundingClientRect().left, right: el.getBoundingClientRect().right, width: el.getBoundingClientRect().width, scrollWidth: el.scrollWidth, clientWidth: el.clientWidth,
      }));
      return { width: 300, height: frame.getBoundingClientRect().height, contentBottom: bounds.bottom,
        contentLimit: frame.getBoundingClientRect().bottom, scrollWidth: frame.scrollWidth, clientWidth: frame.clientWidth,
        controls, imageCount: document.images.length };
    });
    if (layout.contentBottom > height + 0.5 || layout.scrollWidth > layout.clientWidth || layout.controls.some(c => c.scrollWidth > c.clientWidth + 1 || c.left < 0 || c.right > 300 || c.bottom > height))
      throw new Error(`Card overflows the ${args.size} ${300}x${height} host: ${JSON.stringify(layout)}`);
    fs.mkdirSync(path.dirname(path.resolve(args.output)), { recursive: true });
    await page.locator('#widget').screenshot({ path: args.output, omitBackground: true });
    const report = { renderer: 'AdaptiveCards JS 3.0.6', source: 'WidgetTemplates.Render with synthetic state',
      actionAlignment: args['action-alignment'], size: args.size, scenario: args.scenario || 'unspecified', theme: args.theme, defaultFontPx: 14, metricFontPx: args.size === 'large' ? 28 : 18, attributionPx: 48, paddingPx: 16,
      width: 300, height, semanticColorSimulation: palette, transparentRoundedCorners: true, layout, boardQa: false };
    if (args.report) fs.writeFileSync(args.report, JSON.stringify(report, null, 2) + '\n');
    console.log(JSON.stringify(report));
  } finally { await browser.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
