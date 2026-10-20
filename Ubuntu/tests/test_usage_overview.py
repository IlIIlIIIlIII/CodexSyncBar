"""Owned native widgets with synthetic account data; no live account access."""
from datetime import datetime, timezone
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'ui'))
try:
    import gi
    gi.require_version('Gtk', '4.0')
    from gi.repository import Gtk, Gdk
    from claude_syncbar.usage_overview import UsageOverview
except (ImportError, ValueError):
    Gtk = None


@unittest.skipIf(Gtk is None, 'GTK 4 is required for the native widget checks.')
class UsageOverviewTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        Gtk.init()
        if Gdk.Display.get_default() is None:
            raise unittest.SkipTest('An owned GTK display is required.')

    def setUp(self):
        self.now = datetime(2026, 10, 6, 6, 42, tzinfo=timezone.utc)
        self.events = []
        self.view = UsageOverview(lambda value: self.events.append(('select', value)),
                                  lambda: self.events.append('refresh'),
                                  lambda: self.events.append('switch'),
                                  lambda: self.events.append('add'))
        self.snapshot = {'activeProfileId': 1, 'accounts': [
            {'id': 1, 'alias': '개인', 'email': 'main@example.com', 'plan': 'Max 20x', 'usage': {
                'weekly': {'usedPercent': 32, 'resetsAt': '2026-10-08T00:00:00Z'},
                'fiveHour': {'usedPercent': 85, 'resetsAt': '2026-10-06T08:00:00Z'},
                'updatedAt': self.now.isoformat()}},
            {'id': 2, 'alias': '보조', 'email': 'other@example.com', 'needsLogin': True},
        ]}
        self.view.reflow(1040)
        self.view.update(self.snapshot, now=self.now)

    def cell(self, account, key):
        return self.view.rows[account].cells[key]

    def test_both_quotas_show_side_by_side_with_their_own_values(self):
        self.assertEqual(['fiveHour', 'weekly'], list(self.view.rows[1].cells))
        self.assertEqual('15%', self.cell(1, 'fiveHour').value.get_text())
        self.assertTrue(self.cell(1, 'fiveHour').value.has_css_class('usage-warning'))
        self.assertEqual('68%', self.cell(1, 'weekly').value.get_text())
        self.assertAlmostEqual(.68, self.cell(1, 'weekly').meter.get_fraction())
        self.assertEqual(['5시간 한도', '주간 한도'], [t.get_text() for t in self.view.column_titles.values()])
        self.assertTrue(self.view.columns.get_visible())

    def test_plan_is_shown_with_the_email(self):
        self.assertEqual('main@example.com · Max 20x', self.view.rows[1].email.get_text())
        self.assertEqual('other@example.com', self.view.rows[2].email.get_text())

    def test_missing_has_no_fake_zero_or_reset(self):
        for key in ('fiveHour', 'weekly'):
            self.assertEqual('—', self.cell(2, key).value.get_text())
            self.assertEqual('정보 없음', self.cell(2, key).caption.get_text())
            self.assertTrue(self.cell(2, key).meter.has_css_class('missing'))
        self.assertEqual('재로그인 필요', self.view.rows[2].status.get_text())

    def test_poll_updates_values_without_replacing_rows(self):
        row = self.view.rows[1]
        self.snapshot['accounts'][0]['usage']['fiveHour']['usedPercent'] = 100
        self.view.update(self.snapshot, now=self.now)
        self.assertIs(row, self.view.rows[1])
        self.assertEqual('0%', self.cell(1, 'fiveHour').value.get_text())
        self.assertTrue(self.cell(1, 'fiveHour').meter.has_css_class('critical'))
        self.assertFalse(self.cell(1, 'fiveHour').meter.has_css_class('missing'))

    def test_footer_says_when_automatic_switching_is_on(self):
        self.assertNotIn('자동 전환', self.view.updated.get_text())
        self.snapshot['settings'] = {'autoSwitchEnabled': True}
        self.view.update(self.snapshot, now=self.now)
        self.assertIn('자동 전환 켜짐', self.view.updated.get_text())

    def test_settings_hide_a_quota_everywhere_on_the_page(self):
        self.snapshot['settings'] = {'claudeFiveHour': False}
        self.view.update(self.snapshot, now=self.now)
        self.assertEqual(['weekly'], list(self.view.rows[1].cells))
        self.assertEqual(['weekly'], list(self.view.column_titles))
        self.assertIn('주간 한도', self.view.subtitle.get_text())
        self.snapshot['settings'] = {}
        self.view.update(self.snapshot, now=self.now)
        self.assertEqual(['fiveHour', 'weekly'], list(self.view.rows[1].cells))

    def test_offline_retains_explicit_previous_data_and_recovers(self):
        self.view.update(self.snapshot, error='서비스 연결 실패', now=self.now)
        self.assertEqual('연결 실패 · 이전 수치', self.view.rows[1].status.get_text())
        self.assertEqual('68%', self.cell(1, 'weekly').value.get_text())
        self.assertTrue(self.view.notice.get_visible())
        self.assertFalse(self.view.rows[1].get_activatable())
        self.view.update(self.snapshot, now=self.now)
        self.assertFalse(self.view.notice.get_visible())
        self.assertTrue(self.view.rows[1].get_activatable())
        self.assertFalse(self.view.rows[1].status.get_visible())

    def test_refresh_pending_disables_actions_without_hiding_previous_values(self):
        self.view.update(self.snapshot, pending=True, now=self.now)
        self.assertFalse(self.view.refresh.get_sensitive())
        self.assertFalse(self.view.rows[1].get_activatable())
        self.assertEqual('갱신 중 · 이전 수치', self.view.rows[1].status.get_text())

    def test_empty_and_reordered_accounts_reconcile(self):
        self.snapshot['accounts'].reverse()
        self.view.update(self.snapshot, now=self.now)
        self.assertEqual(2, self.view.list.get_first_child().account_id)
        self.snapshot['accounts'] = []
        self.view.update(self.snapshot, now=self.now)
        self.assertFalse(self.view.rows)
        self.assertTrue(self.view.empty.get_visible())
        self.assertFalse(self.view.columns.get_visible())
        self.assertTrue(self.view.add.get_visible())

    def test_row_activation_and_footer_do_not_apply_accounts(self):
        self.view.list.emit('row-activated', self.view.rows[2])
        self.view.refresh.emit('clicked')
        self.view.switch.emit('clicked')
        self.assertEqual([('select', 2), 'refresh', 'switch'], self.events)

    def test_narrower_layouts_preserve_rows_and_reverse(self):
        row = self.view.rows[1]
        self.view.reflow(640)
        self.assertEqual('stacked', self.view.layout)
        self.assertFalse(self.view.columns.get_visible())
        self.assertTrue(self.cell(1, 'weekly').title.get_visible())
        self.assertEqual(Gtk.Orientation.HORIZONTAL, row.quotas.get_orientation())
        self.view.reflow(500)
        self.assertEqual(Gtk.Orientation.VERTICAL, row.quotas.get_orientation())
        self.assertFalse(self.view.refresh_text.get_visible())
        self.assertIs(row, self.view.rows[1])
        self.view.reflow(1040)
        self.assertTrue(self.view.columns.get_visible())
        self.assertFalse(self.cell(1, 'weekly').title.get_visible())
        self.assertTrue(self.view.refresh_text.get_visible())


if __name__ == '__main__':
    unittest.main()
