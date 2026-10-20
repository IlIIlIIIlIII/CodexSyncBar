"""Five-hour and weekly quota states must distinguish unknown, exhausted, old and reset values."""
from datetime import datetime, timedelta, timezone
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'ui'))
from claude_syncbar.quota import account_usage, percent, quota, remaining_percent, until, visible_quotas


class QuotaTests(unittest.TestCase):
    now = datetime(2026, 10, 6, 6, 42, tzinfo=timezone.utc)

    def account(self, weekly=32, five_hour=60, **changes):
        usage = {'weekly': {'usedPercent': weekly, 'resetsAt': (self.now + timedelta(days=2)).isoformat()},
                 'fiveHour': {'usedPercent': five_hour, 'resetsAt': (self.now + timedelta(hours=1, minutes=41, seconds=30)).isoformat()},
                 'updatedAt': self.now.isoformat()}
        usage.update(changes)
        return {'usage': usage}

    def test_each_quota_comes_from_its_own_entry_in_display_order(self):
        result = account_usage(self.account(), now=self.now)
        self.assertEqual([('fiveHour', 40), ('weekly', 68)], [(q.key, q.remaining) for q in result.quotas])
        self.assertEqual('', result.status)
        only_weekly = account_usage(self.account(), keys=('weekly',), now=self.now)
        self.assertEqual(['weekly'], [q.key for q in only_weekly.quotas])

    def test_settings_choose_the_quotas(self):
        self.assertEqual(('fiveHour', 'weekly'), visible_quotas(None))
        self.assertEqual(('weekly',), visible_quotas({'claudeFiveHour': False}))
        self.assertEqual((), visible_quotas({'claudeFiveHour': False, 'claudeWeekly': False}))

    def test_reset_captions(self):
        result = account_usage(self.account(), now=self.now)
        local = (self.now + timedelta(hours=1, minutes=41, seconds=30)).astimezone()
        self.assertEqual(f'{local:%H:%M} 초기화 · 1시간 42분 후', result.get('fiveHour').caption)
        self.assertEqual(f'{local:%H:%M} 초기화', result.get('fiveHour').short)
        self.assertRegex(result.get('weekly').caption, r'^\d+월 \d+일 \d\d:\d\d 초기화$')
        self.assertEqual('2시간 후', until(self.now + timedelta(hours=2), self.now))
        self.assertEqual('5분 후', until(self.now + timedelta(minutes=4, seconds=1), self.now))
        self.assertEqual('곧', until(self.now, self.now))

    def test_unused_and_already_reset_windows(self):
        unused = quota({'usedPercent': 0, 'resetsAt': None}, 'fiveHour', now=self.now)
        self.assertEqual(('100%', '사용 전', 'healthy'), (unused.value, unused.caption, unused.tone))
        passed = quota({'usedPercent': 95, 'resetsAt': (self.now - timedelta(minutes=1)).isoformat()}, 'fiveHour', now=self.now)
        self.assertEqual(('100%', '초기화됨'), (passed.value, passed.caption))
        # An old value is never presented as reset.
        old = quota({'usedPercent': 95, 'resetsAt': (self.now - timedelta(minutes=1)).isoformat()}, 'weekly', now=self.now, previous=True)
        self.assertEqual(('5%', 'muted'), (old.value, old.tone))

    def test_missing_and_exhausted_are_distinct(self):
        missing = account_usage(self.account(fiveHour=None), now=self.now).get('fiveHour')
        self.assertEqual(('—', '정보 없음', 'muted'), (missing.value, missing.caption, missing.tone))
        self.assertEqual('5시간 정보 없음', missing.spoken)
        exhausted = account_usage(self.account(five_hour=100), now=self.now).get('fiveHour')
        self.assertEqual(('0%', 'critical'), (exhausted.value, exhausted.tone))

    def test_thresholds_and_clamping(self):
        for remaining, tone in [(0, 'critical'), (10, 'critical'), (11, 'warning'), (25, 'warning'), (26, 'healthy'), (100, 'healthy')]:
            with self.subTest(remaining=remaining):
                self.assertEqual(tone, account_usage(self.account(weekly=100-remaining), now=self.now).get('weekly').tone)
        self.assertEqual(100, remaining_percent({'usedPercent': -4}))
        self.assertEqual(0, remaining_percent({'usedPercent': 104}))

    def test_zero_and_full_are_shown_only_when_exact(self):
        self.assertEqual(['0%', '0.1%', '0.2%', '0.3%', '1%', '72%', '99%', '100%', '—'],
                         [percent(v) for v in [0, 0.04, 0.2, 0.3, 0.96, 72.4, 99.6, 100, None]])
        sliver = account_usage(self.account(five_hour=99.8), now=self.now).get('fiveHour')
        self.assertEqual(('0.2%', 'critical'), (sliver.value, sliver.tone))

    def test_invalid_values_are_unknown(self):
        for value in [float('nan'), float('inf'), '32', True, None]:
            with self.subTest(value=value):
                self.assertIsNone(remaining_percent({'remainingPercent': value}))

    def test_account_reasons_are_said_once_and_keep_old_values_muted(self):
        for changes, options, reason, tone in [
            ({}, {'unavailable': True}, '연결 실패', 'warning'),
            ({}, {'refreshing': True}, '갱신 중', 'muted'),
            ({'needsLogin': True}, {}, '재로그인 필요', 'warning'),
            ({'isPending': True}, {}, '재로그인 필요', 'warning'),
        ]:
            with self.subTest(reason=reason):
                account = self.account()
                account.update(changes)
                result = account_usage(account, now=self.now, **options)
                self.assertEqual(reason + ' · 이전 수치', result.status)
                self.assertEqual(tone, result.status_tone)
                self.assertEqual([40, 68], [q.remaining for q in result.quotas])
                self.assertTrue(all(q.tone == 'muted' for q in result.quotas))
        self.assertEqual('조회 실패 · 이전 수치', account_usage(self.account(error='failed'), now=self.now).status)
        stale = self.account(updatedAt=(self.now - timedelta(minutes=11)).isoformat())
        self.assertEqual('갱신 지연 · 이전 수치', account_usage(stale, now=self.now).status)

    def test_missing_does_not_invent_values_or_reset_times(self):
        result = account_usage({'needsLogin': True}, now=self.now)
        self.assertEqual('재로그인 필요', result.status)
        self.assertEqual(['—', '—'], [q.value for q in result.quotas])
        self.assertEqual('아직 조회되지 않음', account_usage({}, now=self.now).status)


if __name__ == '__main__':
    unittest.main()
