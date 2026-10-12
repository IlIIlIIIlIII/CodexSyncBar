"""Native weekly comparison page; selecting a row only opens a preview."""
from gi.repository import Gtk, Pango
from .weekly_usage import weekly_balance


CSS = b'''
.page-navigation { margin: 12px 0 8px; }
.page-navigation button { padding: 8px 24px; }
.weekly-content { padding: 16px 30px 12px; font-size: 16px; }
.weekly-content .small { font-size: 13px; }
.weekly-title { font-size: 32px; font-weight: 700; }
.weekly-subtitle { margin-top: 10px; }
.weekly-heading { margin-bottom: 20px; }
.weekly-axis { margin-bottom: 4px; }
.weekly-axis-labels { padding-bottom: 8px; }
.weekly-list { background: transparent; }
.weekly-list > row { padding: 22px 0; border-bottom: 1px solid alpha(@window_fg_color, .12); }
.weekly-list > row:last-child { border-bottom: none; }
.weekly-list > row:hover { background: alpha(@window_fg_color, .035); }
.weekly-list > row:focus-visible { outline: 2px solid @accent_color; outline-offset: -2px; }
.weekly-identity { font-size: 17px; font-weight: 650; }
.weekly-avatar { border-radius: 99px; padding: 16px; background: alpha(@window_fg_color, .12); }
.weekly-avatar.active { background: alpha(@accent_bg_color, .22); color: @accent_color; }
.weekly-value { font-size: 30px; font-weight: 700; font-feature-settings: "tnum"; }
.weekly-healthy { color: @accent_color; }
.weekly-warning { color: @warning_color; }
.weekly-critical { color: @error_color; }
.weekly-meter trough, .weekly-meter progress { min-height: 12px; border-radius: 99px; }
.weekly-meter trough { background: alpha(@window_fg_color, .17); }
.weekly-meter progress { background: @accent_color; }
.weekly-meter.warning progress { background: @warning_color; }
.weekly-meter.critical progress { background: @error_color; }
.weekly-meter.muted progress { background: alpha(@window_fg_color, .35); }
.weekly-meter.missing trough { background: transparent; border: 1px dashed alpha(@window_fg_color, .3); min-height: 4px; }
.weekly-footer { padding: 20px 26px; border-top: 1px solid alpha(@window_fg_color, .12); }
.weekly-footer .small { font-size: 13px; }
.weekly-empty { padding: 54px 24px; }
.weekly-notice { margin-bottom: 12px; }
.weekly-notice-present .weekly-list > row { padding: 16px 0; }
.weekly-narrow .weekly-content { padding: 18px 24px; }
.weekly-narrow .weekly-title { font-size: 25px; }
.weekly-narrow .weekly-subtitle { font-size: 14px; }
.weekly-narrow .weekly-list > row { padding: 18px 0; }
'''


def label(text='', css=None, wrap=False):
    result = Gtk.Label(label=text, xalign=0, wrap=wrap)
    if css:
        for name in css.split():
            result.add_css_class(name)
    return result


def box(vertical=True, spacing=8):
    return Gtk.Box(orientation=Gtk.Orientation.VERTICAL if vertical else Gtk.Orientation.HORIZONTAL, spacing=spacing)


