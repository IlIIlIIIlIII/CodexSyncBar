"""The token page of the management dialog: libadwaita rows over token_view's wording and decisions."""
import gi
gi.require_version('Gtk', '4.0')
gi.require_version('Adw', '1')
gi.require_version('Gsk', '4.0')
gi.require_version('Graphene', '1.0')
from gi.repository import Adw, Gdk, GLib, Graphene, Gsk, Gtk, Pango

from . import token_view as tv

# Token figures and rates stay neutral: colours here speak only of what is left, and the accent marks the account in use.
CSS = b'''
.token-card { padding: 14px 16px; }
.token-figure-large { font-size: 28px; font-weight: 700; font-feature-settings: "tnum"; }
.token-figure { font-size: 18px; font-weight: 600; font-feature-settings: "tnum"; }
.token-unit { font-size: 13px; margin-bottom: 4px; }
.token-rate { font-size: 17px; font-weight: 700; font-feature-settings: "tnum"; }
.token-rate.rough { opacity: .7; }
.token-amount { font-feature-settings: "tnum"; }
.token-bar { color: @window_fg_color; }
.token-swatch { min-width: 10px; min-height: 10px; border-radius: 2px; }
.token-swatch.major { background: alpha(@window_fg_color, .55); }
.token-swatch.minor { background: alpha(@window_fg_color, .25); }
.token-swatch.none { border: 1px solid alpha(@window_fg_color, .35); }
.token-avatar { border-radius: 99px; padding: 8px; background: alpha(@window_fg_color, .10); }
.token-avatar.active { background: alpha(@accent_bg_color, .22); color: @accent_color; }
'''

ALPHA = {'major': .55, 'minor': .25, 'track': .12}
# A spin button reports every step; the period is cut once it settles.
SETTLE_MS = 400


def label(text='', css=None, wrap=False):
    widget = Gtk.Label(label=text, xalign=0, wrap=wrap)
    if wrap:
        widget.set_wrap_mode(Pango.WrapMode.WORD_CHAR)
    for name in (css or '').split():
        widget.add_css_class(name)
    return widget


def box(vertical=True, spacing=8):
    return Gtk.Box(orientation=Gtk.Orientation.VERTICAL if vertical else Gtk.Orientation.HORIZONTAL, spacing=spacing)


def clear(widget):
    child = widget.get_first_child()
    while child:
        following = child.get_next_sibling()
        widget.remove(child)
        child = following


def card(child):
    """A boxed-list row holding any content, so figures sit in the same list as the rows around them."""
    row = Adw.PreferencesRow(activatable=False, selectable=False)
    child.add_css_class('token-card')
    row.set_child(child)
    return row


class ShareBar(Gtk.Widget):
    """Parts of a whole side by side, or one part against a track; drawn in the text colour at low opacity."""
    __gtype_name__ = 'ClaudeSyncBarShareBar'

    def __init__(self, width=0, height=6):
        super().__init__(accessible_role=Gtk.AccessibleRole.IMG)
        self.width, self.height = width, height
        self.parts, self.track = (), False
        self.add_css_class('token-bar')
        self.set_valign(Gtk.Align.CENTER)
        self.set_hexpand(width == 0)

    def show_parts(self, parts, spoken, track=False):
        self.parts, self.track = tuple(parts), track
        self.update_property([Gtk.AccessibleProperty.LABEL], [spoken])
        self.queue_draw()

    def do_measure(self, orientation, for_size):
        size = self.height if orientation == Gtk.Orientation.VERTICAL else max(24, self.width)
        return size, size, -1, -1

    def do_snapshot(self, snapshot):
        width, height = self.get_width(), self.get_height()
        color = self.get_color()
        for x, size, tone in tv.bar_segments(self.parts, width, track=self.track):
            if size <= 0:
                continue
            bounds = Graphene.Rect().init(x, 0, size, height)
            clip = Gsk.RoundedRect()
            clip.init_from_rect(bounds, min(3, height / 2))
            paint = Gdk.RGBA()
            paint.red, paint.green, paint.blue = color.red, color.green, color.blue
            paint.alpha = color.alpha * ALPHA.get(tone, ALPHA['track'])
            snapshot.push_rounded_clip(clip)
            snapshot.append_color(paint, bounds)
            snapshot.pop()


