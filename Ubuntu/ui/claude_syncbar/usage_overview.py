"""Native per-account usage page; selecting a row only opens a preview."""
from gi.repository import Gtk, Pango
from .quota import TITLES, account_usage, visible_quotas


CSS = b'''
.page-navigation { margin: 12px 0 8px; }
.page-navigation button { padding: 8px 24px; }
.usage-content { padding: 16px 30px 12px; font-size: 16px; }
.usage-content .small { font-size: 13px; }
.usage-title { font-size: 32px; font-weight: 700; }
.usage-subtitle { margin-top: 10px; }
.usage-heading { margin-bottom: 18px; }
.usage-columns { padding: 0 0 10px; border-bottom: 1px solid alpha(@window_fg_color, .12); }
.usage-column-title { font-size: 13px; font-weight: 700; letter-spacing: .02em; }
.usage-list { background: transparent; }
.usage-list > row { padding: 22px 0; border-bottom: 1px solid alpha(@window_fg_color, .12); }
.usage-list > row:last-child { border-bottom: none; }
.usage-list > row:hover { background: alpha(@window_fg_color, .035); }
.usage-list > row:focus-visible { outline: 2px solid @accent_color; outline-offset: -2px; }
.usage-identity-name { font-size: 17px; font-weight: 650; }
.usage-avatar { border-radius: 99px; padding: 16px; background: alpha(@window_fg_color, .12); }
.usage-avatar.active { background: alpha(@accent_bg_color, .22); color: @accent_color; }
.usage-chip { font-size: 12px; font-weight: 650; padding: 2px 10px; border-radius: 99px; }
.usage-chip.active { color: @accent_color; background: alpha(@accent_bg_color, .16); }
.usage-chip.warning { color: @warning_color; background: alpha(@warning_color, .14); }
.usage-chip.muted { background: alpha(@window_fg_color, .08); }
.usage-cell { padding: 2px 0; }
.usage-cell-title { font-size: 13px; font-weight: 700; }
.usage-value { font-size: 28px; font-weight: 700; font-feature-settings: "tnum"; }
.usage-unit { font-size: 13px; margin-bottom: 5px; }
/* Quota colors say how much is left and never follow the accent color, which can itself be orange or red. */
.usage-healthy { color: @success_color; }
.usage-link { color: @accent_color; }
.usage-warning { color: @warning_color; }
.usage-critical { color: @error_color; }
.usage-meter trough, .usage-meter progress { min-height: 10px; border-radius: 99px; }
.usage-meter trough { background: alpha(@window_fg_color, .15); }
.usage-meter progress { background: @success_color; }
.usage-meter.warning progress { background: @warning_color; }
.usage-meter.critical progress { background: @error_color; }
.usage-meter.muted progress { background: alpha(@window_fg_color, .35); }
.usage-meter.missing trough { background: transparent; border: 1px dashed alpha(@window_fg_color, .3); min-height: 4px; }
.usage-footer { padding: 20px 26px; border-top: 1px solid alpha(@window_fg_color, .12); }
.usage-footer .small { font-size: 13px; }
.usage-empty { padding: 54px 24px; }
.usage-notice { margin-bottom: 12px; }
.usage-notice-present .usage-list > row { padding: 16px 0; }
.usage-narrow .usage-content { padding: 18px 24px; }
.usage-narrow .usage-title { font-size: 25px; }
.usage-narrow .usage-subtitle { font-size: 14px; }
.usage-narrow .usage-list > row { padding: 18px 0; }
.usage-narrow .usage-value { font-size: 24px; }
'''

TONES = ('healthy', 'warning', 'critical', 'muted')
IDENTITY_WIDTH = 260


def label(text='', css=None, wrap=False):
    result = Gtk.Label(label=text, xalign=0, wrap=wrap)
    if css:
        for name in css.split():
            result.add_css_class(name)
    return result


def box(vertical=True, spacing=8):
    return Gtk.Box(orientation=Gtk.Orientation.VERTICAL if vertical else Gtk.Orientation.HORIZONTAL, spacing=spacing)


def retone(widget, tone, prefix=''):
    for name in TONES:
        widget.remove_css_class(prefix + name if name != 'muted' else name)
    widget.add_css_class(prefix + tone if tone != 'muted' else tone)


