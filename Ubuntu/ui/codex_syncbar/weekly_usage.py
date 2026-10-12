"""Weekly quota presentation, independent of GTK and account mutation."""
from dataclasses import dataclass
from datetime import datetime, timezone
import math


def timestamp(value):
    if not isinstance(value, str):
        return None
    try:
        result = datetime.fromisoformat(value.replace('Z', '+00:00'))
        return result.astimezone() if result.tzinfo else None
    except ValueError:
        return None


def remaining_percent(window):
    if not isinstance(window, dict):
        return None
    value = window.get('remainingPercent')
    if value is None:
        used = window.get('usedPercent')
        if isinstance(used, (int, float)) and not isinstance(used, bool):
            value = 100 - used
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return None
    return max(0, min(100, value))


@dataclass(frozen=True)
class WeeklyBalance:
    remaining: float | None
    tone: str
    status: str
    reset: str
    updated: datetime | None

    @property
    def value(self):
        return '—' if self.remaining is None else f'{self.remaining:.0f}%'


def weekly_balance(account, *, now=None, unavailable=False, refreshing=False):
    now = now or datetime.now(timezone.utc)
    usage = account.get('usage') or {}
    window = usage.get('weekly') or {}
    remaining = remaining_percent(window)
    updated = timestamp(usage.get('updatedAt'))
    resets = timestamp(window.get('resetsAt'))
    reset = f'{resets.month}월 {resets.day}일 {resets:%H:%M} 초기화' if resets else '초기화 시각 확인 필요'
    tone = 'muted' if remaining is None else 'critical' if remaining <= 10 else 'warning' if remaining <= 25 else 'healthy'
    status = '' if remaining is None else '모두 소진' if remaining == 0 else '거의 소진' if remaining <= 10 else '잔여량 적음' if remaining <= 25 else ''
    reason = None
    if unavailable:
        reason = '연결 실패'
    elif account.get('needsLogin') or account.get('isPending'):
        reason = '재로그인 필요'
    elif usage.get('error'):
        reason = '조회 실패'
    elif refreshing:
        reason = '갱신 중'
    elif remaining is not None and (updated is None or (now - updated).total_seconds() > 600):
        reason = '갱신 지연'
    elif remaining is not None and resets and resets <= now:
        reason = '초기화 확인 필요'
    if reason:
        status = reason + (' · 이전 수치' if remaining is not None else '')
        tone = 'muted'
    elif remaining is None:
        status = '주간 한도 정보 없음' if usage else '아직 조회되지 않음'
    if remaining is None:
        reset = status
    return WeeklyBalance(remaining, tone, status, reset, updated)
