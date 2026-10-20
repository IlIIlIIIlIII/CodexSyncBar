import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Scripting from 'resource:///org/gnome/shell/ui/scripting.js';
export const METRICS = {};
let pending;
export function run() { return pending ??= scenes(); }
export function init() {
    GLib.timeout_add(GLib.PRIORITY_DEFAULT, 2000, () => {
        if (!pending) Scripting.runPerfScript({run, METRICS}, GLib.getenv('SHELL_PERF_OUTPUT'));
        return GLib.SOURCE_REMOVE;
    });
}
const assert = (test, message) => { if (!test) throw new Error(message); print(`SYNCBAR_PASS ${message}`); };
const output = GLib.getenv('SYNCBAR_QA_OUTPUT');
async function shot(extension, name) {
    await Scripting.sleep(350);
    const stream = Gio.File.new_for_path(`${output}/${name}.png`).replace(null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
    await new Shell.Screenshot().screenshot(false, stream); stream.close(null);
    const actor = extension._button.menu.actor;
    const [x, y] = actor.get_transformed_position();
    const [width, height] = actor.get_transformed_size();
    GLib.file_set_contents(`${output}/${name}.json`, JSON.stringify({x, y, width, height, scale: St.ThemeContext.get_for_stage(global.stage).scale_factor,
        rows: extension._snapshot.accounts.length, scrollHeight: extension._scroll.height, footerMapped: extension._openButton.mapped}, null, 2));
    const bars = [];
    const visit = actor => {
        if (actor.has_style_class_name?.('clsb-percent') && actor.clutter_text.get_layout().is_ellipsized()) throw new Error(`Quota is truncated: ${actor.text}`);
        if (actor.has_style_class_name?.('clsb-track')) bars.push({track: [actor.width, actor.height], value: actor.value ?? null});
        actor.get_children().forEach(visit);
    };
    visit(extension._rows);
    GLib.file_set_contents(`${output}/${name}-bars.json`, JSON.stringify(bars));
    if (name === 'selected-design') assert(bars.slice(0, 4).every((bar, i) => bar.value === [0.72, 0.68, 0.09, 0.84][i] && bar.track[0] > 50 && bar.track[1] > 0), 'native five-hour and weekly meters have values and visible dimensions');
    print(`SYNCBAR_CAPTURE ${name} ${width}x${height}`);
}
// A fractional display scale like the 1.67x many laptop panels use, on the private monitor only.
async function applyScale(scale) {
    const bus = Gio.DBus.session;
    const call = (method, parameters, type) => new Promise((resolve, reject) => bus.call('org.gnome.Mutter.DisplayConfig',
        '/org/gnome/Mutter/DisplayConfig', 'org.gnome.Mutter.DisplayConfig', method, parameters, type ? new GLib.VariantType(type) : null,
        Gio.DBusCallFlags.NONE, -1, null, (source, result) => { try { resolve(source.call_finish(result)); } catch (error) { reject(error); } }));
    const [serial, monitors] = (await call('GetCurrentState', null, '(ua((ssss)a(siiddada{sv})a{sv})a(iiduba(ssss)a{sv})a{sv})')).deepUnpack();
    const [[connector], modes] = monitors[0];
    const mode = modes.find(item => item[6]['is-current']?.unpack()) ?? modes[0];
    const chosen = mode[5].reduce((best, value) => Math.abs(value - scale) < Math.abs(best - scale) ? value : best);
    await call('ApplyMonitorsConfig', new GLib.Variant('(uua(iiduba(ssa{sv}))a{sv})',
        [serial, 1, [[0, 0, chosen, 0, true, [[connector, mode[0], {}]]]], {}]), null);
    await Scripting.sleep(800);
    print(`SYNCBAR_SCALE ${chosen}`);
}
async function until(predicate, attempts = 100) { for (let i = 0; i < attempts; i++) { if (predicate()) return; await Scripting.sleep(50); } throw new Error('Timed out'); }
async function scenes() {
    const settings = new Gio.Settings({schema_id: 'org.gnome.desktop.interface'});
    settings.set_string('color-scheme', 'prefer-dark');
    settings.set_string('icon-theme', 'Yaru');
    settings.set_double('text-scaling-factor', Number(GLib.getenv('SYNCBAR_QA_TEXT_SCALE')));
    Main.overview.hide();
    await Scripting.sleep(400);
    if (GLib.getenv('SYNCBAR_QA_SCALE')) await applyScale(Number(GLib.getenv('SYNCBAR_QA_SCALE')));
    // Every extension's stylesheet applies to the whole shell, so another one can restyle this popup.
    if (GLib.getenv('SYNCBAR_QA_OTHER_STYLESHEET'))
        St.ThemeContext.get_for_stage(global.stage).get_theme().load_stylesheet(Gio.File.new_for_path(GLib.getenv('SYNCBAR_QA_OTHER_STYLESHEET')));
    const extension = Main.extensionManager.lookup('claude-syncbar@griplabs.io').stateObj;
    assert(Boolean(extension?._button), 'extension enabled');
    // This private shell reaches no user service manager, so the start attempt itself fails.
    await until(() => !extension._starting);
    assert(extension._autostarted, 'the Shell session starts the service without a popup click');
    extension._starting = true; extension._render();
    assert(extension._unavailable && extension._message.text === '서비스를 시작하는 중…', 'a service still starting is not reported as a failed connection');
    extension._starting = false; extension._render();
    assert(extension._message.text.startsWith('서비스에 연결하지 못했습니다'), 'a failed start keeps the actionable connection notice');
    await until(() => !extension._fetching);
    extension._client.path = GLib.getenv('SYNCBAR_QA_SOCKET');
    await extension._refreshSnapshot();
    assert(!extension._unavailable && extension._snapshot.accounts.length > 0, 'native async socket reads accounts');
    // These devices belong only to the private, headless test compositor.
    assert(GLib.get_user_runtime_dir().includes('syncbar-tray-qa-'), 'input is isolated from the desktop');
    const seat = Clutter.get_default_backend().get_default_seat();
    const pointer = seat.create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);
    const keyboard = seat.create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
    const click = async actor => {
        const [x, y] = actor.get_transformed_position(), [w, h] = actor.get_transformed_size();
        pointer.notify_absolute_motion(GLib.get_monotonic_time(), x + w/2, y + h/2);
        await Scripting.sleep(60);
        pointer.notify_button(GLib.get_monotonic_time(), Clutter.BUTTON_PRIMARY, Clutter.ButtonState.PRESSED);
        await Scripting.sleep(60);
        pointer.notify_button(GLib.get_monotonic_time(), Clutter.BUTTON_PRIMARY, Clutter.ButtonState.RELEASED);
        await Scripting.sleep(60);
    };
    await click(extension._button);
    await until(() => !extension._fetching);
    assert(extension._button.menu.isOpen, 'tray opens native popup');
    if (GLib.getenv('SYNCBAR_QA_LIVE') === '1') {
        assert(extension._panelLabel.mapped && !extension._panelLabel.clutter_text.get_layout().is_ellipsized(), 'live panel usage is visible and not truncated');
        print(`SYNCBAR_PANEL ${extension._panelLabel.text}`);
        await shot(extension, 'installed-accounts-readonly');
        print(`SYNCBAR_LIVE ${JSON.stringify({accounts: extension._snapshot.accounts.length, activeProfileId: extension._snapshot.activeProfileId, buttons: extension._accountButtons.length})}`);
        extension._button.menu.close(); return;
    }
    await click(extension._accountButtons.find(([id]) => id === 2)[1]);
    assert(extension._pending || extension._snapshot.isBusy || extension._snapshot.activeProfileId === 2, 'pointer click starts switch');
    await until(() => !extension._pending);
    await Scripting.sleep(350); await extension._refreshSnapshot();
    assert(extension._snapshot.activeProfileId === 2, 'native button applies account B');
    assert(extension._snapshot.devices.filter(d => d.kind === 'ssh').every(d => d.currentProfileId === 1), 'tray switch leaves every SSH device unchanged');
    extension._accountButtons.find(([id]) => id === 1)[1].grab_key_focus();
    keyboard.notify_keyval(GLib.get_monotonic_time(), Clutter.KEY_Return, Clutter.KeyState.PRESSED);
    keyboard.notify_keyval(GLib.get_monotonic_time(), Clutter.KEY_Return, Clutter.KeyState.RELEASED);
    await Scripting.sleep(60);
    await until(() => !extension._pending);
    await Scripting.sleep(350); await extension._refreshSnapshot();
    assert(extension._snapshot.activeProfileId === 1, 'keyboard Enter restores account A');
    extension._refreshButton.emit('clicked', 1);
    await until(() => !extension._pending);
    assert(extension._snapshot.operation.kind === 'refresh', 'refresh invokes usage job');
    // From here use presentation-only fixtures. Never ship a fixture hook in the extension.
    extension._client.destroy();
    const today = Date.now();
    const at = hours => new Date(today + hours * 3600000).toISOString();
    const quotas = (fiveHour, fiveHourReset, weekly, days) => ({fiveHour: {remainingPercent: fiveHour, resetsAt: fiveHourReset === null ? null : at(fiveHourReset)},
        weekly: {remainingPercent: weekly, resetsAt: at(days * 24)}, updatedAt: new Date(today).toISOString()});
    const fixture = {accounts: [
        {id: 1, alias: '개인 계정', email: 'main@example.com', plan: 'Max 20x', usage: quotas(72, 1.7, 68, 2)},
        {id: 2, alias: '업무 계정', email: 'work@example.com', usage: quotas(9, 0.6, 84, 3)},
        {id: 3, alias: '실험 계정', email: 'lab@example.com', usage: quotas(100, null, 5, 6)},
        {id: 4, alias: '보조 계정', email: 'backup@example.com', needsLogin: true},
    ], activeProfileId: 1};
    extension._client = {call: async method => { if (method === 'snapshot') return fixture; throw new Error('테스트: 연결 실패'); }, destroy() {}};
    await extension._refreshSnapshot();
    assert(extension._panelLabel.text === '5시간 72% · 주간 68%', 'panel shows both quotas of the local active account');
    await shot(extension, 'selected-design');
    extension._button.menu.close();
    fixture.activeProfileId = 2;
    await until(() => extension._panelLabel.text === '5시간 9% · 주간 84%', 400);
    assert(!extension._button.menu.isOpen, 'closed panel follows active account changes without opening menu');
    fixture.accounts[1].usage.weekly.remainingPercent = 100;
    await until(() => extension._panelLabel.text === '5시간 9% · 주간 100%', 400);
    assert(!extension._panelLabel.clutter_text.get_layout().is_ellipsized(), '100 percent panel label fits');
    fixture.settings = {claudeFiveHour: false};
    await until(() => extension._panelLabel.text === '주간 100%', 400);
    assert(true, 'settings that hide the five-hour quota hide it in the panel');
    delete fixture.settings;
    await shot(extension, 'panel-closed');
    fixture.activeProfileId = 1;
    await extension._refreshSnapshot();
    extension._button.menu.open();
    fixture.accounts[1].usage.weekly.remainingPercent = 100;
    fixture.accounts[2].usage.weekly.remainingPercent = 0; fixture.accounts[2].usage.fiveHour.remainingPercent = 0;
    await extension._refreshSnapshot(); await shot(extension, 'quota-extremes');
    fixture.accounts[1].usage.weekly.remainingPercent = 84; fixture.accounts[2].usage.weekly.remainingPercent = 5;
    fixture.accounts[2].usage.fiveHour.remainingPercent = 100;
    await extension._refreshSnapshot();
    assert(extension._accountButtons.length === 2, 'active and re-login accounts have no switch button');
    extension._accountButtons[0][1].emit('clicked', 1); await until(() => !extension._pending);
    assert(extension._message.text.includes('연결 실패'), 'failed switch stays inline without claiming success');
    await shot(extension, 'switch-error');
    extension._actionError = null;
    fixture.accounts = Array.from({length: 16}, (_, index) => ({...fixture.accounts[index % 4], id: index + 1}));
    await extension._refreshSnapshot();
    await shot(extension, 'overflow-top');
    const adjustment = extension._scroll.vadjustment;
    assert(adjustment.upper > adjustment.page_size, 'overflow accounts scroll');
    adjustment.value = adjustment.upper - adjustment.page_size;
    await shot(extension, 'overflow-bottom');
    const [fx, fy] = extension._openButton.get_transformed_position();
    assert(fy + extension._openButton.height <= global.stage.height, 'footer stays on-screen');
    fixture.accounts = []; await extension._refreshSnapshot(); await shot(extension, 'empty');
    let opened = false; extension._openApp = () => { opened = true; };
    extension._openButton.emit('clicked', 1); assert(opened, 'app button routes to launcher');
    extension._button.menu.close();
    extension.disable();
    assert(!Main.panel.statusArea['claude-syncbar@griplabs.io'], 'disable removes tray');
    extension.enable(); await until(() => !extension._fetching);
    assert(Boolean(extension._button), 'extension can enable again');
    print('SYNCBAR_COMPLETE');
}