class QuotaCell(Gtk.Box):
    """One quota of one account: how much is left, as a number and a bar, and when it resets."""

    def __init__(self, key):
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=8, hexpand=True)
        self.key = key
        self.add_css_class('usage-cell')
        self.title = label(TITLES[key], 'usage-cell-title muted')
        self.append(self.title)
        amount = box(False, 6)
        self.value = label(css='usage-value')
        self.value.set_valign(Gtk.Align.BASELINE_FILL)
        amount.append(self.value)
        self.unit = label('남음', 'usage-unit muted')
        self.unit.set_valign(Gtk.Align.END)
        amount.append(self.unit)
        self.append(amount)
        self.meter = Gtk.ProgressBar()
        self.meter.add_css_class('usage-meter')
        self.append(self.meter)
        self.caption = label(css='muted small', wrap=True)
        self.caption.set_wrap_mode(Pango.WrapMode.WORD_CHAR)
        self.append(self.caption)

    def update(self, quota):
        self.value.set_text(quota.value)
        self.unit.set_visible(quota.remaining is not None)
        self.meter.set_fraction((quota.remaining or 0) / 100)
        (self.meter.add_css_class if quota.remaining is None else self.meter.remove_css_class)('missing')
        retone(self.meter, quota.tone)
        retone(self.value, quota.tone, 'usage-')
        self.caption.set_text(quota.caption)


class UsageAccountRow(Gtk.ListBoxRow):
    def __init__(self, account_id, identity_group):
        super().__init__(activatable=True, selectable=False)
        self.account_id = account_id
        self.grid = Gtk.Grid(column_spacing=32, row_spacing=16)
        self.set_child(self.grid)
        # Equal widths, so the quotas of every account line up in columns.
        self.quotas = Gtk.Box(spacing=32, homogeneous=True, hexpand=True)
        self.identity = box(False, 16)
        # The account column keeps its width; the quotas take the rest.
        self.identity.set_hexpand(False)
        self.identity.set_valign(Gtk.Align.CENTER)
        self.avatar = Gtk.Image.new_from_icon_name('avatar-default-symbolic')
        self.avatar.set_pixel_size(24)
        self.avatar.set_valign(Gtk.Align.CENTER)
        self.avatar.add_css_class('usage-avatar')
        self.identity.append(self.avatar)
        names = box(spacing=5)
        names.set_hexpand(True)
        names.set_valign(Gtk.Align.CENTER)
        self.name = label(css='usage-identity-name', wrap=True)
        self.email = label(css='muted small', wrap=True)
        for widget in (self.name, self.email):
            widget.set_max_width_chars(24)
            widget.set_wrap_mode(Pango.WrapMode.WORD_CHAR)
            names.append(widget)
        chips = box(False, 6)
        chips.set_margin_top(4)
        self.active = label('현재 사용 중', 'usage-chip active')
        self.status = label(css='usage-chip', wrap=True)
        self.status.set_max_width_chars(22)
        for chip in (self.active, self.status):
            chip.set_halign(Gtk.Align.START)
            chips.append(chip)
        names.append(chips)
        self.chips = chips
        self.identity.append(names)
        identity_group.add_widget(self.identity)
        self.cells = {}
        self._layout = None

    def set_quotas(self, keys):
        if tuple(self.cells) == tuple(keys):
            return
        for cell in self.cells.values():
            self.quotas.remove(cell)
        self.cells = {key: QuotaCell(key) for key in keys}
        for cell in self.cells.values():
            self.quotas.append(cell)
        self._layout = None

    def reflow(self, layout):
        """'wide' puts quotas beside the account, 'stacked' below it side by side, 'narrow' one under another."""
        if layout == self._layout:
            return
        self._layout = layout
        for widget in (self.identity, self.quotas):
            if widget.get_parent():
                self.grid.remove(widget)
        self.identity.set_size_request(IDENTITY_WIDTH if layout == 'wide' else -1, -1)
        self.identity.set_hexpand(layout != 'wide')
        self.grid.attach(self.identity, 0, 0, 1, 1)
        self.grid.attach(self.quotas, 1 if layout == 'wide' else 0, 0 if layout == 'wide' else 1, 1, 1)
        self.quotas.set_orientation(Gtk.Orientation.VERTICAL if layout == 'narrow' else Gtk.Orientation.HORIZONTAL)
        self.quotas.set_spacing(18 if layout == 'narrow' else 32)
        # The column header names the quotas when they sit in columns beside the account.
        for cell in self.cells.values():
            cell.title.set_visible(layout != 'wide')

    def update(self, account, active, keys, **state):
        usage = account_usage(account, keys=keys, **state)
        self.name.set_text(account.get('alias') or account.get('email') or '계정')
        email = account.get('email') or ''
        # The plan decides which accounts the smart switch may choose.
        self.email.set_text(' · '.join(part for part in [email if email != self.name.get_text() else '', account.get('plan') or ''] if part))
        self.email.set_visible(bool(self.email.get_text()))
        self.active.set_visible(active)
        self.active.set_text('마지막 적용 계정' if state.get('unavailable') else '현재 사용 중')
        (self.avatar.add_css_class if active else self.avatar.remove_css_class)('active')
        self.status.set_text(usage.status)
        self.status.set_visible(bool(usage.status))
        retone(self.status, usage.status_tone)
        self.chips.set_visible(active or bool(usage.status))
        for quota in usage.quotas:
            self.cells[quota.key].update(quota)
        parts = [self.name.get_text(), self.email.get_text(), *(quota.spoken for quota in usage.quotas)]
        if usage.status:
            parts.append(usage.status)
        if active:
            parts.append(self.active.get_text())
        self.update_property([Gtk.AccessibleProperty.LABEL], [', '.join(part for part in parts if part)])
        self.set_tooltip_text('이 계정의 전환 화면 열기 · 선택만으로 적용되지 않습니다.')
        return usage


