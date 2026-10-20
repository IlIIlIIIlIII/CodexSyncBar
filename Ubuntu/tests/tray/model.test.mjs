import assert from 'node:assert/strict';
import {quota, usage, percent, visibleQuotas, panelUsage, subtitle, switchable, switchLocal, smartSwitch, localNotice} from '../../gnome/model.js';
const now = Date.now();
const at = hours => new Date(now + hours * 3600000).toISOString();
const account = (weekly, fiveHour = 72) => ({id: 2, usage: {
    weekly: {remainingPercent: weekly, resetsAt: at(24)}, fiveHour: {remainingPercent: fiveHour, resetsAt: at(1.5)},
    updatedAt: new Date(now).toISOString()}});
const both = ['fiveHour', 'weekly'];
const shown = (item, keys = both, time = now, unavailable = false) => usage(item, keys, time, unavailable);
// Both quotas are read from their own entries, in display order.
assert.deepEqual(shown(account(68, 40)).quotas.map(item => [item.title, item.value]), [['5시간', '40%'], ['주간', '68%']]);
assert.equal(shown(account(0)).quotas[1].value, '0%');
assert.equal(shown(account(null)).quotas[1].value, '—');
assert.equal(shown(account(NaN)).quotas[1].caption, '정보 없음');
assert.equal(shown(account(68)).quotas[1].tone, 'healthy');
assert.equal(shown(account(20)).quotas[1].tone, 'warning');
assert.equal(shown(account(5)).quotas[1].tone, 'critical');
assert.match(shown(account(68)).quotas[0].caption, /^\d\d:\d\d 초기화$/);
assert.match(shown(account(68)).quotas[1].caption, /^\d+월 \d+일 초기화$/);
// A five-hour window nobody has used yet, and one whose reset time has passed.
assert.equal(quota({remainingPercent: 100, resetsAt: null}, 'fiveHour', now).caption, '사용 전');
assert.deepEqual([quota({usedPercent: 90, resetsAt: at(-0.1)}, 'fiveHour', now).value, quota({usedPercent: 90, resetsAt: at(-0.1)}, 'fiveHour', now).caption], ['100%', '초기화됨']);
// Account-level reasons are said once and mute every quota.
assert.equal(shown({...account(null, null), needsLogin: true}).status, '재로그인 필요');
assert.equal(shown({...account(68), needsLogin: true}).statusTone, 'warning');
assert.equal(shown(account(68), both, now + 700000).status, '갱신 지연 · 이전 수치');
assert.ok(shown(account(68), both, now + 700000).quotas.every(item => item.tone === 'muted'));
assert.equal(shown(account(68), both, now, true).status, '연결 실패 · 이전 수치');
assert.equal(shown({id: 3}).status, '아직 조회되지 않음');
assert.equal(subtitle({settings: {autoSwitchEnabled: true}}), '이 PC · 자동 전환 켜짐');
assert.equal(subtitle({}), '이 PC');
// '0%' and '100%' only when exact.
assert.deepEqual([0, 0.04, 0.2, 0.3, 0.96, 72.4, 99.6, 100, null].map(percent), ['0%', '0.1%', '0.2%', '0.3%', '1%', '72%', '99%', '100%', '—']);
assert.equal(panelUsage({accounts: [account(0.3, 99.7)], activeProfileId: 2}, now).text, '5시간 99% · 주간 0.3%');
// Settings choose which quotas are shown.
assert.deepEqual(visibleQuotas(), both);
assert.deepEqual(visibleQuotas({claudeFiveHour: false}), ['weekly']);
assert.deepEqual(visibleQuotas({claudeFiveHour: false, claudeWeekly: false}), []);
assert.equal(switchable(account(68), {activeProfileId: 2}), false);
assert.equal(switchable(account(68), {activeProfileId: 1, isBusy: true}), false);
assert.equal(switchable({...account(68), needsLogin: true}, {activeProfileId: 1}), false);
assert.equal(switchable(account(68), {activeProfileId: 1}), true);
const panelSnapshot = {accounts: [{...account(84, 30), id: 1}, account(68)], activeProfileId: 2};
assert.equal(panelUsage(panelSnapshot, now).text, '5시간 72% · 주간 68%');
assert.equal(panelUsage({...panelSnapshot, activeProfileId: 1}, now).text, '5시간 30% · 주간 84%');
assert.equal(panelUsage({...panelSnapshot, activeProfileId: 99}, now).text, '5시간 — · 주간 —');
assert.equal(panelUsage({...panelSnapshot, accounts: [account(0, 0)]}, now).text, '5시간 0% · 주간 0%');
assert.equal(panelUsage({...panelSnapshot, accounts: [account(100, 100)]}, now).text, '5시간 100% · 주간 100%');
assert.equal(panelUsage(panelSnapshot, now, true).text, '5시간 — · 주간 —');
assert.equal(panelUsage(panelSnapshot, now + 700000).text, '5시간 — · 주간 —');
assert.equal(panelUsage({...panelSnapshot, accounts: [{...account(68), needsLogin: true}]}, now).text, '5시간 — · 주간 —');
assert.equal(panelUsage({...panelSnapshot, settings: {claudeFiveHour: false}}, now).text, '주간 68%');
assert.equal(panelUsage({...panelSnapshot, settings: {claudeWeekly: false}}, now).text, '5시간 72%');
assert.match(panelUsage(panelSnapshot, now).description, /이 PC 5시간 잔여량 72%, \d\d:\d\d 초기화, 주간 잔여량 68%/);
const calls = [];
const call = async (method, params) => {
    calls.push([method, params]);
    return method === 'preview' ? {profileId: 2, previewId: 'p', canApply: true, targets: [{id: 'local', included: true}, {id: 'ssh:dev', included: false}]} : {id: 'o'};
};
await switchLocal(call, 2, 'request');
assert.deepEqual(calls, [['preview', {profileId: 2, deviceId: 'local'}], ['apply', {previewId: 'p', requestId: 'request'}]]);
let applied = false;
await assert.rejects(switchLocal(async method => {
    if (method === 'apply') applied = true;
    return {profileId: 2, canApply: true, targets: [{id: 'local', included: true}, {id: 'ssh:dev', included: true}]};
}, 2, 'request'), /이 PC/);
assert.equal(applied, false);
await assert.rejects(switchLocal(async () => ({canApply: false, targets: [], blockingReason: '복구 필요'}), 2, 'request'), /복구 필요/);
const order = [];
await switchLocal(async method => {
    order.push(method);
    return method === 'preview' ? {profileId: 2, previewId: 'p', canApply: true, desktopNotice: '데스크톱 재시작', targets: [{id: 'local', included: true}]} : {id: 'o'};
}, 2, 'request', notice => order.push(notice));
assert.deepEqual(order, ['preview', '데스크톱 재시작', 'apply']);
assert.equal(localNotice({devices: [{id: 'local', reconnectionRequired: true, detail: '로그인해 주세요.'}]}), '로그인해 주세요.');
assert.equal(localNotice({devices: [{id: 'local', reconnectionRequired: false, detail: '로그인해 주세요.'}, {id: 'ssh:dev', reconnectionRequired: true, detail: '다른 장치'}]}), '');
assert.equal(localNotice({}), '');
// The smart switch applies the service's choice to every active device, and only that choice.
const smartCalls = [];
const smartResult = await smartSwitch(async (method, params) => {
    smartCalls.push(method);
    return method === 'smart.preview'
        ? {found: true, profileId: 3, message: '계정3(으)로 전환합니다.', preview: {profileId: 3, previewId: 's', canApply: true, desktopNotice: '데스크톱 재시작',
            targets: [{id: 'local', included: true}, {id: 'ssh:dev', included: true}]}}
        : {id: 'o'};
}, 'request', notice => smartCalls.push(notice));
assert.deepEqual(smartCalls, ['smart.preview', '데스크톱 재시작', 'apply']);
assert.equal(smartResult.operation.id, 'o');
assert.deepEqual(await smartSwitch(async () => ({found: false, message: '없음'}), 'request'), {message: '없음'});
let smartApplied = false;
await assert.rejects(smartSwitch(async method => {
    if (method === 'apply') smartApplied = true;
    return {found: true, profileId: 3, preview: {profileId: 2, canApply: true, targets: []}};
}, 'request'), /대상/);
await assert.rejects(smartSwitch(async () => ({found: true, profileId: 3, preview: {profileId: 3, canApply: false, blockingReason: '복구 필요'}}), 'request'), /복구 필요/);
assert.equal(smartApplied, false);
console.log('Tray model/command assertions passed');
