#!/usr/bin/env python3
"""Synthetic visual fixture only; this is not the production integration backend."""
import argparse
import copy
import json
import os
from pathlib import Path
import signal
import socketserver


SCENARIO = 'default'

UPDATED = '2026-10-03T03:40:00Z'


def snapshot():
    accounts = []
    for identity, alias, session, weekly in [(1, '개인 계정', 74, 39), (2, '업무 계정', 72, 58)]:
        accounts.append({'id': identity, 'email': 'p***@example.com' if identity == 1 else 'w***@example.com', 'alias': alias,
                         'isPending': False, 'needsLogin': False,
                         'usage': {'session': {'usedPercent': 100-session, 'resetsAt': '2026-10-03T07:00:00Z'},
                                   'weekly': {'usedPercent': 100-weekly, 'resetsAt': '2026-10-06T00:00:00Z'},
                                   'resetCredits': 3, 'resetCreditExpirations': ['2026-10-10T03:40:00Z'],
                                   'updatedAt': UPDATED}})
    devices = [{'id': key, 'displayName': title, 'kind': kind, 'enabled': True, 'isReachable': True,
                'currentProfileId': 1, 'currentAccountLabel': '개인 계정', 'observedAt': UPDATED, 'status': 'ready'}
               for key, title, kind in [('local', '이 Ubuntu PC', 'local'), ('ssh:dev', '개발 서버', 'ssh'),
                                        ('ssh:build', '빌드 서버', 'ssh')]]
    if SCENARIO == 'overflow':
        accounts[1]['alias'] = '아주 긴 계정 표시 이름을 사용하는 업무 계정'
        accounts[1]['email'] = 'long-account-name***@department.example.com'
        devices += [dict(devices[1], id=f'ssh:qa-{i}', displayName=f'장치 {i} · 아주 긴 개발 서버 이름', currentAccountLabel='다른 계정 · 확인 필요') for i in range(4, 25)]
    return {'schemaVersion': 1, 'accounts': accounts, 'devices': devices, 'activeProfileId': 1,
            'isBusy': False, 'operation': None, 'configurationRevision': 'visual-fixture-1',
            'updatedAt': UPDATED, 'error': None, 'settings': {'fiveHour': True, 'codexWeekly': True},
            'isDemo': True}


class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        for line in self.rfile:
            request = json.loads(line)
            result = snapshot()
            if request['method'] == 'preview':
                selected = request.get('params', {}).get('profileId', 2)
                name = next(a['alias'] for a in result['accounts'] if a['id'] == selected)
                result = {'previewId': f'fixture-preview-{selected}', 'profileId': selected,
                          'configurationRevision': 'visual-fixture-1', 'createdAt': UPDATED,
                          'canApply': True, 'blockingReason': None,
                          'targets': [dict(copy.deepcopy(d), targetProfileId=selected, targetAccountLabel=name,
                                           action='reapply' if selected == 1 else 'switch', included=True)
                                      for d in result['devices']]}
            if request['method'] not in ('snapshot', 'preview'):
                response = {'id': request['id'], 'ok': False,
                            'error': {'code': 'visual_fixture', 'message': '시각 검증용 fixture입니다.'}}
            else:
                response = {'id': request['id'], 'ok': True, 'result': result}
            self.wfile.write(json.dumps(response, ensure_ascii=False).encode() + b'\n')
            self.wfile.flush()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--socket', required=True)
    parser.add_argument('--scenario', choices=['default', 'overflow'], default='default')
    arguments = parser.parse_args()
    SCENARIO = arguments.scenario
    path = Path(arguments.socket)
    if path.exists():
        parser.error('Use a new socket path in a private temporary directory.')
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(SystemExit(0)))
    try:
        with socketserver.ThreadingUnixStreamServer(str(path), Handler) as server:
            os.chmod(path, 0o600)
            server.serve_forever()
    finally:
        path.unlink(missing_ok=True)
