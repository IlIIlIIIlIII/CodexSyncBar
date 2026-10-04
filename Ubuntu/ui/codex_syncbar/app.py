"""Yaru-native account transition screen. All state comes from the service."""
import datetime as dt
import json
from pathlib import Path
import uuid

import gi
gi.require_version('Gtk', '4.0')
gi.require_version('Adw', '1')
gi.require_version('Gsk', '4.0')
from gi.repository import Adw, Gdk, Gio, GLib, Gsk, Gtk, Pango
from .ipc import Client, ServiceError

CSS = b'''
.main-content { padding: 30px 32px; font-size: 16px; }
.screen-title { font-size: 30px; font-weight: 700; }
.section-title { font-size: 18px; font-weight: 700; }
.account-title { font-size: 17px; font-weight: 650; }
.muted { opacity: .65; }
.small { font-size: 13px; }
.avatar { border-radius: 999px; background: alpha(@window_fg_color, .12); padding: 14px; }
.avatar.active { background: alpha(@accent_bg_color, .28); color: @accent_color; }
.account-group { padding: 18px; }
.account-picker-row { padding: 14px 10px; }
.account-pane { padding-right: 22px; }
.detail-pane { padding-left: 22px; }
.quota-value { font-size: 32px; font-weight: 500; color: @accent_color; }
progressbar trough { min-height: 6px; border-radius: 8px; }
progressbar progress { min-height: 6px; border-radius: 8px; background: @accent_bg_color; }
.preview-table row { padding: 12px 16px; }
.preview-table row:not(:last-child) { border-bottom: 1px solid alpha(@window_fg_color, .10); }
.preview-header { padding: 10px 16px; }
.target-account { color: @window_fg_color; font-weight: 600; }
.action-bar { padding: 16px 28px; border-top: 1px solid alpha(@window_fg_color, .12); }
.action-bar button.suggested-action { padding: 10px 26px; font-weight: 700; }
.content-divider { margin: 18px 0; }
.status-line { padding: 8px 12px; border-radius: 8px; background: alpha(@window_fg_color, .05); }
.error-text { color: @error_color; }
.success-text { color: @success_color; }
.dialog-body { padding: 20px; }
.compact .main-content { padding: 18px 24px; font-size: 14px; }
.compact .screen-title { font-size: 25px; }
.compact .section-title { font-size: 16px; }
.compact .quota-value { font-size: 27px; }
.compact .avatar { padding: 10px; }
.compact .account-group { padding: 12px; }
.compact .account-picker-row { padding: 8px 6px; }
.compact .content-divider { margin: 10px 0; }
.compact .preview-table row { padding: 8px 12px; }
.compact .small { font-size: 12px; }
.compact .action-bar { padding: 12px 24px; }
.compact .action-bar button.suggested-action { padding: 8px 22px; }

'''


def label(text='', css=None, wrap=False, xalign=0):
    widget = Gtk.Label(label=text, xalign=xalign, wrap=wrap)
    if wrap:
        widget.set_wrap_mode(Pango.WrapMode.WORD_CHAR)
    if css:
        for name in css.split():
            widget.add_css_class(name)
    return widget


def box(vertical=True, spacing=8):
    return Gtk.Box(orientation=Gtk.Orientation.VERTICAL if vertical else Gtk.Orientation.HORIZONTAL, spacing=spacing)


def icon(name, size=20):
    image = Gtk.Image.new_from_icon_name(name)
    image.set_pixel_size(size)
    return image


def button(text=None, symbol=None, callback=None, css=None):
    widget = Gtk.Button(label=text) if text else Gtk.Button(icon_name=symbol)
    if callback:
        widget.connect('clicked', callback)
    if css:
        for name in css.split():
            widget.add_css_class(name)
    return widget


def clear(widget):
    child = widget.get_first_child()
    while child:
        following = child.get_next_sibling()
        widget.remove(child)
        child = following


def account_name(account):
    return account.get('alias') or account.get('email') or '계정'


