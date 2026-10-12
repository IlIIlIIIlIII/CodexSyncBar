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
    from codex_syncbar.weekly_overview import WeeklyOverview
except (ImportError, ValueError):
    Gtk = None


@unittest.skipIf(Gtk is None, 'GTK 4 is required for the native widget checks.')
class WeeklyOverviewTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        Gtk.init()
        if Gdk.Display.get_default() is None:
            raise unittest.SkipTest('An owned GTK display is required.')

    def setUp(self):
        self.now = datetime(2026, 10, 6, 6, 42, tzinfo=timezone.utc)
        self.events = []
        self.view = WeeklyOverview(lambda value: self.events.append(('select', value)),
                                   lambda: self.events.append('refresh'),
                                   lambda: self.events.append('switch'),
                                   lambda: self.events.append('add'))
        self.snapshot = {'activeProfileId': 1, 'accounts': [
            {'id': 1, 'alias': '개인', 'email': 'main@example.com', 'usage': {
                'weekly': {'usedPercent': 32, 'resetsAt': '2026-10-08T00:00:00Z'},
                'updatedAt': self.now.isoformat()}},
            {'id': 2, 'alias': '보조', 'email': 'other@example.com', 'needsLogin': True},
        ]}
        self.view.reflow(1040)
        self.view.update(self.snapshot, now=self.now)

    def test_missing_has_no_fake_zero_or_reset(self):
        self.assertEqual('68%', self.view.rows[1].value.get_text())
        self.assertAlmostEqual(.68, self.view.rows[1].meter.get_fraction())
        self.assertEqual('—', self.view.rows[2].value.get_text())
        self.assertEqual('재로그인 필요', self.view.rows[2].reset.get_text())
        self.assertTrue(self.view.rows[2].meter.has_css_class('missing'))

    def test_poll_updates_values_without_replacing_rows(self):
        row = self.view.rows[1]
        self.snapshot['accounts'][0]['usage']['weekly']['usedPercent'] = 100
        self.view.update(self.snapshot, now=self.now)
        self.assertIs(row, self.view.rows[1])
        self.assertEqual('0%', row.value.get_text())
        self.assertEqual('모두 소진', row.status.get_text())
        self.assertFalse(row.meter.has_css_class('missing'))

    def test_offline_retains_explicit_previous_data_and_recovers(self):
        self.view.update(self.snapshot, error='서비스 연결 실패', now=self.now)
        self.assertEqual('연결 실패 · 이전 수치', self.view.rows[1].status.get_text())
        self.assertTrue(self.view.notice.get_visible())
        self.assertFalse(self.view.rows[1].get_activatable())
        self.view.update(self.snapshot, now=self.now)
        self.assertFalse(self.view.notice.get_visible())
        self.assertTrue(self.view.rows[1].get_activatable())
        self.assertEqual('', self.view.rows[1].status.get_text())

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
        self.assertFalse(self.view.axis.get_visible())
        self.assertTrue(self.view.add.get_visible())

    def test_row_activation_and_footer_do_not_apply_accounts(self):
        self.view.list.emit('row-activated', self.view.rows[2])
        self.view.refresh.emit('clicked')
        self.view.switch.emit('clicked')
        self.assertEqual([('select', 2), 'refresh', 'switch'], self.events)

    def test_narrow_reflow_preserves_rows_and_reverses(self):
        row = self.view.rows[1]
        self.view.reflow(600)
        self.assertFalse(self.view.axis_identity.get_visible())
        self.assertIs(row, self.view.rows[1])
        self.assertFalse(self.view.refresh_text.get_visible())
        self.view.reflow(1040)
        self.assertTrue(self.view.axis_identity.get_visible())
        self.assertTrue(self.view.refresh_text.get_visible())


if __name__ == '__main__':
    unittest.main()
