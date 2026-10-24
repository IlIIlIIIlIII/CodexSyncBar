"""Token page wording and decisions over a realistic period view, without GTK or a backend."""
import copy
import os
from pathlib import Path
import sys
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'ui'))
from claude_syncbar import token_view as tv

# Shaped like the demo backend's view (DemoService.DemoTokens through TokenRateCalculator): a day of heavy use,
# about 81M tokens an hour, nineteen in twenty of them cache reads, on this PC and one SSH server.
VIEW = {
    'hours': 24, 'basis': 'uncached',
    'startedAt': '2026-10-24T03:12:00.1234567+00:00', 'endedAt': '2026-10-25T03:12:00.1234567+00:00',
    'elapsedHours': 24.0, 'clipped': False, 'recordStartedAt': '2026-10-20T03:32:00+00:00', 'activeHours': 21,
    'throughput': {'total': 81_234_567.3, 'input': 80_836_517.9, 'output': 398_049.4, 'uncached': 3_517_136.1,
                   'cacheRead': 77_717_431.2, 'cacheWrite': 3_079_871.3, 'uncachedInput': 39_215.4, 'reasoning': 172_355.4,
                   'activeTotal': 92_839_505.5, 'activeOutput': 454_913.6},
    'totals': {'totalTokens': 1_949_629_616, 'inputTokens': 1_940_076_430, 'cachedInputTokens': 1_865_218_349,
               'cacheWriteInputTokens': 73_916_911, 'uncachedInputTokens': 941_170, 'outputTokens': 9_553_186,
               'reasoningOutputTokens': 4_136_530, 'requests': 14_336, 'estimatedCostUsd': 1487.62, 'unpricedTokens': 0},
    'devices': [
        {'id': 'local', 'displayName': '이 Ubuntu PC', 'kind': 'local', 'isReachable': True, 'error': None, 'hasMinutes': True,
         'totalTokens': 1_781_961_469, 'estimatedCostUsd': 1359.68, 'models': [
             {'model': 'claude-opus-5-5', 'serviceTiers': 'default', 'totalTokens': 1_568_126_093, 'estimatedCostUsd': 1302.11, 'isPriced': True},
             {'model': 'claude-sonnet-5-5', 'serviceTiers': 'default', 'totalTokens': 213_835_376, 'estimatedCostUsd': 57.57, 'isPriced': True}]},
        {'id': 'ssh:dev', 'displayName': '개발 서버', 'kind': 'ssh', 'isReachable': True, 'error': None, 'hasMinutes': True,
         'totalTokens': 167_668_147, 'estimatedCostUsd': 127.94, 'models': [
             {'model': 'claude-opus-5-5', 'serviceTiers': 'default, priority', 'totalTokens': 147_547_969, 'estimatedCostUsd': 112.59, 'isPriced': True},
             {'model': 'claude-sonnet-5-5', 'serviceTiers': 'default', 'totalTokens': 20_120_178, 'estimatedCostUsd': 15.35, 'isPriced': True}]},
    ],
    'burn': {
        'basis': 'uncached', 'historyStartedAt': '2026-10-20T03:36:00+00:00',
        'basisTokens': 84_411_266.4, 'attributedTokens': 71_929_576.4,
        'accounts': [
            {'profileId': 2, 'name': '업무 계정', 'plan': 'Max 5x', 'isCurrent': False, 'attributedTokens': 19_629_576.4,
             'devices': ['이 Ubuntu PC'],
             'fiveHour': {'state': 'ok', 'ratePerMillion': 18.4, 'deltaPercent': 361, 'tokens': 19_620_000, 'windows': 2,
                          'runs': 4, 'untrackedPercent': 0, 'planChanged': True},
             'weekly': {'state': 'collecting', 'ratePerMillion': None, 'deltaPercent': 2, 'tokens': 19_620_000, 'windows': 1,
                        'runs': 4, 'untrackedPercent': 0, 'planChanged': False}},
            {'profileId': 1, 'name': '개인 계정', 'plan': 'Max 20x', 'isCurrent': True, 'attributedTokens': 52_300_000,
             'devices': ['개발 서버', '이 Ubuntu PC'],
             'fiveHour': {'state': 'ok', 'ratePerMillion': 4.62, 'deltaPercent': 240, 'tokens': 51_948_000, 'windows': 3,
                          'runs': 12, 'untrackedPercent': 4, 'planChanged': False},
             'weekly': {'state': 'low', 'ratePerMillion': 0.27, 'deltaPercent': 14, 'tokens': 51_948_000, 'windows': 1,
                        'runs': 12, 'untrackedPercent': 0, 'planChanged': False}},
        ],
        'idleAccounts': ['실험 계정'],
    },
}
RESULT = {'collectedAt': '2026-10-25T03:12:00.1234567+00:00', 'estimatedCostUsd': 1487.62, 'view': VIEW, 'devices': []}
HIDDEN = {'state': 'hidden', 'ratePerMillion': None, 'deltaPercent': 0, 'tokens': 0, 'windows': 0, 'runs': 0,
          'untrackedPercent': 0, 'planChanged': False}


