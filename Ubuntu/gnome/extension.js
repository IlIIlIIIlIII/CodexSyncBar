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
import {usage, visibleQuotas, panelUsage, subtitle, switchable, switchLocal, smartSwitch, localNotice} from './model.js';

const label = (text, style = '') => new St.Label({text, style_class: style, y_align: Clutter.ActorAlign.CENTER});
const icon = (name, size = 20) => new St.Icon({icon_name: name, icon_size: size});
function button(child, name, action, style = '') {
    const actor = new St.Button({child, accessible_name: name, reactive: true, can_focus: true,
        style_class: `clsb-button ${style}`, y_align: Clutter.ActorAlign.CENTER});
    actor.connect('clicked', action);
    return actor;
}

export default class SyncBarExtension extends Extension {
    enable() {
        this._enabled = true;
        this._epoch = (this._epoch ?? 0) + 1;
        this._fetching = null;
        this._snapshotVersion = 0;
        this._starting = false;
        this._actionError = null;
        this._client = new Client();
        this._pending = false;
        this._unavailable = true;
        this._snapshot = {accounts: []};
        this._button = new PanelMenu.Button(0.5, 'Claude SyncBar', false);
        const brand = Gio.FileIcon.new(Gio.File.new_for_path(`${this.path}/icon.png`));
        const panel = new St.BoxLayout({style_class: 'panel-status-menu-box'});
        panel.add_child(new St.Icon({gicon: brand, style_class: 'system-status-icon'}));
        this._panelLabel = label(panelUsage(this._snapshot).text);
        panel.add_child(this._panelLabel);
        this._button.add_child(panel);
        this._button.menu.actor.add_style_class_name('clsb-menu');
        this._button.menu.box.add_style_class_name('clsb-content');
        Main.panel.addToStatusArea(this.uuid, this._button);
        const header = new St.BoxLayout({style_class: 'clsb-header'});
        header.add_child(new St.Icon({gicon: brand, icon_size: 64}));
        const titles = new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, x_expand: true, style_class: 'clsb-titles'});
        titles.add_child(label('Claude SyncBar', 'clsb-title'));
        this._subtitle = label('이 PC', 'clsb-subtitle');
        titles.add_child(this._subtitle);
        header.add_child(titles);
        const smart = new St.BoxLayout({style_class: 'clsb-smart-content'});
        smart.add_child(icon('emblem-synchronizing-symbolic', 18)); smart.add_child(label('스마트 전환'));
        this._smartButton = button(smart, '스마트 전환 후보 중 주간이 남고 5시간을 75% 미만 쓴 계정에서 주간 초기화가 가장 가까운 계정으로 모든 장치 전환', () => this._smartSwitch(), 'clsb-smart');
        header.add_child(this._smartButton);
        this._refreshButton = button(icon('view-refresh-symbolic', 24), '사용량 새로고침', () => this._refreshUsage(), 'clsb-refresh');
        header.add_child(this._refreshButton);
        this._button.menu.box.add_child(header);
        this._button.menu.box.add_child(label('계정별 잔여량', 'clsb-heading'));
        this._scroll = new St.ScrollView({hscrollbar_policy: St.PolicyType.NEVER,
            vscrollbar_policy: St.PolicyType.AUTOMATIC, overlay_scrollbars: true});
        this._rows = new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL});
        this._scroll.set_child(this._rows);
        this._button.menu.box.add_child(this._scroll);
        this._message = label('계정을 불러오는 중…', 'clsb-message');
        this._message.clutter_text.line_wrap = true;
        this._button.menu.box.add_child(this._message);
        const footer = new St.BoxLayout({style_class: 'clsb-footer'});
        this._updated = label('업데이트 확인 중', 'clsb-updated');
        this._updated.x_expand = true;
        footer.add_child(this._updated);
        const open = new St.BoxLayout({style_class: 'clsb-open-content'});
        open.add_child(icon('window-new-symbolic')); open.add_child(label('앱 열기')); open.add_child(icon('go-next-symbolic', 16));
        this._openButton = button(open, 'Claude SyncBar 앱 열기', () => this._openApp(), 'clsb-open');
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
        // The panel shows usage only while the service runs, so start it with the session
        // instead of on the first click. Only the first enable of this Shell does so: a screen
        // unlock enables again and must not bring back a service the app quit.
        if (this._autostarted) this._refreshSnapshot();
        else if (Main.layoutManager._startingUp) {
            this._startupSignal = Main.layoutManager.connect('startup-complete', () => {
                Main.layoutManager.disconnect(this._startupSignal);
                this._startupSignal = 0;
                this._autostart();
            });
        } else this._autostart();
    }

    _autostart() {
        this._autostarted = true;
        this._connectService();
    }

    async _connectService() {
        if (!this._enabled || this._starting) return;
        const epoch = this._epoch;
        this._starting = true;
        try {
            await this._refreshSnapshot();
            if (!this._enabled || epoch !== this._epoch || !this._unavailable) return;
            const paths = [GLib.build_filenamev([GLib.get_home_dir(), '.local/bin/claude-syncbar']), '/usr/bin/claude-syncbar'];
            const launcher = paths.find(path => GLib.file_test(path, GLib.FileTest.IS_EXECUTABLE));
            if (!launcher) return;
            const process = Gio.Subprocess.new([launcher, '--background'], Gio.SubprocessFlags.STDOUT_SILENCE | Gio.SubprocessFlags.STDERR_SILENCE);
            await new Promise((resolve, reject) => process.wait_check_async(null, (source, result) => {
                try { resolve(source.wait_check_finish(result)); } catch (error) { reject(error); }
            }));
            // systemctl returns once the backend is spawned; it still has to listen and
            // finish its startup recovery before the first snapshot answers.
            const deadline = GLib.get_monotonic_time() + 60 * GLib.USEC_PER_SEC;
            while (this._enabled && epoch === this._epoch && GLib.get_monotonic_time() < deadline) {
                await this._refreshSnapshot();
                if (!this._unavailable || !this._enabled || epoch !== this._epoch) break;
                await new Promise(resolve => {
                    this._retry = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 1000, () => {
                        this._retry = 0; resolve(); return GLib.SOURCE_REMOVE;
                    });
                });
            }
        } catch { /* Keep the actionable connection notice and the app button. */ }
        finally {
            if (epoch === this._epoch) {
                this._starting = false;
                if (this._enabled) this._render();
            }
        }
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

    _refreshSnapshot() {
        if (!this._enabled) return Promise.resolve();
        // Callers share a read already in flight, so they see its result instead of the state before it.
        if (!this._fetching) {
            const read = this._readSnapshot();
            this._fetching = read;
            const clear = () => { if (this._fetching === read) this._fetching = null; };
            read.then(clear, clear);
        }
        return this._fetching;
    }

    async _readSnapshot() {
        const epoch = this._epoch;
        const version = this._snapshotVersion;
        try {
            const snapshot = await this._client.call('snapshot');
            if (!this._enabled || epoch !== this._epoch || version !== this._snapshotVersion) return;
            this._unavailable = false;
            this._snapshot = snapshot;
            // Keep keyboard focus and scroll position during unchanged polls.
            this._render();
        } catch (error) {
            if (this._enabled && epoch === this._epoch) { this._unavailable = true; this._error = '서비스에 연결하지 못했습니다. 앱을 열어 다시 연결해 주세요.'; this._render(); }
        }
    }

    _render() {
        const snapshot = this._snapshot;
        const panel = panelUsage(snapshot, Date.now(), this._unavailable);
        this._panelLabel.text = panel.text;
        this._subtitle.text = subtitle(snapshot);
        this._button.accessible_name = panel.description;
        const busy = this._pending || snapshot.isBusy;
        this._refreshButton.reactive = !busy && !this._unavailable;
        this._refreshButton.can_focus = this._refreshButton.reactive;
        this._smartButton.reactive = !busy && !this._unavailable && !snapshot.error && snapshot.operation?.state !== 'recoveryRequired' && snapshot.accounts.length > 1;
        this._smartButton.can_focus = this._smartButton.reactive;
        const keys = visibleQuotas(snapshot.settings);
        const presented = snapshot.accounts.map(account => ({account, usage: usage(account, keys, Date.now(), this._unavailable)}))
            .sort((a, b) => Number(b.account.id === snapshot.activeProfileId) - Number(a.account.id === snapshot.activeProfileId));
        // Before the first answer after a start, the service is coming up rather than failing.
        const starting = this._unavailable && this._starting;
        const key = JSON.stringify([presented, snapshot.activeProfileId, busy, snapshot.error, this._unavailable, starting, snapshot.operation?.state]);
        if (key !== this._renderKey) {
            this._renderKey = key;
            const scroll = this._scroll.vadjustment.value;
            const focusId = this._accountButtons?.find(([_, actor]) => actor.has_key_focus())?.[0];
            this._rows.destroy_all_children();
            this._accountButtons = [];
            for (const item of presented) this._addAccount(item.account, item.usage);
            if (!presented.length) this._rows.add_child(label(starting ? '계정을 불러오는 중…' : this._unavailable ? '계정 정보를 불러올 수 없습니다.' : '앱에서 계정을 추가해 주세요.', 'clsb-empty'));
            this._scroll.vadjustment.value = scroll;
            this._accountButtons.find(([id, actor]) => id === focusId && actor.can_focus)?.[1].grab_key_focus();
        }
        const op = snapshot.operation;
        const failed = op && ['failed', 'recoveryRequired', 'cancelled'].includes(op.state);
        this._message.text = starting ? '서비스를 시작하는 중…' : this._unavailable ? this._error : this._actionError || snapshot.error ||
            (busy ? (this._pending ? this._pendingMessage : op?.message) || '작업 중…' : failed ? op.message : localNotice(snapshot));
        this._message.visible = Boolean(this._message.text);
        const times = presented.map(item => item.usage.updated).filter(Number.isFinite);
        const date = new Date(Math.min(...times));
        this._updated.text = times.length ? `업데이트 ${date.getMonth() + 1}월 ${date.getDate()}일 ${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}` : '아직 업데이트되지 않음';
    }

    _addAccount(account, state) {
        const active = account.id === this._snapshot.activeProfileId;
        const name = account.alias || account.email;
        const row = new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, style_class: `clsb-account ${active ? 'clsb-active' : ''}`});
        const top = new St.BoxLayout({style_class: 'clsb-account-top'});
        const avatar = new St.Bin({child: icon('avatar-default-symbolic', 30), style_class: 'clsb-avatar', y_align: Clutter.ActorAlign.CENTER});
        top.add_child(avatar);
        const identity = new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, style_class: 'clsb-identity', x_expand: true, y_align: Clutter.ActorAlign.CENTER});
        identity.add_child(label(name, 'clsb-alias'));
        const details = [account.email !== name ? account.email.replace('@', '@​') : '', account.plan ?? ''].filter(Boolean).join(' · ');
        const email = label(details, 'clsb-email');
        email.clutter_text.ellipsize = Pango.EllipsizeMode.NONE;
        email.clutter_text.line_wrap = true;
        email.clutter_text.line_wrap_mode = Pango.WrapMode.WORD_CHAR;
        const attributes = new Pango.AttrList();
        attributes.insert(Pango.attr_insert_hyphens_new(false));
        email.clutter_text.set_attributes(attributes);
        if (details) identity.add_child(email);
        if (state.status) identity.add_child(label(state.status, `clsb-status clsb-status-${state.statusTone}`));
        top.add_child(identity);
        const action = new St.Bin({style_class: 'clsb-action', y_align: Clutter.ActorAlign.CENTER});
        if (active) {
            const current = new St.BoxLayout({style_class: 'clsb-current'});
            current.add_child(icon(this._unavailable ? 'document-open-recent-symbolic' : 'emblem-ok-symbolic', 18));
            current.add_child(label(this._unavailable ? '이전 계정' : '사용 중'));
            action.set_child(current);
        } else if (account.needsLogin || account.isPending) {
            action.set_child(label('—', 'clsb-subtitle'));
        } else {
            const change = button(label('전환'), `${name} 계정으로 이 PC 전환`, () => this._switch(account.id), 'clsb-switch');
            change.reactive = !this._unavailable && switchable(account, this._snapshot, this._pending);
            change.can_focus = change.reactive;
            this._accountButtons.push([account.id, change]);
            action.set_child(change);
        }
        top.add_child(action);
        row.add_child(top);
        // One line per quota: name, bar, what is left, when it resets.
        const quotas = new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, style_class: 'clsb-quotas'});
        for (const item of state.quotas) {
            const line = new St.BoxLayout({style_class: `clsb-quota clsb-${item.tone}`});
            line.add_child(label(item.title, 'clsb-quota-name'));
            line.add_child(item.remaining === null
                ? new St.Widget({style_class: 'clsb-track clsb-unknown', x_expand: true, y_align: Clutter.ActorAlign.CENTER})
                : new BarLevel({value: item.remaining / 100, style_class: 'clsb-track', x_expand: true, y_align: Clutter.ActorAlign.CENTER,
                    accessible_name: `${name} ${item.title} 잔여량`}));
            const percent = label(item.value, 'clsb-percent');
            percent.clutter_text.x_align = Clutter.ActorAlign.END;
            line.add_child(percent);
            line.add_child(label(item.caption, 'clsb-reset'));
            quotas.add_child(line);
        }
        if (state.quotas.length) row.add_child(quotas);
        const spoken = state.quotas.map(item => item.remaining === null ? `${item.title} 정보 없음` : `${item.title} 잔여량 ${item.value}, ${item.caption}`);
        row.accessible_name = [name, ...spoken, state.status, active ? '사용 중' : ''].filter(Boolean).join(', ');
        this._rows.add_child(row);
        if (!active) this._rows.add_child(new St.Widget({style_class: 'clsb-divider'}));
    }

    async _switch(profileId) {
        const account = this._snapshot.accounts.find(item => item.id === profileId);
        if (!account || this._unavailable || !switchable(account, this._snapshot, this._pending)) return;
        const epoch = this._epoch;
        this._snapshotVersion++;
        this._pending = true; this._pendingMessage = '이 PC의 계정을 전환하는 중…'; this._actionError = null; this._render();
        try {
            const client = this._client;
            const operation = await switchLocal((method, params) => client.call(method, params), profileId, GLib.uuid_string_random(), () => {
                if (!this._enabled || epoch !== this._epoch) return;
                this._pendingMessage = '이 PC의 계정을 전환하고 Claude 데스크톱 앱을 다시 여는 중…'; this._render();
            });
            if (this._enabled && epoch === this._epoch) {
                this._snapshot.operation = operation; this._snapshot.isBusy = !operation.isComplete;
                await this._refreshSnapshot();
            }
        } catch (error) { if (this._enabled && epoch === this._epoch) this._actionError = error.message; }
        finally { if (this._enabled && epoch === this._epoch) { this._pending = false; this._render(); } }
    }

    async _smartSwitch() {
        if (this._pending || this._snapshot.isBusy || this._unavailable) return;
        const epoch = this._epoch;
        this._snapshotVersion++;
        this._pending = true; this._pendingMessage = '사용량을 확인하고 전환할 계정을 고르는 중…'; this._actionError = null; this._render();
        try {
            const client = this._client;
            const result = await smartSwitch((method, params) => client.call(method, params), GLib.uuid_string_random(), () => {
                if (!this._enabled || epoch !== this._epoch) return;
                this._pendingMessage = '스마트 전환 중 · Claude 데스크톱 앱을 다시 여는 중…'; this._render();
            });
            if (this._enabled && epoch === this._epoch) {
                if (result.operation) { this._snapshot.operation = result.operation; this._snapshot.isBusy = !result.operation.isComplete; }
                else this._actionError = result.message;
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
            const app = GioUnix.DesktopAppInfo.new('io.github.claudesyncbar.Ubuntu.desktop');
            if (!app) throw new Error('Claude SyncBar 앱을 설치해 주세요.');
            app.launch([], global.create_app_launch_context(0, -1));
        } catch (error) { Main.notify('Claude SyncBar', error.message); }
    }

    disable() {
        this._enabled = false;
        if (this._timer) GLib.source_remove(this._timer);
        if (this._retry) GLib.source_remove(this._retry);
        if (this._startupSignal) Main.layoutManager.disconnect(this._startupSignal);
        this._retry = 0; this._startupSignal = 0;
        if (this._monitorSignal) Main.layoutManager.disconnect(this._monitorSignal);
        this._interface?.disconnect(this._textSignal);
        this._themeContext?.disconnect(this._scaleSignal);
        this._client?.destroy();
        this._button?.destroy();
        this._button = null; this._client = null; this._interface = null; this._themeContext = null;
        this._snapshot = null; this._renderKey = null; this._accountButtons = null;
    }
}