class WeeklyAccountRow(Gtk.ListBoxRow):
    def __init__(self, account_id, identity_group, value_group):
        super().__init__(activatable=True, selectable=False)
        self.account_id = account_id
        self.grid = Gtk.Grid(column_spacing=22, row_spacing=12)
        self.set_child(self.grid)
        self.identity = box(False, 16)
        self.identity.set_hexpand(False)
        self.identity.set_valign(Gtk.Align.CENTER)
        self.avatar = Gtk.Image.new_from_icon_name('avatar-default-symbolic')
        self.avatar.set_pixel_size(24)
        self.avatar.set_valign(Gtk.Align.CENTER)
        self.avatar.add_css_class('weekly-avatar')
        self.identity.append(self.avatar)
        names = box(spacing=5)
        names.set_hexpand(True)
        names.set_valign(Gtk.Align.CENTER)
        self.name = label(css='weekly-identity', wrap=True)
        self.email = label(css='muted small', wrap=True)
        for widget in (self.name, self.email):
            widget.set_max_width_chars(24)
            widget.set_wrap_mode(Pango.WrapMode.WORD_CHAR)
            names.append(widget)
        self.identity.append(names)
        self.balance = box(spacing=12)
        self.balance.set_hexpand(True)
        self.balance.set_valign(Gtk.Align.CENTER)
        self.meter = Gtk.ProgressBar()
        self.meter.add_css_class('weekly-meter')
        self.balance.append(self.meter)
        self.reset = label(css='muted small', wrap=True)
        self.balance.append(self.reset)
        self.amount = box(spacing=5)
        self.amount.set_valign(Gtk.Align.CENTER)
        self.value = label(css='weekly-value')
        self.value.set_xalign(1)
        self.amount.append(self.value)
        self.status = label(css='small', wrap=True)
        self.status.set_xalign(1)
        self.status.set_max_width_chars(16)
        self.amount.append(self.status)
        self.active = label('현재 사용 중', 'small weekly-healthy')
        self.active.set_xalign(1)
        self.amount.append(self.active)
        identity_group.add_widget(self.identity)
        value_group.add_widget(self.amount)
        self._narrow = None
        self.reflow(False)

    def reflow(self, narrow):
        if narrow == self._narrow:
            return
        self._narrow = narrow
        for widget in (self.identity, self.balance, self.amount):
            if widget.get_parent():
                self.grid.remove(widget)
        self.identity.set_size_request(-1 if narrow else 248, -1)
        self.amount.set_size_request(110, -1)
        if narrow:
            self.grid.attach(self.identity, 0, 0, 2, 1)
            self.grid.attach(self.balance, 0, 1, 1, 1)
            self.grid.attach(self.amount, 1, 1, 1, 1)
        else:
            self.grid.attach(self.identity, 0, 0, 1, 1)
            self.grid.attach(self.balance, 1, 0, 1, 1)
            self.grid.attach(self.amount, 2, 0, 1, 1)

    def update(self, account, active, **state):
        balance = weekly_balance(account, **state)
        self.name.set_text(account.get('alias') or account.get('email') or '계정')
        self.email.set_text(account.get('email') or '')
        self.email.set_visible(self.email.get_text() != self.name.get_text())
        self.active.set_visible(active)
        self.active.set_text('마지막 적용 계정' if state.get('unavailable') else '현재 사용 중')
        (self.avatar.add_css_class if active else self.avatar.remove_css_class)('active')
        self.value.set_text(balance.value)
        self.status.set_text(balance.status)
        self.status.set_visible(bool(balance.status) and balance.remaining is not None)
        self.reset.set_text(balance.reset)
        self.meter.set_fraction((balance.remaining or 0) / 100)
        for tone in ('healthy', 'warning', 'critical', 'muted'):
            self.meter.remove_css_class(tone)
            self.value.remove_css_class('weekly-' + tone if tone != 'muted' else tone)
            self.status.remove_css_class('weekly-' + tone if tone != 'muted' else tone)
        self.meter.add_css_class(balance.tone)
        css = 'weekly-' + balance.tone if balance.tone != 'muted' else balance.tone
        self.value.add_css_class(css)
        self.status.add_css_class(css)
        (self.meter.add_css_class if balance.remaining is None else self.meter.remove_css_class)('missing')
        quota = '주간 정보 없음' if balance.remaining is None else f'주간 {balance.value} 남음'
        spoken = f'{self.name.get_text()}, {self.email.get_text()}, {quota}, {balance.reset}, {balance.status}'
        if active:
            spoken += ', ' + self.active.get_text()
        self.update_property([Gtk.AccessibleProperty.LABEL], [spoken])
        self.set_tooltip_text('이 계정의 전환 화면 열기 · 선택만으로 적용되지 않습니다.')
        return balance