def setUpModule():
    # Times read in Korea, where the logs these figures follow were written; a POSIX zone needs no tzdata.
    global _zone
    _zone = os.environ.get('TZ')
    os.environ['TZ'] = 'KST-9'
    time.tzset()


def tearDownModule():
    if _zone is None:
        os.environ.pop('TZ', None)
    else:
        os.environ['TZ'] = _zone
    time.tzset()


def variant(change):
    view = copy.deepcopy(VIEW)
    change(view)
    return view


class NumberFormatTests(unittest.TestCase):
    def test_tokens_read_like_the_windows_page(self):
        cases = [(0, '0'), (999, '999'), (9_999, '9,999'), (10_000, '10.0K'), (12_345, '12.3K'), (99_999, '100.0K'),
                 (100_000, '100K'), (398_049.4, '398K'), (999_999, '1000K'), (1_000_000, '1.00M'), (48_620_000, '48.62M'),
                 (51_948_000, '51.95M'), (1_234_567_890, '1.23B'), (1_235_000_000, '1.24B'), (None, '0')]
        for count, expected in cases:
            with self.subTest(count=count):
                self.assertEqual(expected, tv.tokens(count))

    def test_dollars_keep_three_significant_figures(self):
        cases = [(4652.4, '$4,652'), (1487.62, '$1,488'), (100.5, '$100'), (101.5, '$102'), (49.6, '$49.6'),
                 (15.35, '$15.4'), (1.32, '$1.32'), (1.005, '$1.01'), (0, '$0.00')]
        for amount, expected in cases:
            with self.subTest(amount=amount):
                self.assertEqual(expected, tv.dollars(amount))

    def test_rates_never_read_zero(self):
        cases = [(0, '0%p'), (-1, '0%p'), (0.0123, '0.012%p'), (0.27, '0.27%p'), (0.999, '1.00%p'), (1, '1.0%p'),
                 (4.62, '4.6%p'), (9.96, '10.0%p'), (10, '10%p'), (18.4, '18%p'), (18.5, '18%p'), (19.5, '20%p')]
        for rate, expected in cases:
            with self.subTest(rate=rate):
                self.assertEqual(expected, tv.rate_text(rate))

    def test_shares_points_and_elapsed_time(self):
        self.assertEqual(['0%', '0.1% 미만', '0.4%', '1.0%', '2%', '4%', '96%', '100%'],
                         [tv.share_text(v) for v in [0, 0.0005, 0.004, 0.0096, 0.025, 0.0381, 0.9614, 1]])
        self.assertEqual(['240', '2.5', '0.3', '1,234', '1,234.6'], [tv.points(v) for v in [240, 2.5, 0.25, 1234, 1234.56]])
        self.assertEqual(['1분', '30분', '1시간', '24시간', '119시간 40분'],
                         [tv.elapsed({'elapsedHours': hours}) for hours in [1 / 60, 0.5, 1, 24, 119 + 2 / 3]])
        self.assertEqual('720시간', tv.elapsed({}))


