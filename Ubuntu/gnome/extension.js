import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GioUnix from 'gi://GioUnix';
import GLib from 'gi://GLib';
import Pango from 'gi://Pango';
import St from 'gi://St';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import {BarLevel} from 'resource:///org/gnome/shell/ui/barLevel.js';
import {Client} from './client.js';
import {weekly, panelUsage, switchable, switchLocal} from './model.js';

const label = (text, style = '') => new St.Label({text, style_class: style, y_align: Clutter.ActorAlign.CENTER});
const icon = (name, size = 20) => new St.Icon({icon_name: name, icon_size: size});
function button(child, name, action, style = '') {
    const actor = new St.Button({child, accessible_name: name, reactive: true, can_focus: true,
        style_class: `cs-button ${style}`, y_align: Clutter.ActorAlign.CENTER});
    actor.connect('clicked', action);
    return actor;
}

export default class SyncBarExtension extends Extension {
    enable() {
        this._enabled = true;
        this._epoch = (this._epoch ?? 0) + 1;
        this._fetching = false;
        this._snapshotVersion = 0;
        this._starting = false;
        this._actionError = null;
        this._client = new Client();
        this._pending = false;
        this._unavailable = true;
        this._snapshot = {accounts: []};
        this._button = new PanelMenu.Button(0.5, 'Codex SyncBar', false);
        const brand = Gio.FileIcon.new(Gio.File.new_for_path(`${this.path}/icon.png`));
        const panel = new St.BoxLayout({style_class: 'panel-status-menu-box'});
        panel.add_child(new St.Icon({gicon: brand, style_class: 'system-status-icon'}));
        this._panelLabel = label('주간 —');
        panel.add_child(this._panelLabel);
        this._button.add_child(panel);
        this._button.menu.actor.add_style_class_name('cs-menu');
        this._button.menu.box.add_style_class_name('cs-content');
        Main.panel.addToStatusArea(this.uuid, this._button);
        const header = new St.BoxLayout({style_class: 'cs-header'});
        header.add_child(new St.Icon({gicon: brand, icon_size: 64}));
        const titles = new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, x_expand: true, style_class: 'cs-titles'});
        titles.add_child(label('Codex SyncBar', 'cs-title'));
        titles.add_child(label('이 PC', 'cs-subtitle'));
        header.add_child(titles);
        this._refreshButton = button(icon('view-refresh-symbolic', 24), '주간 사용량 새로고침', () => this._refreshUsage(), 'cs-refresh');
        header.add_child(this._refreshButton);
        this._button.menu.box.add_child(header);
        this._button.menu.box.add_child(label('주간 잔여량', 'cs-heading'));
        this._scroll = new St.ScrollView({hscrollbar_policy: St.PolicyType.NEVER,
            vscrollbar_policy: St.PolicyType.AUTOMATIC, overlay_scrollbars: true});
        this._rows = new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL});
        this._scroll.set_child(this._rows);
        this._button.menu.box.add_child(this._scroll);
        this._message = label('계정을 불러오는 중…', 'cs-message');
        this._message.clutter_text.line_wrap = true;
        this._button.menu.box.add_child(this._message);
        const footer = new St.BoxLayout({style_class: 'cs-footer'});
        this._updated = label('업데이트 확인 중', 'cs-updated');
        this._updated.x_expand = true;
        footer.add_child(this._updated);
        const open = new St.BoxLayout({style_class: 'cs-open-content'});
        open.add_child(icon('window-new-symbolic')); open.add_child(label('앱 열기')); open.add_child(icon('go-next-symbolic', 16));
        this._openButton = button(open, 'Codex SyncBar 앱 열기', () => this._openApp(), 'cs-open');
        footer.add_child(this._openButton);
        this._button.menu.box.add_child(footer);
        this._button.menu.connect('open-state-changed', (_menu, opened) => {
            if (opened) { this._fit(); this._connectService(); }
        });
        this._monitorSignal = Main.layoutManager.connect('monitors-changed', () => this._fit());
        this._interface = new Gio.Settings({schema_id: 'org.gnome.desktop.interface'});
        this._textSignal = this._interface.connect('changed::text-scaling-factor', () => this._fit());
        this._themeContext = St.ThemeContext.get_for_stage(global.stage);
        this._scaleSignal = this._themeContext.connect('notify::scale-factor', () => this._fit());
        this._fit();
        this._pollTicks = 0;
        this._timer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 3, () => {
            // Read the backend's cached snapshot even while the popup is closed.
            // The backend owns API refreshes; this does not add provider requests.
            if (this._button.menu.isOpen || this._pending || this._snapshot.isBusy || ++this._pollTicks >= 5) {
                this._pollTicks = 0;
                this._refreshSnapshot();
            }
            return GLib.SOURCE_CONTINUE;
        });
        this._refreshSnapshot();
    }

    async _connectService() {
        await this._refreshSnapshot();
        if (!this._enabled || !this._unavailable || this._starting) return;
        const paths = [GLib.build_filenamev([GLib.get_home_dir(), '.local/bin/codex-syncbar']), '/usr/bin/codex-syncbar'];
        const launcher = paths.find(path => GLib.file_test(path, GLib.FileTest.IS_EXECUTABLE));
        if (!launcher) return;
        this._starting = true;
        const epoch = this._epoch;
        try {
            const process = Gio.Subprocess.new([launcher, '--background'], Gio.SubprocessFlags.STDOUT_SILENCE | Gio.SubprocessFlags.STDERR_SILENCE);
            await new Promise((resolve, reject) => process.wait_check_async(null, (source, result) => {
                try { resolve(source.wait_check_finish(result)); } catch (error) { reject(error); }
            }));
            if (this._enabled && epoch === this._epoch) await this._refreshSnapshot();
        } catch { /* Keep the actionable connection notice and the app button. */ }
        finally { if (epoch === this._epoch) this._starting = false; }
    }

    _fit() {
        const monitor = Main.layoutManager.findMonitorForActor(this._button) ?? Main.layoutManager.primaryMonitor;
        if (!monitor) return;
        const scale = this._themeContext.scale_factor;
        const textScale = this._interface.get_double('text-scaling-factor');
        this._button.menu.actor.set_style(`font-size: ${15 * textScale}px;`);
        this._button.menu.box.set_style(`width: ${Math.min(580 * textScale, monitor.width / scale - 24)}px;`);
        const available = (monitor.height - Main.panel.height) / scale - 32;
        this._scroll.set_style(`max-height: ${Math.max(110, Math.min(520, available - 280 * textScale))}px;`);
    }

    async _refreshSnapshot() {
        if (this._fetching || !this._enabled) return;
        const epoch = this._epoch;
        const version = this._snapshotVersion;
        this._fetching = true;
        try {
            const snapshot = await this._client.call('snapshot');
            if (!this._enabled || epoch !== this._epoch || version !== this._snapshotVersion) return;
            this._unavailable = false;
            this._snapshot = snapshot;
            // Keep keyboard focus and scroll position during unchanged polls.
            this._render();
        } catch (error) {
            if (this._enabled && epoch === this._epoch) { this._unavailable = true; this._error = '서비스에 연결하지 못했습니다. 앱을 열어 다시 연결해 주세요.'; this._render(); }
        } finally { if (epoch === this._epoch) this._fetching = false; }
    }

    _render() {
        const snapshot = this._snapshot;
        const panel = panelUsage(snapshot, Date.now(), this._unavailable);
        this._panelLabel.text = panel.text;
        this._button.accessible_name = panel.description;
        const busy = this._pending || snapshot.isBusy;
        this._refreshButton.reactive = !busy && !this._unavailable;
        this._refreshButton.can_focus = this._refreshButton.reactive;
        const presented = snapshot.accounts.map(account => ({account, quota: weekly(account, Date.now(), this._unavailable)}))
            .sort((a, b) => Number(b.account.id === snapshot.activeProfileId) - Number(a.account.id === snapshot.activeProfileId));
        const key = JSON.stringify([presented, snapshot.activeProfileId, busy, snapshot.error, this._unavailable, snapshot.operation?.state]);
        if (key !== this._renderKey) {
            this._renderKey = key;
            const scroll = this._scroll.vadjustment.value;
            const focusId = this._accountButtons?.find(([_, actor]) => actor.has_key_focus())?.[0];
            this._rows.destroy_all_children();
            this._accountButtons = [];
            for (const {account, quota} of presented) this._addAccount(account, quota);
            if (!presented.length) this._rows.add_child(label(this._unavailable ? '계정 정보를 불러올 수 없습니다.' : '앱에서 계정을 추가해 주세요.', 'cs-empty'));
            this._scroll.vadjustment.value = scroll;
            this._accountButtons.find(([id, actor]) => id === focusId && actor.can_focus)?.[1].grab_key_focus();
        }
        const op = snapshot.operation;
        const failed = op && ['failed', 'recoveryRequired', 'cancelled'].includes(op.state);
        this._message.text = this._unavailable ? this._error : this._actionError || snapshot.error ||
            (busy ? (this._pending ? this._pendingMessage : op?.message) || '작업 중…' : failed ? op.message : '');
        this._message.visible = Boolean(this._message.text);
        const times = presented.map(item => item.quota.updated).filter(Number.isFinite);
        const date = new Date(Math.min(...times));
        this._updated.text = times.length ? `업데이트 ${date.getMonth() + 1}월 ${date.getDate()}일 ${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}` : '아직 업데이트되지 않음';
    }

    _addAccount(account, quota) {
        const active = account.id === this._snapshot.activeProfileId;
        const row = new St.BoxLayout({style_class: `cs-account ${active ? 'cs-active' : ''}`});
        const avatar = new St.Bin({child: icon('avatar-default-symbolic', 30), style_class: 'cs-avatar', y_align: Clutter.ActorAlign.CENTER});
        row.add_child(avatar);
        const identity = new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, style_class: 'cs-identity', y_align: Clutter.ActorAlign.CENTER});
        identity.add_child(label(account.alias || account.email, 'cs-alias'));
        const email = label(account.email.replace('@', '@\u200b'), 'cs-email');
        email.clutter_text.ellipsize = Pango.EllipsizeMode.NONE;
        email.clutter_text.line_wrap = true;
        email.clutter_text.line_wrap_mode = Pango.WrapMode.WORD_CHAR;
        const attributes = new Pango.AttrList();
        attributes.insert(Pango.attr_insert_hyphens_new(false));
        email.clutter_text.set_attributes(attributes);
        identity.add_child(email);
        row.add_child(identity);
        const usage = new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, style_class: `cs-usage cs-${quota.tone}`, x_expand: true, y_align: Clutter.ActorAlign.CENTER});
        const meter = new St.BoxLayout({style_class: 'cs-meter', x_expand: true});
        const track = quota.remaining === null
            ? new St.Widget({style_class: 'cs-track cs-unknown', x_expand: true, y_align: Clutter.ActorAlign.CENTER})
            : new BarLevel({value: quota.remaining / 100, style_class: 'cs-track', x_expand: true, y_align: Clutter.ActorAlign.CENTER,
                accessible_name: `${account.alias || account.email} 주간 잔여량`});
        meter.add_child(track);
        meter.add_child(label(quota.value, 'cs-percent'));
        usage.add_child(meter);
        usage.add_child(label(quota.caption, 'cs-reset'));
        row.add_child(usage);
        const action = new St.Bin({style_class: 'cs-action', y_align: Clutter.ActorAlign.CENTER});
        if (active) {
            const current = new St.BoxLayout({style_class: 'cs-current'});
            current.add_child(icon(this._unavailable ? 'document-open-recent-symbolic' : 'emblem-ok-symbolic', 18));
            current.add_child(label(this._unavailable ? '이전 계정' : '사용 중'));
            action.set_child(current);
        } else if (account.needsLogin || account.isPending) {
            action.set_child(label('—', 'cs-subtitle'));
        } else {
            const change = button(label('전환'), `${account.alias || account.email} 계정으로 이 PC 전환`, () => this._switch(account.id), 'cs-switch');
            change.reactive = !this._unavailable && switchable(account, this._snapshot, this._pending);
            change.can_focus = change.reactive;
            this._accountButtons.push([account.id, change]);
            action.set_child(change);
        }
        row.add_child(action);
        row.accessible_name = `${account.alias || account.email}, 주간 잔여량 ${quota.value}, ${quota.caption}${active ? ', 사용 중' : ''}`;
        this._rows.add_child(row);
        if (!active) this._rows.add_child(new St.Widget({style_class: 'cs-divider'}));
    }

    async _switch(profileId) {
        const account = this._snapshot.accounts.find(item => item.id === profileId);
        if (!account || this._unavailable || !switchable(account, this._snapshot, this._pending)) return;
        const epoch = this._epoch;
        this._snapshotVersion++;
        this._pending = true; this._pendingMessage = '이 PC의 계정을 전환하는 중…'; this._actionError = null; this._render();
        try {
            const client = this._client;
            const operation = await switchLocal((method, params) => client.call(method, params), profileId, GLib.uuid_string_random());
            if (this._enabled && epoch === this._epoch) {
                this._snapshot.operation = operation; this._snapshot.isBusy = !operation.isComplete;
                await this._refreshSnapshot();
            }
        } catch (error) { if (this._enabled && epoch === this._epoch) this._actionError = error.message; }
        finally { if (this._enabled && epoch === this._epoch) { this._pending = false; this._render(); } }
    }

    async _refreshUsage() {
        if (this._pending || this._snapshot.isBusy || this._unavailable) return;
        const epoch = this._epoch;
        this._snapshotVersion++;
        this._pending = true; this._pendingMessage = '주간 잔여량을 새로고침하는 중…'; this._actionError = null; this._render();
        try {
            const operation = await this._client.call('refresh', {kind: 'usage'});
            if (this._enabled && epoch === this._epoch) {
                this._snapshot.operation = operation; this._snapshot.isBusy = !operation.isComplete;
                await this._refreshSnapshot();
            }
        } catch (error) { if (this._enabled && epoch === this._epoch) this._actionError = error.message; }
        finally { if (this._enabled && epoch === this._epoch) { this._pending = false; this._render(); } }
    }

    _openApp() {
        this._button.menu.close();
        try {
            const app = GioUnix.DesktopAppInfo.new('io.github.codexsyncbar.Ubuntu.desktop');
            if (!app) throw new Error('Codex SyncBar 앱을 설치해 주세요.');
            app.launch([], global.create_app_launch_context(0, -1));
        } catch (error) { Main.notify('Codex SyncBar', error.message); }
    }

    disable() {
        this._enabled = false;
        if (this._timer) GLib.source_remove(this._timer);
        if (this._monitorSignal) Main.layoutManager.disconnect(this._monitorSignal);
        this._interface?.disconnect(this._textSignal);
        this._themeContext?.disconnect(this._scaleSignal);
        this._client?.destroy();
        this._button?.destroy();
        this._button = null; this._client = null; this._interface = null; this._themeContext = null;
        this._snapshot = null; this._renderKey = null; this._accountButtons = null;
    }
}
