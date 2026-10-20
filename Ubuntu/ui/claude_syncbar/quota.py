"""Five-hour and weekly quota presentation, independent of GTK and account mutation."""
from dataclasses import dataclass
from datetime import datetime, timezone
import math

# Usage key in the snapshot, title, settings key that shows it.
QUOTAS = (('fiveHour', '5시간', 'claudeFiveHour'), ('weekly', '주간', 'claudeWeekly'))
TITLES = {key: title for key, title, _ in QUOTAS}
# Older than this, a value is shown as the previous one.
STALE_SECONDS = 600


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


def percent(remaining):
    """'0%' only when nothing is left and '100%' only when nothing is used; a sliver shows as such."""
    if remaining is None:
        return '—'
    # Rounded up to a tenth, so the smallest sliver never reads as 0%.
    sliver = math.ceil(round(remaining * 10, 6)) / 10
    if 0 < remaining and sliver < 1:
        return f'{sliver:.1f}%'
    if 99 < remaining < 100:
        return '99%'
    return f'{remaining:.0f}%'


def visible_quotas(settings):
    """The quotas the user chose to see, in display order."""
    settings = settings or {}
    return tuple(key for key, _, setting in QUOTAS if settings.get(setting, True) is not False)


def tone_of(remaining):
    return 'critical' if remaining <= 10 else 'warning' if remaining <= 25 else 'healthy'


def until(moment, now):
    minutes = max(0, math.ceil((moment - now).total_seconds() / 60))
    hours, minutes = divmod(minutes, 60)
    if hours and minutes:
        return f'{hours}시간 {minutes}분 후'
    if hours:
        return f'{hours}시간 후'
    return f'{minutes}분 후' if minutes else '곧'


@dataclass(frozen=True)
class Quota:
    key: str
    title: str
    remaining: float | None
    tone: str
    caption: str
    short: str

    @property
    def value(self):
        return percent(self.remaining)

    @property
    def spoken(self):
        if self.remaining is None:
            return f'{self.title} 정보 없음'
        return f'{self.title} {self.value} 남음, {self.caption}'


@dataclass(frozen=True)
class AccountUsage:
    quotas: tuple
    status: str
    status_tone: str
    updated: datetime | None

    def get(self, key):
        return next((quota for quota in self.quotas if quota.key == key), None)


def quota(window, key, *, now, previous=False):
    title = TITLES[key]
    remaining = remaining_percent(window)
    if remaining is None:
        return Quota(key, title, None, 'muted', '정보 없음', '정보 없음')
    resets = timestamp(window.get('resetsAt'))
    # Past its reset time the whole quota is available again until it is next used.
    if resets and resets <= now and not previous:
        return Quota(key, title, 100, 'healthy', '초기화됨', '초기화됨')
    tone = 'muted' if previous else tone_of(remaining)
    if resets is None:
        caption = '사용 전' if remaining >= 100 else '초기화 시각 미정'
        return Quota(key, title, remaining, tone, caption, caption)
    if key == 'fiveHour':
        clock = f'{resets:%H:%M} 초기화'
        return Quota(key, title, remaining, tone, clock if previous else f'{clock} · {until(resets, now)}', clock)
    return Quota(key, title, remaining, tone, f'{resets.month}월 {resets.day}일 {resets:%H:%M} 초기화',
                 f'{resets.month}월 {resets.day}일 초기화')


def account_usage(account, *, keys=('fiveHour', 'weekly'), now=None, unavailable=False, refreshing=False):
    now = now or datetime.now(timezone.utc)
    usage = account.get('usage') or {}
    updated = timestamp(usage.get('updatedAt'))
    windows = {key: usage.get(key) for key in keys}
    known = any(remaining_percent(window) is not None for window in windows.values())
    reason = ''
    if unavailable:
        reason = '연결 실패'
    elif account.get('needsLogin') or account.get('isPending'):
        reason = '재로그인 필요'
    elif usage.get('error'):
        reason = '조회 실패'
    elif refreshing:
        reason = '갱신 중'
    elif known and (updated is None or (now - updated).total_seconds() > STALE_SECONDS):
        reason = '갱신 지연'
    status = reason + (' · 이전 수치' if reason and known else '')
    if not reason and not usage:
        status = '아직 조회되지 않음'
    status_tone = 'muted' if reason in ('', '갱신 중', '갱신 지연') else 'warning'
    quotas = tuple(quota(windows[key], key, now=now, previous=bool(reason)) for key in keys)
    return AccountUsage(quotas, status, status_tone, updated)