class PeriodTests(unittest.TestCase):
    def test_presets_and_direct_hours(self):
        self.assertEqual(('1시간', '5시간', '24시간', '7일', '30일', '직접'), tv.PERIOD_LABELS)
        self.assertEqual([0, 2, 4, 5, 5], [tv.period_index(h, False) for h in (1, 24, 720, 36, 721)])
        self.assertEqual(5, tv.period_index(24, True))
        self.assertEqual([1, 168, None, None], [tv.preset_hours(i) for i in (0, 3, 5, -1)])
        self.assertEqual([1, 1, 36, 720, None, None], [tv.clamp_hours(v) for v in (0, 1.4, 36.2, 9999, float('nan'), '24')])

    def test_range_line_and_clipped_note(self):
        self.assertEqual('10월 24일 12:12 – 10월 25일 12:12', tv.range_text(VIEW))
        self.assertIsNone(tv.clipped_note(VIEW))
        self.assertEqual('10월 24일 12:12 – 10월 25일 12:12', tv.period_subtitle(VIEW))
        hour = variant(lambda v: v.update(hours=1, startedAt='2026-10-25T02:12:00+00:00', elapsedHours=1.0))
        self.assertEqual('10월 25일 11:12 – 12:12', tv.range_text(hour))
        week = variant(lambda v: v.update(hours=168, startedAt='2026-10-18T03:12:00+00:00', clipped=True, elapsedHours=119 + 2 / 3))
        self.assertEqual('기록이 10월 20일 12:32부터 있어 119시간 40분으로 나눴습니다.', tv.clipped_note(week))
        self.assertEqual('10월 18일 12:12 – 10월 25일 12:12\n기록이 10월 20일 12:32부터 있어 119시간 40분으로 나눴습니다.',
                         tv.period_subtitle(week))
        self.assertEqual('', tv.range_text({}))


class ThroughputTests(unittest.TestCase):
    def test_hourly_figures_and_active_hours(self):
        model = tv.throughput(VIEW)
        self.assertEqual('경과 24시간 기준', model.meta)
        self.assertEqual([('전체', '81.23M', '토큰/시간', True), ('입력', '80.84M', None, False), ('출력', '398K', None, False),
                          ('캐시 읽기 제외', '3.52M', None, False)],
                         [(f.title, f.value, f.unit, f.large) for f in model.figures])
        self.assertEqual('요청이 있던 21시간만 치면 시간당 전체 92.84M · 출력 455K입니다.', model.note)

    def test_compositions_of_input_and_output(self):
        inputs, outputs = tv.throughput(VIEW).compositions
        self.assertEqual(['캐시 읽기 77.72M · 96%', '캐시 쓰기 3.08M · 4%', '캐시 없는 입력 39.2K · 0.1% 미만'],
                         [part.legend for part in inputs.parts])
        self.assertEqual(['major', 'minor', 'none'], [part.tone for part in inputs.parts])
        self.assertEqual('입력 구성: 캐시 읽기 96%, 캐시 쓰기 4%, 캐시 없는 입력 0.1% 미만', inputs.spoken)
        self.assertEqual(['응답 226K · 57%', '추론 172K · 43%'], [part.legend for part in outputs.parts])

    def test_notes_for_a_quiet_or_a_busy_period(self):
        quiet = variant(lambda v: v.update(activeHours=0, throughput={}))
        self.assertEqual('이 기간에는 기록이 없습니다.', tv.throughput(quiet).note)
        self.assertEqual(['0%', '0%'], [p.legend.split(' · ')[1] for p in tv.throughput(quiet).compositions[1].parts])
        busy = variant(lambda v: v.update(activeHours=24))
        self.assertEqual('기간 내내 요청이 있었습니다.', tv.throughput(busy).note)
        # A clipped period of 2시간 30분 spans three hours.
        clipped = variant(lambda v: v.update(activeHours=3, elapsedHours=2.5))
        self.assertEqual('기간 내내 요청이 있었습니다.', tv.throughput(clipped).note)

    def test_bars_fill_with_visible_parts_or_draw_against_a_track(self):
        segments = tv.bar_segments([(0.96, 'major'), (0.038, 'minor'), (0.002, 'none')], 202)
        self.assertEqual(['major', 'minor'], [tone for _, _, tone in segments])
        self.assertAlmostEqual(200 * 0.96 / 0.998, segments[0][1])
        self.assertAlmostEqual(202, segments[-1][0] + segments[-1][1])
        # A part too small to see stays in the legend only, and the rest fill the bar.
        (only,) = tv.bar_segments([(0.996, 'major'), (0.004, 'minor')], 100)
        self.assertEqual((0.0, 'major'), (only[0], only[2]))
        self.assertAlmostEqual(100, only[1])
        self.assertEqual([(0.0, 80.0, 'track')], tv.bar_segments([(0, 'major')], 80))
        self.assertEqual([(0.0, 96.0, 'track'), (0.0, 48.0, 'major')], tv.bar_segments([(0.5, 'major')], 96, track=True))
        self.assertEqual([(0.0, 96.0, 'track')], tv.bar_segments([(0, 'major')], 96, track=True))
        self.assertEqual([], tv.bar_segments([(1, 'major')], 0))


