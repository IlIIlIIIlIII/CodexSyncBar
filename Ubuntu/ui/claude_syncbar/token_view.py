"""What the token page says and decides, independent of GTK.

The view is the backend's TokenPeriodView (Windows/ClaudeSyncBar.Windows.Core/TokenRates.cs) in camelCase. The
wording follows the Windows token page in 합니다체, and numbers are formatted digit for digit as it formats them.
"""
from dataclasses import dataclass
from decimal import Decimal, ROUND_HALF_UP
import math

from .quota import TITLES, timestamp

MAXIMUM_HOURS = 720
PRESETS = ((1, '1시간'), (5, '5시간'), (24, '24시간'), (168, '7일'), (720, '30일'))
PRESET_HOURS = tuple(hours for hours, _ in PRESETS)
PERIOD_LABELS = tuple(name for _, name in PRESETS) + ('직접',)
QUOTA_KEYS = ('fiveHour', 'weekly')

INTRO = 'Claude Code 세션 기록을 고른 기간으로 집계합니다. 비용은 API 요금으로 환산한 추정치라 실제 청구 금액과 다릅니다.'
AGAIN = '다시 집계'
AGAIN_BUSY = '집계 중…'
RETRY = '다시 시도'
LOADING_TITLE = '세션 기록을 집계하고 있습니다…'
LOADING_CAPTION = '처음에는 1분쯤 걸릴 수 있습니다.'
PERIOD_TITLE = '기간'
HOURS_TITLE = '기간(시간)'
HOURS_SUBTITLE = '최근 1~720시간'
THROUGHPUT_TITLE = '시간당 평균'
BURN_TITLE = '1M 토큰당 한도 소모'
BURN_DESCRIPTION = ('고른 기간에 계정의 5시간·주간 사용률이 오른 만큼을 그동안 그 계정으로 쓴 토큰으로 나눈 평균입니다. '
                    '요금제마다 1%의 크기가 달라 계정별로 보여 줍니다.')
BASIS_TITLE = '기준 토큰'
CURRENT = '사용 중'
ROUGH_TIP = '한도가 1%p 단위로만 보여서 변화가 작으면 오차가 큽니다. 기간을 늘리면 정확해집니다.'
CAVEAT = 'Claude 데스크톱·웹 채팅이나 집계하지 않는 기기에서 쓴 양도 같은 한도를 줄여서, 실제보다 크게 나올 수 있습니다.'
TOTALS_TITLE = '기간 합계'
TOTALS_DESCRIPTION = '고른 기간 동안 쓴 양입니다.'
NO_DEVICES = '집계된 기록이 없습니다.'
DEVICE_WARNING = '처리량과 합계는 나머지 장치만 셌습니다. 그 장치가 계정을 쓰던 시간은 소모율에서 뺍니다.'
UNREADABLE = '토큰 집계 결과를 읽지 못했습니다.'


@dataclass(frozen=True)
class Basis:
    id: str
    label: str
    short: str
    description: str


BASES = (
    Basis('uncached', '캐시 읽기 제외', '캐시 읽기 제외 토큰',
          '캐시 쓰기·캐시 없는 입력·출력을 1개씩 셉니다. 입력의 96%인 캐시 읽기를 빼서 대화 길이에 덜 흔들립니다.'),
    Basis('total', '전체 토큰', '전체 토큰', '아래 합계의 전체 토큰과 같은 기준입니다. 대부분이 캐시 읽기라 값이 아주 작습니다.'),
    Basis('weighted', '요금 환산', '요금 환산 토큰', 'API 요금 비율로 셉니다. Opus 5.5 입력 1개를 1로, 캐시 읽기는 0.05개, 출력은 5개로 칩니다.'),
)
# Cache reads leave the count unless another basis is chosen, as the backend does.
DEFAULT_BASIS = BASES[0].id


# ---------- values from the view ----------

