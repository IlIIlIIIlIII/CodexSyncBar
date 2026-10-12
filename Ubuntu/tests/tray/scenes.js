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
        if (actor.has_style_class_name?.('cs-percent') && actor.clutter_text.get_layout().is_ellipsized()) throw new Error(`Quota is truncated: ${actor.text}`);
        if (actor.has_style_class_name?.('cs-track')) bars.push({track: [actor.width, actor.height], value: actor.value ?? null});
        actor.get_children().forEach(visit);
    };
    visit(extension._rows);
    GLib.file_set_contents(`${output}/${name}-bars.json`, JSON.stringify(bars));
    if (name === 'selected-design') assert(bars.slice(0, 3).every((bar, i) => bar.value === [0.68, 0.84, 0.05][i] && bar.track[0] > 50 && bar.track[1] > 0), 'native quota meters have values and visible dimensions');
    print(`SYNCBAR_CAPTURE ${name} ${width}x${height}`);
}
async function until(predicate, attempts = 100) { for (let i = 0; i < attempts; i++) { if (predicate()) return; await Scripting.sleep(50); } throw new Error('Timed out'); }
async function scenes() {
    const settings = new Gio.Settings({schema_id: 'org.gnome.desktop.interface'});
    settings.set_string('color-scheme', 'prefer-dark');
    settings.set_string('icon-theme', 'Yaru');
    settings.set_double('text-scaling-factor', Number(GLib.getenv('SYNCBAR_QA_TEXT_SCALE')));
    Main.overview.hide();
    await Scripting.sleep(400);
    const extension = Main.extensionManager.lookup('codex-syncbar@griplabs.io').stateObj;
    assert(Boolean(extension?._button), 'extension enabled');
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
    assert(extension._snapshot.operation.kind === 'refresh', 'refresh invokes weekly usage job');
    // From here use presentation-only fixtures. Never ship a fixture hook in the extension.
    extension._client.destroy();
    const today = Date.now();
    const fixture = {accounts: [
        {id: 1, alias: '개인 계정', email: 'main@example.com', usage: {weekly: {remainingPercent: 68, resetsAt: new Date(today + 2*86400000).toISOString()}, updatedAt: new Date(today).toISOString()}},
        {id: 2, alias: '업무 계정', email: 'work@example.com', usage: {weekly: {remainingPercent: 84, resetsAt: new Date(today + 3*86400000).toISOString()}, updatedAt: new Date(today).toISOString()}},
        {id: 3, alias: '실험 계정', email: 'lab@example.com', usage: {weekly: {remainingPercent: 5, resetsAt: new Date(today + 6*86400000).toISOString()}, updatedAt: new Date(today).toISOString()}},
        {id: 4, alias: '보조 계정', email: 'backup@example.com', needsLogin: true},
    ], activeProfileId: 1};
    extension._client = {call: async method => { if (method === 'snapshot') return fixture; throw new Error('테스트: 연결 실패'); }, destroy() {}};
    await extension._refreshSnapshot();
    assert(extension._panelLabel.text === '주간 68%', 'panel shows local active account remaining usage');
    await shot(extension, 'selected-design');
    extension._button.menu.close();
    fixture.activeProfileId = 2;
    await until(() => extension._panelLabel.text === '주간 84%', 400);
    assert(!extension._button.menu.isOpen, 'closed panel follows active account changes without opening menu');
    fixture.accounts[1].usage.weekly.remainingPercent = 100;
    await until(() => extension._panelLabel.text === '주간 100%', 400);
    assert(!extension._panelLabel.clutter_text.get_layout().is_ellipsized(), '100 percent panel label fits');
    await shot(extension, 'panel-closed');
    fixture.activeProfileId = 1;
    await extension._refreshSnapshot();
    extension._button.menu.open();
    fixture.accounts[1].usage.weekly.remainingPercent = 100;
    fixture.accounts[2].usage.weekly.remainingPercent = 0;
    await extension._refreshSnapshot(); await shot(extension, 'quota-extremes');
    fixture.accounts[1].usage.weekly.remainingPercent = 84; fixture.accounts[2].usage.weekly.remainingPercent = 5;
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
    assert(!Main.panel.statusArea['codex-syncbar@griplabs.io'], 'disable removes tray');
    extension.enable(); await until(() => !extension._fetching);
    assert(Boolean(extension._button), 'extension can enable again');
    print('SYNCBAR_COMPLETE');
}