class BurnTests(unittest.TestCase):
    def test_current_account_first_with_ok_low_and_collecting_cells(self):
        model = tv.burn(VIEW)
        self.assertIsNone(model.waiting)
        self.assertEqual(('fiveHour', 'weekly'), model.shown)
        self.assertEqual(['개인 계정', '업무 계정'], [line.name for line in model.accounts])
        mine, work = model.accounts
        self.assertTrue(mine.is_current)
        self.assertEqual('Max 20x · 52.30M 토큰', mine.detail)
        self.assertEqual('Max 5x · 19.63M 토큰', work.detail)
        five, week = mine.cells
        self.assertEqual(('5시간 · 1M 토큰당', 'ok', '4.6%p', '100% ≈ 21.65M 토큰', None),
                         (five.title, five.state, five.value, five.caption, five.tooltip))
        self.assertEqual(('주간 · 1M 토큰당', 'low', '약 0.27%p', '참고용 · 변화 14%p', tv.ROUGH_TIP),
                         (week.title, week.state, week.value, week.caption, week.tooltip))
        self.assertEqual(('ok', '18%p', '100% ≈ 5.43M 토큰'), (work.cells[0].state, work.cells[0].value, work.cells[0].caption))
        self.assertEqual(('collecting', '모으는 중', '변화 2%p · 3%p부터 보입니다'),
                         (work.cells[1].state, work.cells[1].value, work.cells[1].caption))
        self.assertEqual('5시간 · 1M 토큰당 4.6%p, 100% ≈ 21.65M 토큰, 주간 · 1M 토큰당 약 0.27%p, 참고용 · 변화 14%p, 사용 중',
                         mine.spoken)

    def test_evidence_says_how_each_rate_was_made(self):
        mine, work = tv.burn(VIEW).accounts
        self.assertEqual((('5시간', '창 3개 · 사용률 +240%p ÷ 51.95M 토큰'), ('주간', '창 1개 · 사용률 +14%p ÷ 51.95M 토큰'),
                          ('장치', '개발 서버, 이 Ubuntu PC'), ('뺀 구간', '토큰 기록 없이 오른 5시간 4%p')), mine.evidence)
        self.assertEqual(('뺀 구간', '요금제가 바뀌기 전 기록'), work.evidence[-1])

    def test_weighted_basis_reads_converted_tokens(self):
        model = tv.burn(variant(lambda v: v.update(basis='weighted')))
        self.assertEqual(('환산 1M 토큰당', ' 환산 토큰', '요금 환산'), (model.per, model.word, model.basis.label))
        five = model.accounts[0].cells[0]
        self.assertEqual(('5시간 · 환산 1M 토큰당', '100% ≈ 21.65M 환산 토큰'), (five.title, five.caption))
        self.assertEqual('Max 20x · 52.30M 환산 토큰', model.accounts[0].detail)
        self.assertTrue(model.notes[0].startswith('이 기간 요금 환산 토큰 84.41M 중 85%'))
        total = tv.burn(variant(lambda v: v.update(basis='total')))
        self.assertEqual(('1M 토큰당', ' 토큰', '전체 토큰'), (total.per, total.word, total.basis.short))

    def test_hidden_quota_drops_its_column_and_lines(self):
        def hide(view):
            view['burn']['showFiveHour'] = False
            for account in view['burn']['accounts']:
                account['fiveHour'] = dict(HIDDEN)
        model = tv.burn(variant(hide))
        self.assertEqual(('weekly',), model.shown)
        self.assertEqual([['weekly'], ['weekly']], [[cell.key for cell in line.cells] for line in model.accounts])
        self.assertEqual(['주간', '장치'], [label for label, _ in model.accounts[0].evidence])

        # With both turned off there is nothing to work out, not even the wait for readings.
        def neither(view):
            hide(view)
            view['burn']['showWeekly'] = False
            view['burn']['historyStartedAt'] = None
        none = tv.burn(variant(neither))
        self.assertEqual(((), (), None), (none.shown, none.accounts, none.waiting))

    def test_notes_name_what_no_rate_can_hold(self):
        def apart(view):
            view['burn'].update(attributedTokens=0.004 * view['burn']['basisTokens'], unregisteredTokens=2_500_000, unpricedTokens=1_200_000)
        notes = tv.burn(variant(apart)).notes
        self.assertIn('0.4%를 계정에 연결해 계산했습니다.', notes[0])
        self.assertIn('등록하지 않은 계정으로 쓴 2.50M 토큰은 어느 계정의 한도에도 넣지 않았습니다.', notes)
        self.assertIn('요금을 모르는 모델의 1.20M 토큰은 환산할 수 없어 그 구간을 뺐습니다.', notes)

    def test_waiting_until_the_first_rates(self):
        def collecting(since):
            def change(view):
                view['burn']['historyStartedAt'] = since
                for account in view['burn']['accounts']:
                    for key in ('fiveHour', 'weekly'):
                        account[key].update(state='collecting', ratePerMillion=None)
            return variant(change)
        fresh = tv.burn(collecting('2026-10-24T09:40:00+00:00'))
        self.assertEqual('한도 기록을 모으고 있습니다', fresh.waiting.title)
        self.assertIn('5분마다 계정별 5시간·주간 사용률을 기록합니다.', fresh.waiting.body)
        self.assertEqual('기록 시작 · 10월 24일 18:40 · 처리량과 합계는 그 전 기록으로도 바로 보입니다.', fresh.waiting.caption)
        self.assertEqual(((), ()), (fresh.accounts, fresh.notes))
        none = tv.burn(collecting(None))
        self.assertEqual('아직 기록이 없습니다 · 처리량과 합계는 지난 기록으로 바로 보입니다.', none.waiting.caption)
        # Readings older than the period with nothing rated yet show every account collecting.
        old = tv.burn(collecting('2026-10-20T03:36:00+00:00'))
        self.assertIsNone(old.waiting)
        self.assertEqual({'collecting'}, {cell.state for line in old.accounts for cell in line.cells})

    def test_coverage_caveat_and_idle_notes(self):
        self.assertEqual(('이 기간 캐시 읽기 제외 토큰 84.41M 중 85%를 계정에 연결해 계산했습니다. '
                          '앱이 꺼져 있었거나 어느 계정이 썼는지 모르는 시간의 토큰은 뺐습니다.',
                          tv.CAVEAT, '실험 계정은 이 기간에 쓴 기록이 없습니다.'), tv.burn(VIEW).notes)
        def whole(view):
            view['burn'].update(attributedTokens=84_100_000, idleAccounts=['실험 계정', '보조 계정'])
        self.assertEqual(('이 기간 캐시 읽기 제외 토큰 84.41M 중 99%를 계정에 연결해 계산했습니다.', tv.CAVEAT,
                          '실험·보조 계정은 이 기간에 쓴 기록이 없습니다.'), tv.burn(variant(whole)).notes)
        def mixed(view):
            view['burn'].update(basisTokens=0, idleAccounts=['lab@example.com', '보조 계정'])
        self.assertEqual((tv.CAVEAT, '이 기간에 쓴 기록이 없는 계정: lab@example.com, 보조 계정'), tv.burn(variant(mixed)).notes)


