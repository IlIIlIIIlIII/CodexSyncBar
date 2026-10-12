// Credential-free presentation model. Only the weekly quota is supported.
export function weekly(account, now = Date.now(), unavailable = false) {
    const usage = account.usage ?? {}, window = usage.weekly ?? {};
    const raw = window.remainingPercent ?? (typeof window.usedPercent === 'number' ? 100 - window.usedPercent : null);
    const remaining = typeof raw === 'number' && Number.isFinite(raw) ? Math.max(0, Math.min(100, raw)) : null;
    const updated = Date.parse(usage.updatedAt), resets = Date.parse(window.resetsAt);
    const date = new Date(resets);
    const reset = Number.isFinite(resets) ? `${date.getMonth() + 1}월 ${date.getDate()}일 ${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')} 초기화` : '초기화 시각 확인 필요';
    let status = unavailable ? '연결 실패' : account.needsLogin || account.isPending ? '재로그인 필요' : usage.error ? '조회 실패' :
        remaining !== null && (!Number.isFinite(updated) || now - updated > 600000) ? '갱신 지연' :
        remaining !== null && resets <= now ? '초기화 확인 필요' : '';
    const tone = status || remaining === null ? 'muted' : remaining <= 25 ? 'warning' : 'healthy';
    if (status && remaining !== null) status += ' · 이전 수치';
    if (!status && remaining === null) status = '주간 한도 정보 없음';
    return {remaining, value: remaining === null ? '—' : `${Math.round(remaining)}%`, tone,
        caption: remaining === null ? status : status || reset, reset, updated};
}

export function switchable(account, snapshot, pending = false) {
    return !pending && !snapshot.isBusy && !snapshot.error && snapshot.operation?.state !== 'recoveryRequired' &&
        !account.needsLogin && !account.isPending && account.id !== snapshot.activeProfileId;
}

export function panelUsage(snapshot, now = Date.now(), unavailable = false) {
    const account = snapshot.accounts.find(item => item.id === snapshot.activeProfileId);
    if (!account) return {text: '주간 —', description: 'Codex SyncBar, 이 PC의 활성 계정 정보 없음'};
    const quota = weekly(account, now, unavailable);
    // The compact panel has no room to explain an old value; keep it in the popup.
    const value = quota.tone === 'muted' ? '—' : quota.value;
    return {text: `주간 ${value}`, description: `Codex SyncBar, 이 PC 주간 잔여량 ${value}, ${quota.caption}`};
}

export async function switchLocal(call, profileId, requestId) {
    const preview = await call('preview', {profileId, deviceId: 'local'});
    const targets = preview.targets.filter(target => target.included);
    if (!preview.canApply) throw new Error(preview.blockingReason || '전환할 수 없습니다. 앱에서 상태를 확인해 주세요.');
    // Fail closed if an incompatible backend includes another machine.
    if (targets.length !== 1 || targets[0].id !== 'local' || preview.profileId !== profileId)
        throw new Error('이 PC에만 적용되는 전환인지 확인하지 못했습니다.');
    return call('apply', {previewId: preview.previewId, requestId});
}