def number(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return None
    return float(value)


def text(value):
    return value if isinstance(value, str) else None


def flag(value):
    return value is True


def _dict(value):
    return value if isinstance(value, dict) else {}


def _dicts(value):
    return [item for item in value if isinstance(item, dict)] if isinstance(value, list) else []


def _of(source, key):
    return number(_dict(source).get(key)) or 0.0


def normalize_basis(value):
    return value if value in ('total', 'weighted') else DEFAULT_BASIS


def basis(value):
    return next((item for item in BASES if item.id == value), BASES[0])


def basis_index(value):
    return next((index for index, item in enumerate(BASES) if item.id == value), 0)


def clamp_hours(value):
    """Whole hours from 1 to 720, as the backend clamps them; None for anything that is not a number."""
    value = number(value)
    return None if value is None else int(min(MAXIMUM_HOURS, max(1, round(value))))


def view_hours(view):
    return clamp_hours(_dict(view).get('hours')) or MAXIMUM_HOURS


def period_index(hours, custom):
    """The period list entry: a preset, or '직접' for any other number of hours."""
    if not custom and hours in PRESET_HOURS:
        return PRESET_HOURS.index(hours)
    return len(PRESETS)


def preset_hours(index):
    """The hours a period list entry stands for; None for '직접'."""
    return PRESETS[index][0] if 0 <= index < len(PRESETS) else None


# ---------- number formats, the Windows page's digit for digit ----------

def _custom(value, digits):
    # .NET's custom formats ("0.00") take 15 significant digits, then round a half away from zero.
    return Decimal(format(value, '.15g')).quantize(Decimal(1).scaleb(-digits), rounding=ROUND_HALF_UP)


def _whole(value):
    # Math.Round: a half goes to the even neighbour, as Python's round does.
    return int(round(value))


def tokens(count):
    """'1.23B', '48.62M', '123K', '12.3K', '9,999'."""
    count = _whole(number(count) or 0)
    if count >= 1_000_000_000:
        return f'{_custom(count / 1e9, 2):f}B'
    if count >= 1_000_000:
        return f'{_custom(count / 1e6, 2):f}M'
    if count >= 100_000:
        return f'{_custom(count / 1e3, 0):f}K'
    if count >= 10_000:
        return f'{_custom(count / 1e3, 1):f}K'
    return f'{count:,}'


def dollars(amount):
    """'$4,652', '$49.6', '$1.32': three significant figures."""
    amount = number(amount) or 0.0
    if amount >= 100:
        return f'${_whole(amount):,}'
    if amount >= 10:
        return f'${_custom(amount, 1):f}'
    return f'${_custom(amount, 2):f}'


def rate_text(rate):
    """'18%p', '4.6%p', '0.27%p': whole from 10, a decimal from 1, else two significant figures, so a weekly rate never reads 0."""
    rate = number(rate) or 0.0
    if rate >= 10:
        return f'{_whole(rate)}%p'
    if rate >= 1:
        return f'{_custom(rate, 1):f}%p'
    if rate <= 0:
        return '0%p'
    decimals = min(6, max(1, 1 - math.floor(math.log10(rate))))
    return f'{Decimal(rate).quantize(Decimal(1).scaleb(-decimals), rounding=ROUND_HALF_UP):f}%p'


def share_text(fraction):
    """A share of a whole: whole percent, a decimal below 1%, '0.1% 미만' below that."""
    percent = (number(fraction) or 0.0) * 100
    if percent <= 0:
        return '0%'
    if percent < 0.1:
        return '0.1% 미만'
    if percent < 1:
        return f'{_custom(percent, 1):f}%'
    return f'{_whole(percent)}%'


def points(value):
    """Percentage points: '240', '2.5', '1,234'."""
    value = number(value) or 0.0
    if value == math.floor(value):
        return f'{int(value):,}'
    return f'{_custom(value, 1):,f}'


def fraction(part, whole):
    return 0.0 if whole <= 0 else min(1.0, max(0.0, part / whole))


def moment(at):
    return f'{at.month}월 {at.day}일 {at:%H:%M}'


def elapsed(view):
    """How long the period's tokens were divided by: '24시간', '30분', '119시간 40분'."""
    hours = number(_dict(view).get('elapsedHours'))
    minutes = _whole((view_hours(view) if hours is None else hours) * 60)
    if minutes % 60 == 0:
        return f'{minutes // 60}시간'
    if minutes < 60:
        return f'{minutes}분'
    return f'{minutes // 60}시간 {minutes % 60}분'


# ---------- period ----------

def range_text(view):
    start, end = timestamp(_dict(view).get('startedAt')), timestamp(_dict(view).get('endedAt'))
    if not start or not end:
        return ''
    if start.date() == end.date():
        return f'{moment(start)} – {end:%H:%M}'
    return f'{moment(start)} – {moment(end)}'


def clipped_note(view):
    """When the records begin inside the period, the rates are divided by the time they cover."""
    first = timestamp(_dict(view).get('recordStartedAt'))
    if not flag(_dict(view).get('clipped')) or first is None:
        return None
    return f'기록이 {moment(first)}부터 있어 {elapsed(view)}으로 나눴습니다.'


def period_subtitle(view):
    return '\n'.join(part for part in (range_text(view), clipped_note(view)) if part)


# ---------- tokens an hour ----------

@dataclass(frozen=True)
class Figure:
    title: str
    value: str
    unit: str | None = None
    large: bool = False


@dataclass(frozen=True)
class Part:
    label: str
    tone: str  # 'major', 'minor' or 'none'; 'none' stays in the legend only.
    share: float
    per_hour: float

    @property
    def legend(self):
        return f'{self.label} {tokens(self.per_hour)} · {share_text(self.share)}'


@dataclass(frozen=True)
class Composition:
    title: str
    parts: tuple

    @property
    def spoken(self):
        return f'{self.title}: ' + ', '.join(f'{part.label} {share_text(part.share)}' for part in self.parts)


@dataclass(frozen=True)
class Throughput:
    meta: str
    figures: tuple
    note: str
    compositions: tuple


def throughput(view):
    rate = _dict(view).get('throughput')
    of = lambda key: _of(rate, key)
    figures = (Figure('전체', tokens(of('total')), '토큰/시간', True), Figure('입력', tokens(of('input'))),
               Figure('출력', tokens(of('output'))), Figure('캐시 읽기 제외', tokens(of('uncached'))))
    active = int(number(_dict(view).get('activeHours')) or 0)
    spanned = math.ceil((number(_dict(view).get('elapsedHours')) or 0) - 1e-9)
    if active == 0:
        note = '이 기간에는 기록이 없습니다.'
    elif active >= spanned:
        note = '기간 내내 요청이 있었습니다.'
    else:
        note = f'요청이 있던 {active}시간만 치면 시간당 전체 {tokens(of("activeTotal"))} · 출력 {tokens(of("activeOutput"))}입니다.'
    inputs = Composition('입력 구성', (
        Part('캐시 읽기', 'major', fraction(of('cacheRead'), of('input')), of('cacheRead')),
        Part('캐시 쓰기', 'minor', fraction(of('cacheWrite'), of('input')), of('cacheWrite')),
        Part('캐시 없는 입력', 'none', fraction(of('uncachedInput'), of('input')), of('uncachedInput'))))
    reasoning = fraction(of('reasoning'), of('output'))
    outputs = Composition('출력 구성', (
        Part('응답', 'major', 0.0 if of('output') <= 0 else 1 - reasoning, of('output') - of('reasoning')),
        Part('추론', 'minor', reasoning, of('reasoning'))))
    return Throughput(f'경과 {elapsed(view)} 기준', figures, note, (inputs, outputs))


def bar_segments(parts, width, *, track=False, gap=2):
    """Where a bar's pieces go, as (x, width, tone).

    A composition fills the bar with the parts big enough to see, side by side; with track, one part is drawn
    against the whole, the way a model is drawn against the largest.
    """
    if width <= 0:
        return []
    if track:
        share = min(1.0, max(0.0, parts[0][0])) if parts else 0.0
        drawn = [(0.0, float(width), 'track')]
        if share > 0:
            drawn.append((0.0, width * share, parts[0][1]))
        return drawn
    visible = [(share, tone) for share, tone in parts if share >= 0.005 and tone != 'none']
    total = sum(share for share, _ in visible)
    if total <= 0:
        return [(0.0, float(width), 'track')]
    room = max(0.0, width - gap * (len(visible) - 1))
    drawn, x = [], 0.0
    for share, tone in visible:
        size = room * share / total
        drawn.append((x, size, tone))
        x += size + gap
    return drawn


# ---------- quota use per million tokens ----------

@dataclass(frozen=True)
class RateCell:
    key: str
    title: str
    state: str  # 'ok', 'low' or 'collecting'
    value: str
    caption: str
    tooltip: str | None

    @property
    def spoken(self):
        return f'{self.title} {self.value}, {self.caption}'


@dataclass(frozen=True)
class AccountLine:
    profile_id: int | None
    name: str
    is_current: bool
    detail: str
    cells: tuple
    evidence: tuple  # (label, text)

    @property
    def spoken(self):
        parts = [cell.spoken for cell in self.cells]
        if self.is_current:
            parts.append(CURRENT)
        return ', '.join(parts)


@dataclass(frozen=True)
class Waiting:
    title: str
    body: str
    caption: str


@dataclass(frozen=True)
class Burn:
    basis: Basis
    per: str
    word: str
    waiting: Waiting | None
    shown: tuple
    accounts: tuple
    notes: tuple


def _state(quota):
    return text(_dict(quota).get('state'))


def rate_cell(quota, key, per, word):
    quota = _dict(quota)
    title = f'{TITLES[key]} · {per}'
    state = _state(quota)
    rate = number(quota.get('ratePerMillion')) or 0.0
    delta = points(number(quota.get('deltaPercent')) or 0.0)
    if state == 'ok':
        caption = f'100% ≈ {tokens(1e8 / rate)}{word}' if rate > 0 else '100% ≈ —'
        return RateCell(key, title, 'ok', rate_text(rate), caption, None)
    if state == 'low':
        return RateCell(key, title, 'low', '약 ' + rate_text(rate), f'참고용 · 변화 {delta}%p', ROUGH_TIP)
    return RateCell(key, title, 'collecting', '모으는 중', f'변화 {delta}%p · 3%p부터 보입니다', None)


def evidence(account, word, shown):
    """How each rate was made: windows and points over tokens, the devices, and what was left out."""
    lines = []
    for key in shown:
        quota = _dict(account.get(key))
        lines.append((TITLES[key], f'창 {int(number(quota.get("windows")) or 0)}개 · 사용률 +{points(_of(quota, "deltaPercent"))}%p '
                                   f'÷ {tokens(_of(quota, "tokens"))}{word}'))
    devices = [name for name in account.get('devices') or [] if isinstance(name, str)]
    if devices:
        lines.append(('장치', ', '.join(devices)))
    cut = [f'토큰 기록 없이 오른 {TITLES[key]} {points(_of(account.get(key), "untrackedPercent"))}%p'
           for key in shown if _of(account.get(key), 'untrackedPercent') > 0]
    if any(flag(_dict(account.get(key)).get('planChanged')) for key in QUOTA_KEYS):
        cut.append('요금제가 바뀌기 전 기록')
    if cut:
        lines.append(('뺀 구간', ' · '.join(cut)))
    return tuple(lines)


def account_line(account, per, word, shown):
    plan = text(account.get('plan'))
    detail = ' · '.join(part for part in (plan, f'{tokens(_of(account, "attributedTokens"))}{word}') if part)
    profile = number(account.get('profileId'))
    return AccountLine(None if profile is None else int(profile), text(account.get('name')) or '계정', flag(account.get('isCurrent')),
                       detail, tuple(rate_cell(account.get(key), key, per, word) for key in shown), evidence(account, word, shown))


def waiting(since):
    caption = (f'기록 시작 · {moment(since)} · 처리량과 합계는 그 전 기록으로도 바로 보입니다.' if since
               else '아직 기록이 없습니다 · 처리량과 합계는 지난 기록으로 바로 보입니다.')
    return Waiting('한도 기록을 모으고 있습니다',
                   '앱이 켜져 있는 동안 5분마다 계정별 5시간·주간 사용률을 기록합니다. 토큰을 쓰는 동안 기록이 쌓이면 보통 몇 시간 안에 첫 값이 나옵니다.',
                   caption)


def burn_notes(data, chosen, word=' 토큰'):
    notes = []
    basis_tokens, attributed = _of(data, 'basisTokens'), _of(data, 'attributedTokens')
    unregistered, unpriced = _of(data, 'unregisteredTokens'), _of(data, 'unpricedTokens')
    if basis_tokens > 0:
        share = attributed / basis_tokens
        # Under 1% is written as such; otherwise the whole percent, never rounded up to 100%.
        shown = share_text(share) if 0 < share < 0.01 else f'{math.floor(share * 100)}%'
        particle = '을' if shown.endswith('미만') else '를'
        note = f'이 기간 {chosen.short} {tokens(basis_tokens)} 중 {shown}{particle} 계정에 연결해 계산했습니다.'
        if 1 - (attributed + unregistered) / basis_tokens > 0.005:
            note += ' 앱이 꺼져 있었거나 어느 계정이 썼는지 모르는 시간의 토큰은 뺐습니다.'
        notes.append(note)
    if unregistered > 0:
        notes.append(f'등록하지 않은 계정으로 쓴 {tokens(unregistered)}{word}은 어느 계정의 한도에도 넣지 않았습니다.')
    if unpriced > 0:
        notes.append(f'요금을 모르는 모델의 {tokens(unpriced)} 토큰은 환산할 수 없어 그 구간을 뺐습니다.')
    notes.append(CAVEAT)
    idle = [name for name in data.get('idleAccounts') or [] if isinstance(name, str)]
    if idle and all(name.endswith(' 계정') for name in idle):
        notes.append('·'.join(name[:-3] for name in idle) + ' 계정은 이 기간에 쓴 기록이 없습니다.')
    elif idle:
        notes.append('이 기간에 쓴 기록이 없는 계정: ' + ', '.join(idle))
    return tuple(notes)


def burn(view):
    """Each account's five-hour and weekly use per million tokens, or the wait for the first readings."""
    data = _dict(_dict(view).get('burn'))
    chosen = basis(_dict(view).get('basis'))
    weighted = chosen.id == 'weighted'
    per = '환산 1M 토큰당' if weighted else '1M 토큰당'
    word = ' 환산 토큰' if weighted else ' 토큰'
    accounts = _dicts(data.get('accounts'))
    # The settings decide which quotas show (a backend without the flags shows both); with neither there is nothing.
    shown = tuple(key for key, setting in zip(QUOTA_KEYS, ('showFiveHour', 'showWeekly')) if data.get(setting) is not False)
    if not shown:
        return Burn(chosen, per, word, None, (), (), ())
    rated = any(_state(account.get(key)) in ('ok', 'low') for account in accounts for key in QUOTA_KEYS)
    since = timestamp(data.get('historyStartedAt'))
    started = timestamp(_dict(view).get('startedAt'))
    if not rated and (since is None or (started is not None and since > started)):
        return Burn(chosen, per, word, waiting(since), shown, (), ())
    ordered = sorted(accounts, key=lambda account: not flag(account.get('isCurrent')))
    return Burn(chosen, per, word, None, shown, tuple(account_line(account, per, word, shown) for account in ordered),
                burn_notes(data, chosen, word))


# ---------- totals and devices ----------

@dataclass(frozen=True)
class ModelLine:
    name: str
    tiers: str | None
    tooltip: str
    fraction: float
    tokens: str
    cost: str

    @property
    def spoken(self):
        return f'{self.name} {self.tokens} 토큰, {self.cost}'


@dataclass(frozen=True)
class DeviceLine:
    id: str
    name: str
    kind: str
    failed: bool
    counted: bool
    detail: str
    amount: str
    models: tuple


def totals(view):
    data = _dict(_dict(view).get('totals'))
    return (Figure('전체 토큰', tokens(_of(data, 'totalTokens')), '토큰', True),
            Figure('추정 비용 (USD)', dollars(_of(data, 'estimatedCostUsd')), None, True),
            Figure('입력', tokens(_of(data, 'inputTokens'))), Figure('캐시 읽기', tokens(_of(data, 'cachedInputTokens'))),
            Figure('출력', tokens(_of(data, 'outputTokens'))))


def device_failed(device):
    return text(device.get('error')) is not None or not flag(device.get('isReachable'))


def model_lines(models):
    largest = max([1.0, *(_of(model, 'totalTokens') for model in models)])
    lines = []
    for model in models:
        name = text(model.get('model')) or 'unknown'
        tiers = text(model.get('serviceTiers')) or 'default'
        amount = _of(model, 'totalTokens')
        lines.append(ModelLine(name, None if tiers == 'default' else tiers, name if tiers == 'default' else f'{name} · {tiers}',
                               fraction(amount, largest), tokens(amount),
                               dollars(_of(model, 'estimatedCostUsd')) if flag(model.get('isPriced')) else '요금 미상'))
    return tuple(lines)


def devices(view):
    lines = []
    for device in _dicts(_dict(view).get('devices')):
        kind = text(device.get('kind')) or 'local'
        failed = device_failed(device)
        counted = flag(device.get('hasMinutes'))
        place = {'local': 'Ubuntu', 'ssh': 'SSH'}.get(kind, 'WSL')
        if failed:
            detail = text(device.get('error')) or '연결 실패 · 집계하지 못했습니다'
        else:
            detail = place + (' · 집계 완료' if counted else ' · 기간별 기록 없음 · 장치를 다시 설치하면 보입니다')
        amount = '—' if failed or not counted else f'{tokens(_of(device, "totalTokens"))} 토큰 · {dollars(_of(device, "estimatedCostUsd"))}'
        lines.append(DeviceLine(text(device.get('id')) or '', text(device.get('displayName')) or '장치', kind, failed, counted,
                                detail, amount, () if failed else model_lines(_dicts(device.get('models')))))
    return tuple(lines)


def device_warning(view):
    """(title, message) naming the devices whose logs were not read; None when every device was read."""
    failed = [text(device.get('displayName')) or '장치' for device in _dicts(_dict(view).get('devices')) if device_failed(device)]
    return (f'{", ".join(failed)} 기록을 읽지 못했습니다', DEVICE_WARNING) if failed else None


def collected_note(result, view):
    at = timestamp(_dict(result).get('collectedAt')) or timestamp(_dict(view).get('endedAt'))
    return f'{moment(at)} 집계 · 요금을 모르는 모델은 비용에서 빠집니다.' if at else ''


# ---------- which reply is shown ----------

class TokenSession:
    """The reading on screen and the period and basis chosen, numbering every request so only the newest reply counts.

    'tokens' reads every device's logs again; 'tokens.view' cuts another period or basis from that reading.
    """

    def __init__(self, last=None, settings=None):
        self.result = last if isinstance(last, dict) and isinstance(last.get('view'), dict) else None
        self.error = None
        self.loading = False
        self.latest = 0
        settings = _dict(settings)
        view = self.view or {}
        hours = clamp_hours(view.get('hours')) or clamp_hours(settings.get('tokenPeriodHours')) or MAXIMUM_HOURS
        self.choice = (hours, normalize_basis(view.get('basis') if view else settings.get('tokenRateBasis')))
        self.custom = hours not in PRESET_HOURS

    @property
    def view(self):
        view = _dict(self.result).get('view')
        return view if isinstance(view, dict) else None

    def _params(self):
        return {'hours': self.choice[0], 'basis': self.choice[1]}

    def _adopt(self, view):
        # The backend clamps and normalizes; what it cut is what is chosen now.
        hours = view_hours(view)
        self.choice = (hours, normalize_basis(view.get('basis')))
        if hours not in PRESET_HOURS:
            self.custom = True

    def _keep_view(self):
        # The figures on screen are still the earlier cut: the period and basis say which.
        if self.view:
            self._adopt(self.view)

    def start_load(self):
        """(ticket, params) for 'tokens'; without a reading yet the backend opens on the period chosen last time."""
        self.loading = True
        self.latest += 1
        return self.latest, self._params() if self.view else {}

    def finish_load(self, ticket, result, error):
        """'shown', 'failed', 'stale', or 'again' when a newer choice was made meanwhile and should be cut from this reading."""
        self.loading = False
        if ticket != self.latest:
            return 'stale' if error is not None else 'again'
        if error is not None or not isinstance(result, dict) or not isinstance(result.get('view'), dict):
            # A cut asked for before this reading was dropped with it, so the choice goes back to the figures.
            self.error = str(error) if error is not None else UNREADABLE
            self._keep_view()
            return 'failed'
        self.result, self.error = result, None
        self._adopt(result['view'])
        return 'shown'

    def choose(self, hours=None, basis=None):
        """(ticket, params) for 'tokens.view', or None when nothing changed."""
        picked = (self.choice[0] if hours is None else clamp_hours(hours) or self.choice[0],
                  self.choice[1] if basis is None else normalize_basis(basis))
        if picked == self.choice:
            return None
        self.choice = picked
        self.latest += 1
        return self.latest, self._params()

    def recut(self):
        """(ticket, params) cutting the choice again, after a new reading arrived behind it."""
        self.latest += 1
        return self.latest, self._params()

    def finish_view(self, ticket, result, error):
        """True when this reply is the newest and now on screen (or its error); an older one is ignored."""
        if ticket != self.latest:
            return False
        if error is None and isinstance(result, dict) and isinstance(result.get('view'), dict):
            merged = dict(self.result or {})
            merged['view'] = result['view']
            if result.get('collectedAt') is not None:
                merged['collectedAt'] = result['collectedAt']
            self.result, self.error = merged, None
            self._adopt(result['view'])
            return True
        self.error = str(error) if error is not None else UNREADABLE
        self._keep_view()
        return True