class TotalsAndDevicesTests(unittest.TestCase):
    def test_period_totals(self):
        self.assertEqual([('전체 토큰', '1.95B', '토큰', True), ('추정 비용 (USD)', '$1,488', None, True), ('입력', '1.94B', None, False),
                          ('캐시 읽기', '1.87B', None, False), ('출력', '9.55M', None, False)],
                         [(f.title, f.value, f.unit, f.large) for f in tv.totals(VIEW)])

    def test_device_rows_and_their_models(self):
        local, server = tv.devices(VIEW)
        self.assertEqual(('local', '이 Ubuntu PC', 'Ubuntu · 집계 완료', '1.78B 토큰 · $1,360', False),
                         (local.id, local.name, local.detail, local.amount, local.failed))
        self.assertEqual(('SSH · 집계 완료', '167.67M 토큰 · $128'), (server.detail, server.amount))
        opus, sonnet = local.models
        self.assertEqual(('claude-opus-5-5', None, 'claude-opus-5-5', 1.0, '1.57B', '$1,302'),
                         (opus.name, opus.tiers, opus.tooltip, opus.fraction, opus.tokens, opus.cost))
        self.assertAlmostEqual(213_835_376 / 1_568_126_093, sonnet.fraction)
        self.assertEqual(('213.84M', '$57.6', 'claude-sonnet-5-5 213.84M 토큰, $57.6'), (sonnet.tokens, sonnet.cost, sonnet.spoken))
        self.assertEqual(('default, priority', 'claude-opus-5-5 · default, priority'), (server.models[0].tiers, server.models[0].tooltip))
        self.assertEqual('$15.4', server.models[1].cost)
        self.assertIsNone(tv.device_warning(VIEW))

    def test_unpriced_models_and_devices_without_minutes(self):
        def change(view):
            view['devices'][0]['models'][1]['isPriced'] = False
            view['devices'][1].update(hasMinutes=False, totalTokens=0, estimatedCostUsd=0, models=[])
        local, server = tv.devices(variant(change))
        self.assertEqual('요금 미상', local.models[1].cost)
        self.assertEqual(('SSH · 기간별 기록 없음 · 장치를 다시 설치하면 보입니다', '—', False, ()),
                         (server.detail, server.amount, server.failed, server.models))

    def test_a_device_not_read_is_named_and_the_others_counted(self):
        def offline(view):
            view['devices'][1].update(isReachable=False, error='사용량 수집을 완료하지 못했습니다.', hasMinutes=False,
                                      totalTokens=0, estimatedCostUsd=0, models=[])
        view = variant(offline)
        self.assertEqual(('개발 서버 기록을 읽지 못했습니다',
                          '처리량과 합계는 나머지 장치만 셌습니다. 그 장치가 계정을 쓰던 시간은 소모율에서 뺍니다.'), tv.device_warning(view))
        server = tv.devices(view)[1]
        self.assertEqual((True, '사용량 수집을 완료하지 못했습니다.', '—'), (server.failed, server.detail, server.amount))
        unreachable = variant(lambda v: v['devices'][0].update(isReachable=False))
        self.assertEqual('연결 실패 · 집계하지 못했습니다', tv.devices(unreachable)[0].detail)
        self.assertEqual('이 Ubuntu PC 기록을 읽지 못했습니다', tv.device_warning(unreachable)[0])
        both = variant(lambda v: [d.update(isReachable=False) for d in v['devices']])
        self.assertEqual('이 Ubuntu PC, 개발 서버 기록을 읽지 못했습니다', tv.device_warning(both)[0])

    def test_collected_line(self):
        self.assertEqual('10월 25일 12:12 집계 · 요금을 모르는 모델은 비용에서 빠집니다.', tv.collected_note(RESULT, VIEW))
        self.assertEqual('10월 25일 12:12 집계 · 요금을 모르는 모델은 비용에서 빠집니다.', tv.collected_note({}, VIEW))
        self.assertEqual('', tv.collected_note(None, {}))