class WeeklyOverview(Gtk.Box):
    def __init__(self, on_account, on_refresh, on_switch, on_add):
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.rows = {}
        self.narrow = None
        self.identity_group = Gtk.SizeGroup(mode=Gtk.SizeGroupMode.HORIZONTAL)
        self.value_group = Gtk.SizeGroup(mode=Gtk.SizeGroupMode.HORIZONTAL)
        self.scroll = Gtk.ScrolledWindow(vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER)
        self.append(self.scroll)
        content = box(spacing=0)
        content.add_css_class('weekly-content')
        self.scroll.set_child(content)
        self.heading = box(False, 20)
        self.heading.add_css_class('weekly-heading')
        titles = box(spacing=0)
        titles.set_hexpand(True)
        titles.append(label('주간 잔여량 비교', 'weekly-title', True))
        titles.append(label('계정마다 이번 주 얼마나 남았는지 확인하세요.', 'muted weekly-subtitle', True))
        self.heading.append(titles)
        self.refresh = Gtk.Button()
        refresh_content = box(False, 8)
        refresh_content.append(Gtk.Image.new_from_icon_name('view-refresh-symbolic'))
        self.refresh_text = label('새로고침')
        refresh_content.append(self.refresh_text)
        self.refresh.set_child(refresh_content)
        self.refresh.set_valign(Gtk.Align.START)
        self.refresh.connect('clicked', lambda *_: on_refresh())
        self.heading.append(self.refresh)
        content.append(self.heading)
        self.notice = label(css='small error-text weekly-notice', wrap=True)
        self.notice.set_visible(False)
        content.append(self.notice)
        self.axis = box(False, 22)
        self.axis.add_css_class('weekly-axis')
        self.axis_identity = box()
        self.axis_identity.set_size_request(248, -1)
        self.identity_group.add_widget(self.axis_identity)
        self.axis.append(self.axis_identity)
        scale = box(spacing=0)
        scale.set_hexpand(True)
        marks = box(False, 0)
        marks.add_css_class('weekly-axis-labels')
        for text, alignment in [('0%', 0), ('50%', .5), ('100%', 1)]:
            mark = label(text, 'small muted')
            mark.set_xalign(alignment)
            mark.set_hexpand(True)
            marks.append(mark)
        scale.append(marks)
        scale.append(Gtk.Separator())
        self.axis.append(scale)
        self.axis_value = box()
        self.axis_value.set_size_request(110, -1)
        self.value_group.add_widget(self.axis_value)
        self.axis.append(self.axis_value)
        content.append(self.axis)
        self.list = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE)
        self.list.add_css_class('weekly-list')
        self.list.connect('row-activated', lambda _list, row: on_account(row.account_id))
        content.append(self.list)
        self.empty = box(spacing=16)
        self.empty.add_css_class('weekly-empty')
        self.empty_text = label('계정을 확인하고 있습니다.', 'muted', True)
        self.empty.append(self.empty_text)
        self.add = Gtk.Button(label='계정 추가', halign=Gtk.Align.START)
        self.add.set_visible(False)
        self.add.connect('clicked', lambda *_: on_add())
        self.empty.append(self.add)
        content.append(self.empty)
        self.footer = box(False, 12)
        self.footer.add_css_class('weekly-footer')
        self.updated = label('사용량을 확인하고 있습니다.', 'muted small', True)
        self.updated.set_hexpand(True)
        self.footer.append(self.updated)
        self.switch = Gtk.Button(label='계정 전환으로 이동')
        self.switch.add_css_class('flat')
        self.switch.add_css_class('weekly-healthy')
        self.switch.connect('clicked', lambda *_: on_switch())
        self.footer.append(self.switch)
        self.append(self.footer)

    def reflow(self, width):
        narrow = width < 780
        if narrow == self.narrow:
            return
        self.narrow = narrow
        (self.add_css_class if self.narrow else self.remove_css_class)('weekly-narrow')
        self.axis_identity.set_visible(not self.narrow)
        self.refresh_text.set_visible(not self.narrow)
        self.refresh.set_tooltip_text('사용량 새로고침 (Ctrl+R)')
        self.refresh.update_property([Gtk.AccessibleProperty.LABEL], ['사용량 새로고침'])
        self.switch.set_label('계정 전환' if self.narrow else '계정 전환으로 이동')
        self.updated.set_max_width_chars(28 if self.narrow else 70)
        self.identity_group.set_mode(Gtk.SizeGroupMode.NONE if self.narrow else Gtk.SizeGroupMode.HORIZONTAL)
        for row in self.rows.values():
            row.reflow(self.narrow)

    def update(self, snapshot, *, error=None, pending=False, now=None):
        accounts = snapshot.get('accounts') or []
        busy = snapshot.get('isBusy', False) or pending
        refreshing = pending or (busy and (snapshot.get('operation') or {}).get('kind') == 'refresh')
        self.refresh.set_sensitive(not busy)
        self.refresh_text.set_text('갱신 중…' if refreshing else '새로고침')
        self.add.set_sensitive(not busy and not error)
        self.notice.set_text(error or snapshot.get('error') or '')
        self.notice.set_visible(bool(self.notice.get_text()))
        (self.add_css_class if self.notice.get_visible() else self.remove_css_class)('weekly-notice-present')
        ids = [account['id'] for account in accounts]
        if ids != list(self.rows):
            for row in self.rows.values():
                self.identity_group.remove_widget(row.identity)
                self.value_group.remove_widget(row.amount)
                self.list.remove(row)
            self.rows = {}
            for identity in ids:
                row = WeeklyAccountRow(identity, self.identity_group, self.value_group)
                row.reflow(self.narrow)
                self.rows[identity] = row
                self.list.append(row)
        balances = [self.rows[a['id']].update(a, a['id'] == snapshot.get('activeProfileId'),
                    unavailable=bool(error), refreshing=refreshing, now=now) for a in accounts]
        for row in self.rows.values():
            # Disable activation, not the readable cached data in the row.
            row.set_activatable(not busy and not error)
        self.axis.set_visible(bool(accounts))
        self.empty.set_visible(not accounts)
        self.empty_text.set_text('서비스에 연결하지 못했습니다. 새로고침해 주세요.' if error else
                                '계정을 추가하면 주간 잔여량을 비교할 수 있습니다.')
        self.add.set_visible(not error)
        # Use the oldest account timestamp, never the snapshot's poll timestamp.
        times = [b.updated for b in balances if b.updated]
        suffix = f'{len(accounts)}개 계정'
        if times:
            oldest = min(times)
            incomplete = ' · 일부 미조회' if len(times) < len(accounts) else ''
            self.updated.set_text(f'마지막 조회 {oldest.month}월 {oldest.day}일 {oldest:%H:%M} 기준 · {suffix}{incomplete}')
        else:
            self.updated.set_text(f'아직 조회된 사용량이 없습니다. · {suffix}')
