"""Weekly-only quota states must distinguish unknown from exhausted data."""
from datetime import datetime, timedelta, timezone
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'ui'))
from codex_syncbar.weekly_usage import remaining_percent, weekly_balance


class WeeklyUsageTests(unittest.TestCase):
    now = datetime(2026, 10, 6, 6, 42, tzinfo=timezone.utc)

    def account(self, used=32, **changes):
        usage = {'weekly': {'usedPercent': used, 'resetsAt': (self.now + timedelta(days=2)).isoformat()},
                 'updatedAt': self.now.isoformat()}
        usage.update(changes)
        return {'usage': usage}

    def test_weekly_is_used_even_when_session_is_available(self):
        account = self.account(session={'usedPercent': 99})
        self.assertEqual(68, weekly_balance(account, now=self.now).remaining)

    def test_missing_and_exhausted_are_distinct(self):
        self.assertIsNone(weekly_balance(self.account(weekly=None), now=self.now).remaining)
        result = weekly_balance(self.account(100), now=self.now)
        self.assertEqual('0%', result.value)
        self.assertEqual('모두 소진', result.status)

    def test_thresholds_and_clamping(self):
        for remaining, tone in [(0, 'critical'), (10, 'critical'), (11, 'warning'), (25, 'warning'), (26, 'healthy'), (100, 'healthy')]:
            with self.subTest(remaining=remaining):
                result = weekly_balance(self.account(100-remaining), now=self.now)
                self.assertEqual(tone, result.tone)
        self.assertEqual(100, remaining_percent({'usedPercent': -4}))
        self.assertEqual(0, remaining_percent({'usedPercent': 104}))

    def test_invalid_values_are_unknown(self):
        for value in [float('nan'), float('inf'), '32', True, None]:
            with self.subTest(value=value):
                self.assertIsNone(remaining_percent({'remainingPercent': value}))

    def test_unavailable_refresh_failed_and_login_keep_explicit_old_values(self):
        for changes, options, reason in [
            ({}, {'unavailable': True}, '연결 실패'),
            ({}, {'refreshing': True}, '갱신 중'),
            ({'needsLogin': True}, {}, '재로그인 필요'),
            ({'isPending': True}, {}, '재로그인 필요'),
        ]:
            account = self.account()
            account.update(changes)
            result = weekly_balance(account, now=self.now, **options)
            self.assertEqual(68, result.remaining)
            self.assertEqual(reason + ' · 이전 수치', result.status)
            self.assertEqual('muted', result.tone)
        result = weekly_balance(self.account(error='failed'), now=self.now)
        self.assertEqual('조회 실패 · 이전 수치', result.status)

    def test_stale_or_expired_values_do_not_look_fresh(self):
        stale = self.account(updatedAt=(self.now-timedelta(minutes=11)).isoformat())
        self.assertEqual('갱신 지연 · 이전 수치', weekly_balance(stale, now=self.now).status)
        expired = self.account(weekly={'usedPercent': 32, 'resetsAt': self.now.isoformat()})
        self.assertEqual('초기화 확인 필요 · 이전 수치', weekly_balance(expired, now=self.now).status)

    def test_missing_does_not_invent_reset_time(self):
        result = weekly_balance({'needsLogin': True}, now=self.now)
        self.assertEqual('—', result.value)
        self.assertEqual('재로그인 필요', result.reset)


if __name__ == '__main__':
    unittest.main()