class BasisTests(unittest.TestCase):
    def test_cache_reads_are_left_out_unless_another_basis_is_chosen(self):
        self.assertEqual('uncached', tv.DEFAULT_BASIS)
        self.assertEqual(['캐시 읽기 제외', '전체 토큰', '요금 환산'], [item.label for item in tv.BASES])
        self.assertEqual(['uncached', 'total', 'weighted', 'uncached', 'uncached'],
                         [tv.normalize_basis(v) for v in ('uncached', 'total', 'weighted', 'other', None)])
        self.assertEqual(('캐시 읽기 제외', 0), (tv.basis('nope').label, tv.basis_index('nope')))
        self.assertEqual(2, tv.basis_index('weighted'))
        for item in tv.BASES:
            self.assertTrue(item.description.endswith('니다.'), item.description)


class SessionTests(unittest.TestCase):
    def test_first_open_uses_the_period_chosen_last_time(self):
        fresh = tv.TokenSession(None, None)
        self.assertEqual(((720, 'uncached'), False, None), (fresh.choice, fresh.custom, fresh.view))
        remembered = tv.TokenSession(None, {'tokenPeriodHours': 36, 'tokenRateBasis': 'weighted'})
        self.assertEqual(((36, 'weighted'), True), (remembered.choice, remembered.custom))
        ticket, params = remembered.start_load()
        self.assertEqual((1, {}), (ticket, params))
        self.assertTrue(remembered.loading)
        shown = tv.TokenSession(RESULT, {'tokenPeriodHours': 5})
        self.assertEqual(((24, 'uncached'), False), (shown.choice, shown.custom))
        self.assertEqual((1, {'hours': 24, 'basis': 'uncached'}), shown.start_load())

    def test_a_reply_after_a_newer_choice_is_ignored(self):
        session = tv.TokenSession(RESULT)
        week = session.choose(hours=168)
        self.assertEqual((1, {'hours': 168, 'basis': 'uncached'}), week)
        self.assertIsNone(session.choose(hours=168))
        weighted = session.choose(basis='weighted')
        self.assertEqual((2, {'hours': 168, 'basis': 'weighted'}), weighted)
        late = variant(lambda v: v.update(hours=168))
        self.assertFalse(session.finish_view(week[0], {'collectedAt': RESULT['collectedAt'], 'view': late}, None))
        self.assertEqual(24, session.view['hours'])
        self.assertEqual((168, 'weighted'), session.choice)
        newest = variant(lambda v: v.update(hours=168, basis='weighted'))
        self.assertTrue(session.finish_view(weighted[0], {'collectedAt': '2026-10-25T03:40:00+00:00', 'view': newest}, None))
        self.assertEqual(('weighted', '2026-10-25T03:40:00+00:00'), (session.view['basis'], session.result['collectedAt']))
        self.assertEqual((168, 'weighted'), session.choice)
        self.assertEqual(24, RESULT['view']['hours'])

    def test_a_failed_cut_keeps_the_figures_and_says_which_they_are(self):
        session = tv.TokenSession(RESULT)
        ticket, _ = session.choose(hours=5)
        self.assertTrue(session.finish_view(ticket, None, RuntimeError('백그라운드 서비스에 연결하지 못했습니다.')))
        self.assertEqual('백그라운드 서비스에 연결하지 못했습니다.', session.error)
        self.assertEqual((24, 'uncached'), session.choice)
        ticket, _ = session.choose(hours=5)
        self.assertTrue(session.finish_view(ticket, {'collectedAt': RESULT['collectedAt'], 'view': variant(lambda v: v.update(hours=5))}, None))
        self.assertIsNone(session.error)

    def test_a_new_reading_behind_a_newer_choice_is_cut_again(self):
        session = tv.TokenSession(RESULT)
        load, _ = session.start_load()
        cut = session.choose(hours=5)
        self.assertEqual('again', session.finish_load(load, dict(RESULT, collectedAt='2026-10-25T04:00:00+00:00'), None))
        self.assertFalse(session.loading)
        self.assertEqual('2026-10-25T03:12:00.1234567+00:00', session.result['collectedAt'])
        again = session.recut()
        self.assertEqual((cut[0] + 1, {'hours': 5, 'basis': 'uncached'}), again)
        self.assertFalse(session.finish_view(cut[0], {'view': variant(lambda v: v.update(hours=5))}, None))
        self.assertTrue(session.finish_view(again[0], {'view': variant(lambda v: v.update(hours=5))}, None))
        load, _ = session.start_load()
        session.choose(hours=24)
        self.assertEqual('stale', session.finish_load(load, None, RuntimeError('늦은 오류')))
        self.assertIsNone(session.error)

    def test_a_failed_reading_puts_the_choice_back_on_the_figures(self):
        # A cut asked for, then a new reading that drops its reply and fails: the period shown is the figures' own.
        for failure in (RuntimeError('세션 기록을 읽지 못했습니다.'), None):
            with self.subTest(failure=failure):
                session = tv.TokenSession(RESULT)
                cut, _ = session.choose(hours=168, basis='weighted')
                load, params = session.start_load()
                self.assertEqual({'hours': 168, 'basis': 'weighted'}, params)
                self.assertFalse(session.finish_view(cut, {'view': variant(lambda v: v.update(hours=168, basis='weighted'))}, None))
                self.assertEqual('failed', session.finish_load(load, {'devices': []} if failure is None else None, failure))
                self.assertEqual(((24, 'uncached'), 24), (session.choice, session.view['hours']))
                self.assertEqual(tv.UNREADABLE if failure is None else str(failure), session.error)
        # Without figures yet, the choice remembered from the settings stays.
        fresh = tv.TokenSession(None, {'tokenPeriodHours': 36})
        ticket, _ = fresh.start_load()
        self.assertEqual('failed', fresh.finish_load(ticket, None, RuntimeError('x')))
        self.assertEqual(((36, 'uncached'), True), (fresh.choice, fresh.custom))

    def test_reading_results(self):
        session = tv.TokenSession()
        ticket, _ = session.start_load()
        self.assertEqual('failed', session.finish_load(ticket, None, RuntimeError('세션 기록을 읽지 못했습니다.')))
        self.assertEqual('세션 기록을 읽지 못했습니다.', session.error)
        ticket, _ = session.start_load()
        self.assertEqual('failed', session.finish_load(ticket, {'devices': []}, None))
        self.assertEqual(tv.UNREADABLE, session.error)
        ticket, _ = session.start_load()
        week = variant(lambda v: v.update(hours=36))
        self.assertEqual('shown', session.finish_load(ticket, dict(RESULT, view=week), None))
        self.assertEqual(((36, 'uncached'), True, None), (session.choice, session.custom, session.error))

    def test_direct_hours_stay_direct_until_a_preset_is_picked(self):
        session = tv.TokenSession(RESULT)
        session.custom = True
        self.assertIsNone(session.choose(hours=24.4))
        self.assertIsNone(session.choose(hours=24))
        ticket, params = session.choose(hours=900)
        self.assertEqual({'hours': 720, 'basis': 'uncached'}, params)
        self.assertTrue(session.finish_view(ticket, {'view': variant(lambda v: v.update(hours=720))}, None))
        self.assertEqual(5, tv.period_index(session.choice[0], session.custom))
        session.custom = False
        self.assertEqual(4, tv.period_index(session.choice[0], session.custom))


if __name__ == '__main__':
    unittest.main()
