// Credential-free presentation model for the five-hour and weekly quotas.
// Usage key in the snapshot, title, settings key that shows it.
export const QUOTAS = [['fiveHour', '5시간', 'claudeFiveHour'], ['weekly', '주간', 'claudeWeekly']];
const TITLES = Object.fromEntries(QUOTAS.map(([key, title]) => [key, title]));
// Older than this, a value is shown as the previous one.
const STALE = 600000;
const pad = value => String(value).padStart(2, '0');

export function visibleQuotas(settings) {
    return QUOTAS.filter(([, , setting]) => settings?.[setting] !== false).map(([key]) => key);
}

// '0%' only when nothing is left and '100%' only when nothing is used; a sliver shows as such.
export function percent(remaining) {
    if (remaining === null) return '—';
    // Rounded up to a tenth, so the smallest sliver never reads as 0%.
    const sliver = Math.ceil(Number((remaining * 10).toFixed(6))) / 10;
    if (remaining > 0 && sliver < 1) return `${sliver.toFixed(1)}%`;
    if (remaining > 99 && remaining < 100) return '99%';
    return `${Math.round(remaining)}%`;
}

function remainingOf(window) {
    const raw = window?.remainingPercent ?? (typeof window?.usedPercent === 'number' ? 100 - window.usedPercent : null);
    return typeof raw === 'number' && Number.isFinite(raw) ? Math.max(0, Math.min(100, raw)) : null;
}

export function quota(window, key, now = Date.now(), previous = false) {
    const title = TITLES[key];
    const remaining = remainingOf(window);
    const shown = value => ({key, title, remaining: value, value: percent(value)});
    if (remaining === null) return {...shown(null), tone: 'muted', caption: '정보 없음'};
    const resets = Date.parse(window.resetsAt);
    // Past its reset time the whole quota is available again until it is next used.
    if (Number.isFinite(resets) && resets <= now && !previous) return {...shown(100), tone: 'healthy', caption: '초기화됨'};
    const tone = previous ? 'muted' : remaining <= 10 ? 'critical' : remaining <= 25 ? 'warning' : 'healthy';
    let caption;
    if (!Number.isFinite(resets)) caption = remaining >= 100 ? '사용 전' : '초기화 시각 미정';
    else {
        const date = new Date(resets);
        caption = key === 'fiveHour' ? `${pad(date.getHours())}:${pad(date.getMinutes())} 초기화` : `${date.getMonth() + 1}월 ${date.getDate()}일 초기화`;
    }
    return {...shown(remaining), tone, caption};
}

export function usage(account, keys = visibleQuotas(), now = Date.now(), unavailable = false) {
    const data = account.usage ?? {};
    const updated = Date.parse(data.updatedAt);
    const known = keys.some(key => remainingOf(data[key]) !== null);
    const reason = unavailable ? '연결 실패' : account.needsLogin || account.isPending ? '재로그인 필요' : data.error ? '조회 실패' :
        known && (!Number.isFinite(updated) || now - updated > STALE) ? '갱신 지연' : '';
    const status = reason ? reason + (known ? ' · 이전 수치' : '') : account.usage ? '' : '아직 조회되지 않음';
    return {quotas: keys.map(key => quota(data[key], key, now, Boolean(reason))), status,
        statusTone: reason && reason !== '갱신 지연' ? 'warning' : 'muted', updated};
}

export function switchable(account, snapshot, pending = false) {
    return !pending && !snapshot.isBusy && !snapshot.error && snapshot.operation?.state !== 'recoveryRequired' &&
        !account.needsLogin && !account.isPending && account.id !== snapshot.activeProfileId;
}

export function panelUsage(snapshot, now = Date.now(), unavailable = false) {
    const keys = visibleQuotas(snapshot.settings);
    const account = snapshot.accounts.find(item => item.id === snapshot.activeProfileId);
    if (!account) return {text: keys.map(key => `${TITLES[key]} —`).join(' · '), description: 'Claude SyncBar, 이 PC의 활성 계정 정보 없음'};
    const {quotas} = usage(account, keys, now, unavailable);
    // The compact panel has no room to explain an old value; keep it in the popup.
    const value = item => item.tone === 'muted' ? '—' : item.value;
    return {text: quotas.map(item => `${item.title} ${value(item)}`).join(' · '),
        description: `Claude SyncBar, 이 PC ${quotas.map(item => `${item.title} 잔여량 ${value(item)}, ${item.caption}`).join(', ')}`};
}

export function subtitle(snapshot) {
    return snapshot.settings?.autoSwitchEnabled ? '이 PC · 자동 전환 켜짐' : '이 PC';
}

// What this PC still needs from the user after a switch, such as a first desktop sign-in.
export function localNotice(snapshot) {
    const local = snapshot.devices?.find(device => device.id === 'local');
    return local?.reconnectionRequired && local.detail || '';
}

// The smart switch applies to every active device, like the app's apply button.
export async function smartSwitch(call, requestId, onDesktopChange) {
    const smart = await call('smart.preview', {});
    if (!smart.preview) return {message: smart.message};
    const preview = smart.preview;
    if (!preview.canApply) throw new Error(preview.blockingReason || '지금은 전환할 수 없습니다. 앱에서 상태를 확인해 주세요.');
    if (preview.profileId !== smart.profileId) throw new Error('스마트 전환 대상을 확인하지 못했습니다.');
    if (preview.desktopNotice) onDesktopChange?.(preview.desktopNotice);
    return {message: smart.message, operation: await call('apply', {previewId: preview.previewId, requestId})};
}

export async function switchLocal(call, profileId, requestId, onDesktopChange) {
    const preview = await call('preview', {profileId, deviceId: 'local'});
    const targets = preview.targets.filter(target => target.included);
    if (!preview.canApply) throw new Error(preview.blockingReason || '전환할 수 없습니다. 앱에서 상태를 확인해 주세요.');
    // Fail closed if an incompatible backend includes another machine.
    if (targets.length !== 1 || targets[0].id !== 'local' || preview.profileId !== profileId)
        throw new Error('이 PC에만 적용되는 전환인지 확인하지 못했습니다.');
    if (preview.desktopNotice) onDesktopChange?.(preview.desktopNotice);
    return call('apply', {previewId: preview.previewId, requestId});
}