class UsageOverview(Gtk.Box):
    def __init__(self, on_account, on_refresh, on_switch, on_add, on_smart=None):
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.rows = {}
        self.layout = None
        self.keys = visible_quotas(None)
        self.identity_group = Gtk.SizeGroup(mode=Gtk.SizeGroupMode.HORIZONTAL)
        self.scroll = Gtk.ScrolledWindow(vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER)
        self.append(self.scroll)
        content = box(spacing=0)
        content.add_css_class('usage-content')
        self.scroll.set_child(content)
        self.heading = box(False, 20)
        self.heading.add_css_class('usage-heading')
        titles = box(spacing=0)
        titles.set_hexpand(True)
        titles.append(label('계정별 사용량', 'usage-title', True))
        self.subtitle = label('', 'muted usage-subtitle', True)
        titles.append(self.subtitle)
        self.heading.append(titles)
        self.refresh = Gtk.Button()
        refresh_content = box(False, 8)
        refresh_content.append(Gtk.Image.new_from_icon_name('view-refresh-symbolic'))
        self.refresh_text = label('새로고침')
        refresh_content.append(self.refresh_text)
        self.refresh.set_child(refresh_content)
        self.refresh.set_valign(Gtk.Align.START)
        self.refresh.set_tooltip_text('사용량 새로고침 (Ctrl+R)')
        self.refresh.update_property([Gtk.AccessibleProperty.LABEL], ['사용량 새로고침'])
        self.refresh.connect('clicked', lambda *_: on_refresh())
        actions = box(False, 8)
        actions.set_valign(Gtk.Align.START)
        # One click to the best $200 Max account: most weekly quota left among those with five-hour room.
        self.smart = Gtk.Button()
        smart_content = box(False, 8)
        smart_content.append(Gtk.Image.new_from_icon_name('emblem-synchronizing-symbolic'))
        self.smart_text = label('스마트 전환')
        smart_content.append(self.smart_text)
        self.smart.set_child(smart_content)
        self.smart.add_css_class('suggested-action')
        self.smart.set_tooltip_text('Max 20x 계정 중 주간 한도가 남고 5시간 한도를 75% 미만 쓴 계정에서 주간 초기화가 가장 가까운 계정으로 모든 활성 장치를 전환합니다.')
        self.smart.update_property([Gtk.AccessibleProperty.LABEL], ['스마트 전환'])
        self.smart.connect('clicked', lambda *_: on_smart and on_smart())
        self.smart.set_visible(on_smart is not None)
        actions.append(self.smart)
        actions.append(self.refresh)
        self.heading.append(actions)
        content.append(self.heading)
        self.notice = label(css='small error-text usage-notice', wrap=True)
        self.notice.set_visible(False)
        content.append(self.notice)
        self.columns = box(False, 32)
        self.columns.add_css_class('usage-columns')
        self.columns_identity = label('계정', 'usage-column-title muted')
        self.identity_group.add_widget(self.columns_identity)
        self.columns.append(self.columns_identity)
        self.column_boxes = Gtk.Box(spacing=32, homogeneous=True, hexpand=True)
        self.columns.append(self.column_boxes)
        self.column_titles = {}
        content.append(self.columns)
        self.list = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE)
        self.list.add_css_class('usage-list')
        self.list.connect('row-activated', lambda _list, row: on_account(row.account_id))
        content.append(self.list)
        self.empty = box(spacing=16)
        self.empty.add_css_class('usage-empty')
        self.empty_text = label('계정을 확인하고 있습니다.', 'muted', True)
        self.empty.append(self.empty_text)
        self.add = Gtk.Button(label='계정 추가', halign=Gtk.Align.START)
        self.add.set_visible(False)
        self.add.connect('clicked', lambda *_: on_add())
        self.empty.append(self.add)
        content.append(self.empty)
        self.footer = box(False, 12)
        self.footer.add_css_class('usage-footer')
        self.updated = label('사용량을 확인하고 있습니다.', 'muted small', True)
        self.updated.set_hexpand(True)
        self.footer.append(self.updated)
        self.switch = Gtk.Button(label='계정 전환으로 이동')
        self.switch.add_css_class('flat')
        self.switch.add_css_class('usage-link')
        self.switch.connect('clicked', lambda *_: on_switch())
        self.footer.append(self.switch)
        self.append(self.footer)
        self._set_keys(self.keys)
        self._width = 1040

    def _set_keys(self, keys):
        self.keys = keys
        for child in list(self.column_titles.values()):
            self.column_boxes.remove(child)
        self.column_titles = {}
        for key in keys:
            title = label(f'{TITLES[key]} 한도', 'usage-column-title muted')
            self.column_boxes.append(title)
            self.column_titles[key] = title
        if not keys:
            self.subtitle.set_text('설정에서 표시할 사용량을 켜 주세요.')
        elif len(keys) == 2:
            self.subtitle.set_text('계정마다 5시간 한도와 주간 한도가 얼마나 남았는지 비교하세요.')
        else:
            self.subtitle.set_text(f'계정마다 {TITLES[keys[0]]} 한도가 얼마나 남았는지 비교하세요.')
        for row in self.rows.values():
            row.set_quotas(keys)
            row.reflow(self.layout)

    def reflow(self, width):
        self._width = width
        layout = 'wide' if width >= 820 else 'stacked' if width >= 540 else 'narrow'
        if layout == self.layout:
            return
        self.layout = layout
        narrow = layout != 'wide'
        (self.add_css_class if narrow else self.remove_css_class)('usage-narrow')
        self.refresh_text.set_visible(layout != 'narrow')
        self.smart_text.set_visible(layout != 'narrow')
        self.switch.set_label('계정 전환' if narrow else '계정 전환으로 이동')
        self.updated.set_max_width_chars(28 if narrow else 70)
        self.identity_group.set_mode(Gtk.SizeGroupMode.HORIZONTAL if layout == 'wide' else Gtk.SizeGroupMode.NONE)
        self.columns.set_visible(layout == 'wide' and bool(self.rows))
        for row in self.rows.values():
            row.reflow(layout)

    def update(self, snapshot, *, error=None, pending=False, now=None):
        accounts = snapshot.get('accounts') or []
        keys = visible_quotas(snapshot.get('settings'))
        if keys != self.keys:
            self._set_keys(keys)
        busy = snapshot.get('isBusy', False) or pending
        refreshing = pending or (busy and (snapshot.get('operation') or {}).get('kind') == 'refresh')
        self.refresh.set_sensitive(not busy)
        self.smart.set_sensitive(not busy and not error and len(accounts) > 1)
        self.refresh_text.set_text('갱신 중…' if refreshing else '새로고침')
        self.add.set_sensitive(not busy and not error)
        self.notice.set_text(error or snapshot.get('error') or '')
        self.notice.set_visible(bool(self.notice.get_text()))
        (self.add_css_class if self.notice.get_visible() else self.remove_css_class)('usage-notice-present')
        ids = [account['id'] for account in accounts]
        if ids != list(self.rows):
            for row in self.rows.values():
                self.identity_group.remove_widget(row.identity)
                self.list.remove(row)
            self.rows = {}
            for identity in ids:
                row = UsageAccountRow(identity, self.identity_group)
                row.set_quotas(self.keys)
                row.reflow(self.layout)
                self.rows[identity] = row
                self.list.append(row)
        results = [self.rows[a['id']].update(a, a['id'] == snapshot.get('activeProfileId'), self.keys,
                   unavailable=bool(error), refreshing=refreshing, now=now) for a in accounts]
        for row in self.rows.values():
            # Disable activation, not the readable cached data in the row.
            row.set_activatable(not busy and not error)
        self.columns.set_visible(self.layout == 'wide' and bool(accounts))
        self.empty.set_visible(not accounts)
        self.empty_text.set_text('서비스에 연결하지 못했습니다. 새로고침해 주세요.' if error else
                                '계정을 추가하면 계정별 사용량을 비교할 수 있습니다.')
        self.add.set_visible(not error)
        # Use the oldest account timestamp, never the snapshot's poll timestamp.
        times = [result.updated for result in results if result.updated]
        suffix = f'{len(accounts)}개 계정'
        if (snapshot.get('settings') or {}).get('autoSwitchEnabled'):
            suffix += ' · 자동 전환 켜짐'
        if times:
            oldest = min(times)
            incomplete = ' · 일부 미조회' if len(times) < len(accounts) else ''
            self.updated.set_text(f'마지막 조회 {oldest.month}월 {oldest.day}일 {oldest:%H:%M} 기준 · {suffix}{incomplete}')
        else:
            self.updated.set_text(f'아직 조회된 사용량이 없습니다. · {suffix}')