def timestamp(value):
    if not value:
        return None
    try:
        return dt.datetime.fromisoformat(value.replace('Z', '+00:00')).astimezone()
    except (TypeError, ValueError):
        return None


def reset_text(value):
    moment = timestamp(value)
    if not moment:
        return '초기화 시각 확인 필요'
    return f'{moment.month}월 {moment.day}일 {moment:%H:%M} 초기화'


class Quota(Gtk.Box):
    def __init__(self, title):
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=10, hexpand=True)
        self.append(label(title))
        self.value = label('확인 중', 'quota-value')
        self.append(self.value)
        self.meter = Gtk.ProgressBar()
        self.append(self.meter)
        self.reset = label('', 'muted small', True)
        self.append(self.reset)

    def update(self, window):
        self.set_visible(window is not None)
        if not window:
            return
        remaining = window.get('remainingPercent')
        if remaining is None and window.get('usedPercent') is not None:
            remaining = 100 - window['usedPercent']
        if remaining is None:
            self.value.set_text('확인 필요')
            self.meter.set_fraction(0)
        else:
            remaining = max(0, min(100, remaining))
            self.value.set_text(f'{remaining:.0f}% 남음')
            self.meter.set_fraction(remaining / 100)
        self.reset.set_text(reset_text(window.get('resetsAt')))