class TokenPage:
    """Builds the page once in the dialog's body and refills it whenever a reading or another cut of it arrives.

    The period and basis rows stay in place while the figures around them are replaced, and every request is
    numbered so that a reply arriving after a newer choice is dropped.
    """

    def __init__(self, dialog):
        self.dialog, self.app = dialog, dialog.app
        self.session = tv.TokenSession(getattr(self.app, 'tokens_last', None), (self.app.snapshot or {}).get('settings'))
        self.open_devices = {'local'}
        self.open_accounts = set()
        self.opened_current = False
        self.syncing = False
        self.pending = None
        self.cells = None
        body = dialog.body

        top = box(False, 12)
        intro = label(tv.INTRO, 'muted', True)
        intro.set_hexpand(True)
        top.append(intro)
        self.again = Gtk.Button(label=tv.AGAIN, valign=Gtk.Align.CENTER)
        self.again.connect('clicked', lambda *_: self.load())
        top.append(self.again)
        body.append(top)

        self.warning = box(spacing=0)
        body.append(self.warning)

        period = Adw.PreferencesGroup()
        self.period = Adw.ComboRow(title=tv.PERIOD_TITLE, model=Gtk.StringList.new(list(tv.PERIOD_LABELS)))
        self.period.connect('notify::selected', self.period_changed)
        period.add(self.period)
        self.hours = Adw.SpinRow.new_with_range(1, tv.MAXIMUM_HOURS, 1)
        self.hours.set_title(tv.HOURS_TITLE)
        self.hours.set_subtitle(tv.HOURS_SUBTITLE)
        self.hours.set_digits(0)
        self.hours.set_numeric(True)
        self.hours.connect('notify::value', self.hours_changed)
        period.add(self.hours)
        body.append(period)

        self.upper = box(spacing=18)
        body.append(self.upper)

        self.burn = Adw.PreferencesGroup(title=tv.BURN_TITLE, description=tv.BURN_DESCRIPTION)
        self.basis = Adw.ComboRow(title=tv.BASIS_TITLE, model=Gtk.StringList.new([item.label for item in tv.BASES]))
        # The list says what each basis counts; the row's subtitle says it for the one chosen.
        choices = Gtk.SignalListItemFactory()
        choices.connect('setup', self.basis_setup)
        choices.connect('bind', self.basis_bind)
        self.basis.set_list_factory(choices)
        self.basis.connect('notify::selected', self.basis_changed)
        self.burn.add(self.basis)
        self.burn_rows = []
        self.notes = box(spacing=4)
        self.notes.set_margin_top(10)
        self.burn.add(self.notes)
        body.append(self.burn)

        self.lower = box(spacing=12)
        body.append(self.lower)
        self.show()
        self.load()

    # ---------- requests ----------

    def load(self):
        """Reads every device's logs again, keeping the period and basis in view."""
        if self.session.loading:
            return
        # The reading carries the period and basis chosen, an hours change still settling included.
        self.cancel_pending()
        ticket, params = self.session.start_load()
        self.show()

        def done(result, error):
            outcome = self.session.finish_load(ticket, result, error)
            if outcome == 'shown':
                self.app.tokens_last = self.session.result
            # A dialog closed meanwhile keeps the reading for the next time it opens, and draws nothing.
            if self.dialog.closed:
                return False
            if outcome == 'again':
                self.send(self.session.recut())
            self.show()
            return False
        self.app.client.submit('tokens', params, done, timeout=120)

    def choose(self, delay=0, **choice):
        picked = self.session.choose(**choice)
        if picked is None:
            return
        self.cancel_pending()
        self.sync()
        if delay:
            self.pending = GLib.timeout_add(delay, self.send_pending, picked)
        else:
            self.send(picked)

    def cancel_pending(self):
        if self.pending:
            GLib.source_remove(self.pending)
            self.pending = None

    def send_pending(self, picked):
        self.pending = None
        if not self.dialog.closed:
            self.send(picked)
        return False

    def send(self, picked):
        """Another period or basis cut from the last reading; the backend remembers it for next time."""
        self.cancel_pending()
        ticket, params = picked

        def done(result, error):
            if not self.session.finish_view(ticket, result, error):
                return False
            # Kept even when the dialog was closed meanwhile: the backend now opens on this period and basis.
            if error is None:
                self.app.tokens_last = self.session.result
            if not self.dialog.closed:
                self.show()
            return False
        self.app.client.submit('tokens.view', params, done, timeout=120)

    # ---------- controls ----------

    def period_changed(self, *_):
        if self.syncing:
            return
        hours = tv.preset_hours(self.period.get_selected())
        self.session.custom = hours is None
        # '직접' opens the hours row on the period in view; nothing is cut until the hours change.
        if hours is not None:
            self.choose(hours=hours)
        self.sync()

    def hours_changed(self, *_):
        if self.syncing:
            return
        hours = tv.clamp_hours(self.hours.get_value())
        if hours is not None:
            self.choose(delay=SETTLE_MS, hours=hours)

    @staticmethod
    def basis_setup(_factory, item):
        entry = box(spacing=2)
        entry.set_margin_top(4)
        entry.set_margin_bottom(4)
        entry.append(label())
        description = label('', 'muted small', True)
        description.set_max_width_chars(36)
        entry.append(description)
        item.set_child(entry)

    @staticmethod
    def basis_bind(_factory, item):
        choice = tv.BASES[item.get_position()] if item.get_position() < len(tv.BASES) else tv.BASES[0]
        entry = item.get_child()
        entry.get_first_child().set_text(choice.label)
        entry.get_last_child().set_text(choice.description)

    def basis_changed(self, *_):
        if self.syncing:
            return
        index = self.basis.get_selected()
        if index < len(tv.BASES):
            self.choose(basis=tv.BASES[index].id)

    def sync(self):
        hours, basis = self.session.choice
        ready = self.session.view is not None
        self.syncing = True
        try:
            self.period.set_selected(tv.period_index(hours, self.session.custom))
            self.hours.set_visible(self.session.custom)
            if tv.clamp_hours(self.hours.get_value()) != hours:
                self.hours.set_value(hours)
            self.basis.set_selected(tv.basis_index(basis))
        finally:
            self.syncing = False
        self.basis.set_subtitle(tv.basis(basis).description)
        for row in (self.period, self.hours, self.basis):
            row.set_sensitive(ready)
        self.again.set_label(tv.AGAIN_BUSY if self.session.loading else tv.AGAIN)
        self.again.set_sensitive(not self.session.loading)

    # ---------- page ----------

    def show(self):
        self.fill()
        # An empty slot would still take the body's spacing.
        for slot in (self.warning, self.upper, self.lower):
            slot.set_visible(slot.get_first_child() is not None)

    def fill(self):
        self.sync()
        view = self.session.view
        self.dialog.feedback.set_text(self.session.error or '')
        self.period.set_subtitle(tv.period_subtitle(view) if view else '')
        for slot in (self.warning, self.upper, self.lower):
            clear(slot)
        for row in self.burn_rows:
            self.burn.remove(row)
        self.burn_rows = []
        clear(self.notes)
        self.burn.set_visible(view is not None)
        if view is None:
            if self.session.loading:
                self.upper.append(self.loading_group())
            return
        warning = tv.device_warning(view)
        if warning:
            self.warning.append(self.warning_group(*warning))
        self.upper.append(self.throughput_group(tv.throughput(view)))
        model = tv.burn(view)
        # With both quotas turned off in the settings the whole group goes.
        self.burn.set_visible(bool(model.shown))
        self.fill_burn(model)
        self.lower.append(self.totals_group(view))
        footer = tv.collected_note(self.session.result, view)
        if footer:
            self.lower.append(label(footer, 'muted small', True))

    def retry_button(self):
        retry = Gtk.Button(label=tv.RETRY, valign=Gtk.Align.CENTER)
        retry.set_sensitive(not self.session.loading)
        retry.connect('clicked', lambda *_: self.load())
        return retry

    def loading_group(self):
        group = Adw.PreferencesGroup()
        row = Adw.ActionRow(title=tv.LOADING_TITLE, subtitle=tv.LOADING_CAPTION)
        row.add_prefix(Gtk.Spinner(spinning=True))
        group.add(row)
        return group

    def warning_group(self, title, message):
        group = Adw.PreferencesGroup()
        row = Adw.ActionRow(title=title, subtitle=message)
        row.set_use_markup(False)
        icon = Gtk.Image.new_from_icon_name('dialog-warning-symbolic')
        icon.add_css_class('warning')
        row.add_prefix(icon)
        row.add_suffix(self.retry_button())
        group.add(row)
        return group

    def figures(self, figures):
        flow = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, homogeneous=False, column_spacing=28, row_spacing=16,
                           min_children_per_line=2, max_children_per_line=len(figures))
        flow.set_can_focus(False)
        for figure in figures:
            cell = box(spacing=2)
            cell.set_valign(Gtk.Align.END)
            cell.append(label(figure.title, 'muted small'))
            line = box(False, 6)
            line.append(label(figure.value, 'token-figure-large' if figure.large else 'token-figure'))
            if figure.unit:
                unit = label(figure.unit, 'muted token-unit')
                unit.set_valign(Gtk.Align.END)
                line.append(unit)
            cell.append(line)
            flow.append(cell)
        return flow

    def composition(self, composition):
        part = box(spacing=8)
        part.append(label(composition.title, 'muted small'))
        bar = ShareBar()
        bar.show_parts([(item.share, item.tone) for item in composition.parts], composition.spoken)
        part.append(bar)
        legend = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, homogeneous=False, column_spacing=16, row_spacing=4,
                             max_children_per_line=len(composition.parts), halign=Gtk.Align.START)
        legend.set_can_focus(False)
        for item in composition.parts:
            entry = box(False, 6)
            swatch = Gtk.Box(valign=Gtk.Align.CENTER)
            swatch.add_css_class('token-swatch')
            swatch.add_css_class(item.tone)
            entry.append(swatch)
            entry.append(label(item.legend, 'muted small'))
            legend.append(entry)
        part.append(legend)
        return part

    def throughput_group(self, model):
        group = Adw.PreferencesGroup(title=tv.THROUGHPUT_TITLE, description=model.meta)
        rates = box(spacing=12)
        rates.append(self.figures(model.figures))
        rates.append(label(model.note, 'muted small', True))
        group.add(card(rates))
        parts = Gtk.Box(spacing=32, homogeneous=True)
        for composition in model.compositions:
            parts.append(self.composition(composition))
        group.add(card(parts))
        return group

    def fill_burn(self, model):
        # Rate cells of every account share one width, so the 5시간 and 주간 values line up in columns.
        self.cells = Gtk.SizeGroup(mode=Gtk.SizeGroupMode.HORIZONTAL)
        if model.waiting:
            rows = [self.waiting_row(model.waiting)]
        else:
            current = next((line for line in model.accounts if line.is_current), None)
            if current and current.profile_id is not None and not self.opened_current:
                self.open_accounts.add(current.profile_id)
                self.opened_current = True
            rows = [self.account_row(line) for line in model.accounts]
        for row in rows:
            self.burn.add(row)
        self.burn_rows = rows
        for note in model.notes:
            self.notes.append(label(note, 'muted small', True))
        self.notes.set_visible(bool(model.notes))

    def waiting_row(self, waiting):
        row = Adw.ActionRow(title=waiting.title, subtitle=f'{waiting.body}\n{waiting.caption}')
        row.set_use_markup(False)
        row.add_prefix(Gtk.Image.new_from_icon_name('document-open-recent-symbolic'))
        return row

    def rate_cell(self, cell):
        widget = box(spacing=1)
        widget.set_valign(Gtk.Align.CENTER)
        widget.append(label(cell.title, 'muted small'))
        line = box(False, 4)
        line.append(label(cell.value, 'token-rate rough' if cell.state == 'low' else 'token-rate'))
        if cell.tooltip:
            info = Gtk.Image.new_from_icon_name('dialog-information-symbolic')
            info.add_css_class('muted')
            line.append(info)
            widget.set_tooltip_text(cell.tooltip)
        widget.append(line)
        caption = label(cell.caption, 'muted small', True)
        caption.set_max_width_chars(18)
        widget.append(caption)
        self.cells.add_widget(widget)
        return widget

    def account_row(self, line):
        expandable = bool(line.evidence)
        row = Adw.ExpanderRow() if expandable else Adw.ActionRow()
        row.set_use_markup(False)
        row.set_title(line.name)
        row.set_subtitle(line.detail)
        avatar = Gtk.Image.new_from_icon_name('avatar-default-symbolic')
        avatar.set_valign(Gtk.Align.CENTER)
        avatar.add_css_class('token-avatar')
        if line.is_current:
            avatar.add_css_class('active')
        row.add_prefix(avatar)
        if line.is_current:
            chip = label(tv.CURRENT, 'usage-chip active')
            chip.set_valign(Gtk.Align.CENTER)
            row.add_suffix(chip)
        for cell in line.cells:
            row.add_suffix(self.rate_cell(cell))
        row.update_property([Gtk.AccessibleProperty.DESCRIPTION], [line.spoken])
        if expandable:
            for title, text in line.evidence:
                detail = Adw.ActionRow(title=title, subtitle=text)
                detail.set_use_markup(False)
                row.add_row(detail)
            row.set_expanded(line.profile_id in self.open_accounts)
            row.connect('notify::expanded', lambda widget, _prop, key=line.profile_id: self.remember(self.open_accounts, key, widget))
        return row

    def totals_group(self, view):
        group = Adw.PreferencesGroup(title=tv.TOTALS_TITLE, description=tv.TOTALS_DESCRIPTION)
        group.add(card(self.figures(tv.totals(view))))
        lines = tv.devices(view)
        for line in lines:
            group.add(self.device_row(line))
        if not lines:
            group.add(Adw.ActionRow(title=tv.NO_DEVICES))
        return group

    def device_row(self, line):
        expandable = bool(line.models) and not line.failed
        row = Adw.ExpanderRow() if expandable else Adw.ActionRow()
        row.set_use_markup(False)
        row.set_title(line.name)
        row.set_subtitle(line.detail)
        icon = Gtk.Image.new_from_icon_name('dialog-warning-symbolic' if line.failed else
                                            'computer-symbolic' if line.kind == 'local' else 'network-server-symbolic')
        if line.failed:
            icon.add_css_class('warning')
        row.add_prefix(icon)
        amount = label(line.amount, 'muted' if line.failed or not line.counted else 'token-amount')
        amount.set_valign(Gtk.Align.CENTER)
        row.add_suffix(amount)
        if line.failed:
            row.add_suffix(self.retry_button())
        if expandable:
            for model in line.models:
                row.add_row(self.model_row(model))
            row.set_expanded(line.id in self.open_devices)
            row.connect('notify::expanded', lambda widget, _prop, key=line.id: self.remember(self.open_devices, key, widget))
        return row

    def model_row(self, model):
        # Each model drawn against the largest; the bar is neutral like every token figure.
        row = Adw.ActionRow(title=model.name, subtitle=model.tiers or '', title_lines=1)
        row.set_use_markup(False)
        row.set_tooltip_text(model.tooltip)
        bar = ShareBar(width=96)
        bar.show_parts([(model.fraction, 'major')], model.spoken, track=True)
        row.add_suffix(bar)
        for value, css in ((model.tokens, 'token-amount'), (model.cost, 'muted small')):
            cell = label(value, css)
            cell.set_width_chars(7)
            cell.set_xalign(1)
            cell.set_valign(Gtk.Align.CENTER)
            row.add_suffix(cell)
        return row

    @staticmethod
    def remember(opened, key, row):
        (opened.add if row.get_expanded() else opened.discard)(key)