class SyncBarApplication(Adw.Application):
    def __init__(self, args):
        super().__init__(application_id='io.github.codexsyncbar.Ubuntu.Demo' if args.demo else 'io.github.codexsyncbar.Ubuntu',
                         flags=Gio.ApplicationFlags.NON_UNIQUE if args.demo else Gio.ApplicationFlags.DEFAULT_FLAGS)
        self.args = args
        self.client = Client(args.socket)
        self.window = None
        self.snapshot = {}
        self.accounts = []
        self.selected_id = args.profile_id
        self.preview = None
        self.selection_generation = 0
        self.loading_accounts = False
        self.polling = False
        self.busy = False
        self._last_operation = None
        self._preview_revision = None
        self._pending_request_id = None
        self._capture_started = False
        self._narrow = None
        self._dialogs = []

    def do_activate(self):
        if self.window:
            self.window.present()
            self.poll()
            return
        provider = Gtk.CssProvider()
        provider.load_from_data(CSS)
        Gtk.StyleContext.add_provider_for_display(Gdk.Display.get_default(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        if self.args.light:
            self.get_style_manager().set_color_scheme(Adw.ColorScheme.FORCE_LIGHT)
        self.window = Adw.ApplicationWindow(application=self, title='Codex SyncBar')
        self.window.set_default_size(self.args.width, self.args.height)
        self.window.set_size_request(600, 480)
        self.window.connect('close-request', self.on_close)
        self.toasts = Adw.ToastOverlay()
        self.window.set_content(self.toasts)
        shell = Adw.ToolbarView()
        self.toasts.set_child(shell)
        header = Adw.HeaderBar()
        header.set_title_widget(Adw.WindowTitle(title='Codex SyncBar', subtitle='데모 · 가상 데이터' if self.args.demo else ''))
        refresh = button(symbol='view-refresh-symbolic', callback=lambda *_: self.refresh())
        refresh.set_tooltip_text('모든 상태 새로고침 (Ctrl+R)')
        header.pack_end(refresh)
        menu = Gio.Menu()
        for title, action_name in [('계정 관리', 'accounts'), ('장치 관리', 'devices'), ('Codex CLI 관리', 'cli'), ('토큰 집계', 'tokens'), ('설정', 'settings')]:
            action = Gio.SimpleAction.new(action_name, None)
            action.connect('activate', lambda _a, _p, name=action_name: self.open_dialog(name))
            self.add_action(action)
            menu.append(title, f'app.{action_name}')
        end = Gio.Menu()
        end.append('완전히 종료', 'app.quit-service')
        menu.append_section(None, end)
        menu_button = Gtk.MenuButton(icon_name='open-menu-symbolic', menu_model=menu)
        menu_button.set_tooltip_text('메뉴')
        header.pack_end(menu_button)
        shell.add_top_bar(header)
        for name, callback in [('refresh', lambda *_: self.refresh()), ('quit-service', self.quit_service)]:
            action = Gio.SimpleAction.new(name, None)
            action.connect('activate', callback)
            self.add_action(action)
        self.set_accels_for_action('app.refresh', ['<Primary>r'])
        self.set_accels_for_action('app.quit-service', ['<Primary>q'])
        content = box(spacing=0)
        shell.set_content(content)
        self.scroll = Gtk.ScrolledWindow(vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER)
        content.append(self.scroll)
        body = box(spacing=0)
        body.add_css_class('main-content')
        self.scroll.set_child(body)
        body.append(label('계정 전환', 'screen-title'))
        subtitle = label('적용할 계정과 장치를 확인하세요.', 'muted', True)
        self.subtitle = subtitle
        subtitle.set_margin_top(8)
        subtitle.set_margin_bottom(24)
        body.append(subtitle)
        self.columns = box(False, 0)
        body.append(self.columns)
        self.left = box(spacing=12)
        self.left.add_css_class('account-pane')
        self.left.set_hexpand(False)
        self.left.set_size_request(290, -1)
        self.columns.append(self.left)
        self.divider = Gtk.Separator(orientation=Gtk.Orientation.VERTICAL)
        self.columns.append(self.divider)
        self.right = box(spacing=10)
        self.right.add_css_class('detail-pane')
        self.right.set_hexpand(True)
        self.columns.append(self.right)
        self.build_accounts()
        self.build_details()
        self.build_footer(content)
        self.reflow()
        self.window.present()
        self.poll()
        GLib.timeout_add(2000, self.poll)
        GLib.timeout_add(150, self.reflow)

    def build_accounts(self):
        self.left.append(label('현재 적용 · 이 Ubuntu PC', 'section-title'))
        current = box(False, 14)
        current.add_css_class('card')
        current.add_css_class('account-group')
        avatar = icon('avatar-default-symbolic', 24)
        avatar.add_css_class('avatar')
        avatar.add_css_class('active')
        current.append(avatar)
        names = box(spacing=4)
        names.set_hexpand(True)
        self.active_name = label('확인 중', 'account-title', True)
        self.active_email = label('', 'muted small', True)
        names.append(self.active_name)
        names.append(self.active_email)
        current.append(names)
        self.active_check = icon('object-select-symbolic')
        self.active_check.add_css_class('success-text')
        current.append(self.active_check)
        self.left.append(current)
        arrow = icon('go-down-symbolic', 26)
        arrow.add_css_class('muted')
        arrow.set_margin_top(12)
        arrow.set_margin_bottom(10)
        self.left.append(arrow)
        self.left.append(label('전환할 계정', 'section-title'))
        self.account_model = Gtk.StringList.new([])
        self.account_choice = Gtk.DropDown(model=self.account_model, enable_search=True)
        self.account_choice.set_tooltip_text('전환할 계정 선택. 선택만으로 적용되지 않습니다.')
        factory = Gtk.SignalListItemFactory()
        def setup(_factory, item):
            row = box(False, 12)
            row.add_css_class('account-picker-row')
            avatar = icon('avatar-default-symbolic', 24)
            avatar.add_css_class('avatar')
            row.append(avatar)
            names = box(spacing=4)
            names.append(label('', 'account-title', True))
            names.append(label('', 'muted small', True))
            row.append(names)
            item.set_child(row)
        def bind(_factory, item):
            row = item.get_child()
            names = row.get_last_child()
            position = item.get_position()
            account = self.accounts[position] if position < len(self.accounts) else {}
            names.get_first_child().set_text(account_name(account))
            names.get_last_child().set_text(account.get('email', ''))
            names.get_last_child().set_visible(account.get('email') != account_name(account))
        factory.connect('setup', setup)
        factory.connect('bind', bind)
        self.account_choice.set_factory(factory)
        self.account_choice.connect('notify::selected', self.selection_changed)
        self.left.append(self.account_choice)
        self.selected_email = label('', 'muted', True)
        self.left.append(self.selected_email)
        self.selected_email.set_visible(False)
        self.auth_status = label('', 'muted small', True)
        self.left.append(self.auth_status)
        self.login_button = button('로그인 열기', callback=self.login_selected)
        self.login_button.set_visible(False)
        self.left.append(self.login_button)
        self.add_account_button = button('계정 추가', callback=lambda *_: self.add_account())
        self.add_account_button.set_margin_top(16)
        self.left.append(self.add_account_button)
        self.login_actions = box(spacing=8)
        self.login_actions.append(label('브라우저에서 로그인을 기다리고 있습니다.', 'small', True))
        self.login_actions.append(button('로그인 창 다시 열기', callback=lambda *_: self.reopen_login()))
        self.login_actions.append(button('로그인 취소', callback=lambda *_: self.command('account.cancel')))
        self.login_actions.set_visible(False)
        self.left.append(self.login_actions)
        spacer = box()
        spacer.set_vexpand(True)
        self.left.append(spacer)
        hint = label('적용 버튼을 눌러야 계정이 전환됩니다.', 'muted small', True)
        hint.set_margin_top(24)
        self.left.append(hint)

    def build_details(self):
        self.right.append(label('선택한 계정 사용량', 'section-title'))
        self.usage_updated = label('사용량을 확인하고 있습니다.', 'muted small', True)
        self.right.append(self.usage_updated)
        self.quotas = box(False, 24)
        self.quotas.set_margin_top(12)
        self.session_quota = Quota('5시간')
        self.weekly_quota = Quota('주간')
        self.quotas.append(self.session_quota)
        self.quotas.append(self.weekly_quota)
        self.right.append(self.quotas)
        self.usage_empty = label('사용량 정보가 없습니다.', 'muted', True)
        self.right.append(self.usage_empty)
        self.credits = label('', wrap=True)
        self.credits.set_margin_top(10)
        self.right.append(self.credits)
        separator = Gtk.Separator()
        separator.add_css_class('content-divider')
        self.right.append(separator)
        self.right.append(label('장치별 적용 미리보기', 'section-title'))
        self.device_scope = label('장치를 확인하고 있습니다.', 'muted small', True)
        self.right.append(self.device_scope)
        self.table = box(spacing=0)
        self.table.add_css_class('frame')
        self.table.set_margin_top(8)
        self.right.append(self.table)
        self.table_header = self.device_columns('장치', '현재', '적용 후 · 예정', heading=True)
        self.table_header.add_css_class('preview-header')
        self.table.append(self.table_header)
        self.device_list = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE)
        self.device_list.add_css_class('boxed-list')
        self.device_list.add_css_class('preview-table')
        self.table.append(self.device_list)
        self.status_text = label('', 'small', True)
        self.status_text.add_css_class('status-line')
        self.status_text.set_margin_top(12)
        self.right.append(self.status_text)
        self.recover_button = button('복구 다시 시도', callback=lambda *_: self.command('recovery.retry'))
        self.recover_button.set_visible(False)
        self.right.append(self.recover_button)

    def build_footer(self, content):
        self.footer = box(False, 18)
        self.footer.add_css_class('action-bar')
        content.append(self.footer)
        self.footer_info = box(spacing=4)
        self.footer_info.set_hexpand(True)
        self.transition = label('계정을 선택해 주세요.', 'account-title', True)
        self.scope = label('', 'muted small', True)
        self.footer_info.append(self.transition)
        self.footer_info.append(self.scope)
        self.footer.append(self.footer_info)
        self.spinner = Gtk.Spinner()
        self.footer.append(self.spinner)
        self.apply_button = button('모든 장치에 적용', callback=self.apply, css='suggested-action')
        self.apply_button.set_valign(Gtk.Align.CENTER)
        self.apply_button.set_sensitive(False)
        self.footer.append(self.apply_button)

    def reflow(self):
        if not self.window:
            return False
        width = self.window.get_width() or self.args.width
        height = self.window.get_height() or self.args.height
        compact = height < 900
        if compact:
            self.window.add_css_class('compact')
        else:
            self.window.remove_css_class('compact')
        self.subtitle.set_margin_bottom(16 if compact else 24)
        self.right.set_spacing(6 if compact else 10)
        self.quotas.set_margin_top(6 if compact else 12)
        self.credits.set_margin_top(6 if compact else 10)
        narrow = width < 900
        self.left.set_size_request(-1 if narrow else max(280, min(460, int((width - 64) * .32))), -1)
        if narrow == self._narrow:
            return True
        self._narrow = narrow
        self.columns.set_orientation(Gtk.Orientation.VERTICAL if narrow else Gtk.Orientation.HORIZONTAL)
        self.columns.set_spacing(24 if narrow else 0)
        self.divider.set_visible(not narrow)
        for pane, style in [(self.left, 'account-pane'), (self.right, 'detail-pane')]:
            if narrow:
                pane.remove_css_class(style)
            else:
                pane.add_css_class(style)
        self.footer.set_orientation(Gtk.Orientation.VERTICAL if width < 700 else Gtk.Orientation.HORIZONTAL)
        self.apply_button.set_halign(Gtk.Align.FILL if width < 700 else Gtk.Align.END)
        self.render_devices()
        return True

    def lookup(self, profile_id):
        return next((item for item in self.accounts if item.get('id') == profile_id), {})

    def selected(self):
        return self.lookup(self.selected_id)

    def poll(self):
        if not self.window:
            return False
        if not self.polling:
            self.polling = True
            self.client.submit('snapshot', callback=self.on_snapshot)
        return True

    def on_snapshot(self, snapshot, error):
        self.polling = False
        if not self.window:
            return False
        if error:
            self.status_text.set_text(str(error))
            self.apply_button.set_sensitive(False)
            return False
        if self.args.demo and not (snapshot or {}).get('isDemo'):
            self.status_text.set_text('데모 모드는 운영 서비스에 연결할 수 없습니다.')
            self.apply_button.set_sensitive(False)
            self.client.close()
            self.quit()
            return False
        self.snapshot = snapshot or {}
        previous_accounts = [(a.get('id'), account_name(a), a.get('needsLogin')) for a in self.accounts]
        self.accounts = self.snapshot.get('accounts') or []
        current_accounts = [(a.get('id'), account_name(a), a.get('needsLogin')) for a in self.accounts]
        if current_accounts != previous_accounts:
            self.loading_accounts = True
            self.account_model.splice(0, self.account_model.get_n_items(), [account_name(a) for a in self.accounts])
            if not self.lookup(self.selected_id):
                self.selected_id = self.snapshot.get('activeProfileId') or (self.accounts[0]['id'] if self.accounts else None)
            index = next((i for i, a in enumerate(self.accounts) if a['id'] == self.selected_id), Gtk.INVALID_LIST_POSITION)
            self.account_choice.set_selected(index)
            self.loading_accounts = False
            self.request_preview()
        active = self.lookup(self.snapshot.get('activeProfileId'))
        self.active_name.set_text(account_name(active) if active else '등록된 계정 없음')
        self.active_email.set_text(active.get('email', ''))
        self.active_email.set_visible(bool(active.get('email') and active.get('email') != account_name(active)))
        self.active_check.set_visible(bool(active))
        self.busy = bool(self.snapshot.get('isBusy'))
        operation = self.snapshot.get('operation') or {}
        operation_key = (operation.get('id'), operation.get('state'))
        if operation_key != self._last_operation:
            self._last_operation = operation_key
            if operation.get('state') in ('completed', 'failed', 'recoveryRequired'):
                self._pending_request_id = None
                self.request_preview()
        if self._preview_revision != self.snapshot.get('configurationRevision') and not self.busy:
            self.request_preview()
        self.render_usage()
        self.render_devices()
        self.render_state()
        if self.args.capture and self.preview and not self._capture_started:
            self._capture_started = True
            GLib.timeout_add(800, self.capture)
        return False

    def selection_changed(self, *_):
        if self.loading_accounts:
            return
        index = self.account_choice.get_selected()
        if index >= len(self.accounts):
            return
        self.selected_id = self.accounts[index]['id']
        self._pending_request_id = None
        self.request_preview()
        self.render_usage()
        self.render_state()

    def request_preview(self):
        self.selection_generation += 1
        generation = self.selection_generation
        self.preview = None
        self.apply_button.set_sensitive(False)
        if self.selected_id is None or self.busy:
            return
        self._preview_revision = self.snapshot.get('configurationRevision')
        def complete(result, error):
            if generation != self.selection_generation:
                return False
            if error:
                self.status_text.set_text(str(error))
            else:
                self.preview = result
                self.render_devices()
                self.render_state()
            return False
        self.client.submit('preview', {'profileId': self.selected_id}, complete)

    def render_usage(self):
        selected = self.selected()
        self.selected_email.set_text(selected.get('email', ''))
        needs_login = selected.get('needsLogin') or selected.get('isPending')
        self.auth_status.set_text('로그인이 필요합니다.' if needs_login else ('인증 정상' if selected else '계정을 추가해 주세요.'))
        self.login_button.set_visible(bool(needs_login))
        usage = selected.get('usage') or {}
        settings = self.snapshot.get('settings') or {}
        session = usage.get('session') if settings.get('fiveHour', True) else None
        weekly = usage.get('weekly') if settings.get('codexWeekly', True) else None
        self.session_quota.update(session)
        self.weekly_quota.update(weekly)
        self.usage_empty.set_visible(not session and not weekly)
        updated = timestamp(usage.get('updatedAt'))
        text = f'마지막 확인 {updated:%m월 %d일 %H:%M}' if updated else '아직 조회된 사용량이 없습니다.'
        if usage.get('error'):
            text += ' · 갱신 실패: ' + usage['error']
        self.usage_updated.set_text(text)
        count = usage.get('resetCredits')
        self.credits.set_visible(count is not None)
        if count is not None:
            expirations = sorted(filter(None, (timestamp(x) for x in usage.get('resetCreditExpirations', []))))
            expiry = f' · 다음 만료 {expirations[0]:%m월 %d일 %H:%M}' if expirations else ''
            self.credits.set_text(f'초기화권  {count}개{expiry}')

    def device_columns(self, device, current, target, heading=False):
        row = box(False, 12)
        for text in [device, current, target]:
            cell = label(text, 'muted small' if heading else None, True)
            cell.set_hexpand(True)
            cell.set_size_request(110, -1)
            row.append(cell)
        row.set_homogeneous(True)
        return row

    def render_devices(self):
        if not hasattr(self, 'device_list'):
            return
        clear(self.device_list)
        targets = (self.preview or {}).get('targets')
        if targets is None:
            targets = self.snapshot.get('devices') or []
        included = [t for t in targets if t.get('included', t.get('enabled', True))]
        ssh_count = sum(t.get('kind') == 'ssh' for t in included)
        self.device_scope.set_text(f'이 Ubuntu PC와 SSH 장치 {ssh_count}대' if included else '등록된 장치가 없습니다.')
        self.scope.set_text(f'활성 장치 {len(included)}대에 적용합니다.' if included else '계정과 장치를 확인해 주세요.')
        self.table_header.set_visible(not self._narrow)
        operation = self.snapshot.get('operation') or {}
        results = {t.get('id'): t for t in operation.get('targets', [])} if self.busy or operation.get('state') == 'recoveryRequired' else {}
        states = {'pending':'대기', 'ready':'준비 완료', 'applying':'적용 중', 'applied':'검증 대기', 'verified':'검증 완료', 'restored':'복구됨', 'recoveryRequired':'복구 필요'}
        for device in targets:
            row = Gtk.ListBoxRow(activatable=True, selectable=False)
            layout = box(bool(self._narrow), 8)
            if not self._narrow:
                layout.set_homogeneous(True)
            identity = box(False, 9)
            identity.append(icon('computer-symbolic' if device.get('kind') == 'local' else 'network-server-symbolic', 22))
            name = box(spacing=3)
            name.append(label(device.get('displayName', '장치'), wrap=True))
            connection = '연결됨' if device.get('isReachable') else '확인 필요'
            if not device.get('enabled', True):
                connection = '대상 제외'
            elif device.get('status') == 'unreachable':
                connection = '연결 실패'
            kind = 'Ubuntu 26.04' if device.get('kind') == 'local' else 'SSH'
            name.append(label(f'{kind} · {connection}', 'muted small', True))
            identity.append(name)
            layout.append(identity)
            current = device.get('currentAccountLabel') or account_name(self.lookup(device.get('currentProfileId'))) if device.get('currentProfileId') is not None else device.get('currentAccountLabel') or '확인 필요'
            current_widget = box(spacing=4)
            current_widget.append(label(f'현재: {current}' if self._narrow else current, wrap=True))
            current_account = self.lookup(device.get('currentProfileId'))
            if current_account and current_account.get('email') != current:
                current_widget.append(label(current_account.get('email', ''), 'muted small', True))
            layout.append(current_widget)
            target = box(spacing=3)
            planned = device.get('targetAccountLabel') or (account_name(self.selected()) if self.selected() else '계정 선택')
            if not device.get('included', device.get('enabled', True)):
                planned = '대상 제외'
            target.append(label(('적용 후 · 예정: ' if self._narrow else '') + planned, 'target-account', True))
            if device.get('included', device.get('enabled', True)) and self.selected() and self.selected().get('email') != planned:
                target.append(label(self.selected().get('email', ''), 'muted small', True))
            detail = device.get('detail') or ''
            if device.get('action') == 'reapply':
                detail = '동일 계정 재적용'
            if device.get('id') in results:
                state = results[device['id']]
                detail = states.get(state.get('state'), state.get('state', ''))
                if state.get('detail'):
                    detail += ' · ' + state['detail']
            if detail:
                target.append(label(detail, 'muted small', True))
            layout.append(target)
            row.set_child(layout)
            row.connect('activate', lambda _row, d=device: self.open_dialog('cli' if d.get('kind') == 'local' else 'devices', d.get('id')))
            self.device_list.append(row)

    def render_state(self):
        active = self.lookup(self.snapshot.get('activeProfileId'))
        selected = self.selected()
        self.transition.set_text(f'{account_name(active) if active else "확인 필요"} → {account_name(selected)}' if selected else '계정을 추가해 주세요.')
        self.account_choice.set_sensitive(not self.busy)
        self.add_account_button.set_sensitive(not self.busy)
        operation = self.snapshot.get('operation') or {}
        waiting_login = self.busy and operation.get('kind') == 'login'
        self.login_actions.set_visible(waiting_login)
        self.login_button.set_sensitive(not self.busy)
        self.spinner.set_spinning(self.busy and not waiting_login)
        self.spinner.set_visible(self.busy and not waiting_login)
        recovery = operation.get('state') == 'recoveryRequired'
        self.recover_button.set_visible(recovery)
        message = operation.get('message') if self.busy or recovery or operation.get('state') == 'failed' else self.snapshot.get('error')
        if not message and self.preview:
            message = self.preview.get('blockingReason')
        self.status_text.set_text(message or '')
        self.status_text.set_visible(bool(message))
        busy_label = {'login':'로그인 대기 중', 'refresh':'새로고침 중…',
                      'recovery':'복구 중…', 'switch':'적용 중…'}.get(operation.get('kind'), '작업 중…')
        self.apply_button.set_label(busy_label if self.busy else '모든 장치에 적용')
        self.apply_button.set_sensitive(bool(self.preview and self.preview.get('canApply') and not self.busy and not recovery))

    def apply(self, *_):
        if not self.preview or self.busy:
            return
        self._pending_request_id = self._pending_request_id or uuid.uuid4().hex
        self.apply_button.set_sensitive(False)
        self.busy = True
        self.render_state()
        def complete(result, error):
            if error:
                self.busy = False
                self.toast(str(error))
                if getattr(error, 'code', '') in ('stale_preview', 'invalid_preview'):
                    self._pending_request_id = None
                    self.request_preview()
                self.render_state()
            self.poll()
            return False
        self.client.submit('apply', {'previewId': self.preview['previewId'], 'requestId': self._pending_request_id}, complete)

    def toast(self, text):
        self.toasts.add_toast(Adw.Toast(title=text, timeout=6))

    def command(self, method, params=None, after=None):
        def complete(result, error):
            self.toast(str(error) if error else '요청을 처리했습니다.')
            if after:
                after(result, error)
            self.poll()
            return False
        self.client.submit(method, params, complete, timeout=120)

    def refresh(self):
        self.command('refresh', {'kind':'all'}, lambda result, error: self.request_preview())

    def add_account(self):
        self.command('account.add', after=lambda result, error: self.open_dialog('accounts') if not error else None)

    def reopen_login(self):
        operation = self.snapshot.get('operation') or {}
        profile_id = operation.get('profileId')
        if profile_id is None:
            pending = [a['id'] for a in self.accounts if a.get('isPending')]
            profile_id = pending[-1] if pending else self.selected_id
        if profile_id is not None:
            self.command('account.reopen', {'profileId':profile_id})

    def login_selected(self, *_):
        if self.selected_id is not None:
            self.command('account.login', {'profileId':self.selected_id})

    def open_dialog(self, name, device_id=None):
        from .dialogs import ManagerDialog
        dialog = ManagerDialog(self, name, device_id)
        self._dialogs.append(dialog)
        dialog.present(self.window)

    def quit_service(self, *_):
        def complete(result, error):
            if error:
                self.toast(str(error))
            else:
                self.quit()
            return False
        self.client.submit('quit', callback=complete, timeout=120)

    def on_close(self, *_):
        # Closing the management process never terminates the background daemon.
        self.window = None
        self.client.close()
        return False

    def capture(self):
        if getattr(self.args, 'scroll_end', False) and not getattr(self, '_scrolled_for_capture', False):
            self._scrolled_for_capture = True
            adjustment = self.scroll.get_vadjustment()
            adjustment.set_value(adjustment.get_upper() - adjustment.get_page_size())
            GLib.timeout_add(300, self.capture)
            return False
        try:
            width, height = self.window.get_width(), self.window.get_height()
            paintable = Gtk.WidgetPaintable.new(self.window)
            snapshot = Gtk.Snapshot()
            paintable.snapshot(snapshot, float(width), float(height))
            node = snapshot.to_node()
            if node is None:
                raise RuntimeError('아직 렌더링된 위젯이 없습니다.')
            renderer = Gsk.CairoRenderer.new()
            renderer.realize_for_display(self.window.get_display())
            texture = renderer.render_texture(node, None)
            output = Path(self.args.capture)
            output.parent.mkdir(parents=True, exist_ok=True)
            texture.save_to_png(str(output))
            renderer.unrealize()
            output.with_suffix('.json').write_text(json.dumps({'width':width,'height':height,'mode':'isolated-demo','selectedProfileId':self.selected_id,'operation':self.snapshot.get('operation'),'source':'GTK WidgetPaintable','gtkScaleFactor':self.window.get_scale_factor(),'scrollValue':self.scroll.get_vadjustment().get_value(),'scrollUpper':self.scroll.get_vadjustment().get_upper(),'scrollPageSize':self.scroll.get_vadjustment().get_page_size()}, ensure_ascii=False, indent=2))
            print(f'GTK_CAPTURE={output}', flush=True)
            self.quit()
        except Exception as error:
            print(f'GTK_CAPTURE_ERROR={error}', flush=True)
            self.quit()
        return False
