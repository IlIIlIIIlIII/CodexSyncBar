#!/usr/bin/env python3
"""Credential-free UI client contract tests; no window or desktop automation."""
import json
import os
from pathlib import Path
import socket
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Ubuntu/ui'))
from codex_syncbar.ipc import Client, ServiceError


class ReplyServer:
    def __init__(self, path, respond):
        self.socket = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.socket.bind(str(path))
        self.socket.listen(1)
        self.thread = threading.Thread(target=self.serve, args=(respond,), daemon=True)
        self.thread.start()

    def serve(self, respond):
        with self.socket:
            connection, _ = self.socket.accept()
            with connection, connection.makefile('rb') as stream:
                request = json.loads(stream.readline())
                response = respond(request)
                data = response if isinstance(response, bytes) else json.dumps(response).encode()
                connection.sendall(data + b'\n')

    def close(self):
        self.thread.join(timeout=3)
        self.socket.close()


class ClientContractTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='syncbar-client-')
        self.root = Path(self.temporary.name)
        self.path = self.root / 'control.sock'
        self.client = Client(str(self.path))

    def tearDown(self):
        self.client.close()
        self.temporary.cleanup()

    def request(self, respond):
        server = ReplyServer(self.path, respond)
        try:
            return self.client.call('snapshot', timeout=1)
        finally:
            server.close()

    def test_valid_response_is_returned(self):
        result = self.request(lambda r: {'id': r['id'], 'ok': True, 'result': {'activeProfileId': 2}})
        self.assertEqual({'activeProfileId': 2}, result)

    def test_wrong_response_id_is_rejected(self):
        with self.assertRaises(ServiceError) as failure:
            self.request(lambda r: {'id': 'unrelated', 'ok': True, 'result': {'activeProfileId': 2}})
        self.assertEqual('invalid_response', failure.exception.code)

    def test_service_error_code_survives_transport(self):
        with self.assertRaises(ServiceError) as failure:
            self.request(lambda r: {'id': r['id'], 'ok': False,
                                    'error': {'code': 'stale_preview', 'message': '다시 확인해 주세요.'}})
        self.assertEqual('stale_preview', failure.exception.code)

    def test_malformed_json_is_an_unavailable_service(self):
        with self.assertRaises(ServiceError):
            self.request(lambda r: b'not-json')

    def test_non_object_response_is_rejected(self):
        with self.assertRaises(ServiceError):
            self.request(lambda r: [])

    def test_non_object_error_is_rejected(self):
        with self.assertRaises(ServiceError):
            self.request(lambda r: {'id': r['id'], 'ok': False, 'error': 'invalid-wire-error'})

    def test_connection_failure_never_creates_a_fallback_auth_store(self):
        marker = self.root / 'auth.json'
        marker.write_text('synthetic-auth-sentinel')
        with self.assertRaises(ServiceError) as failure:
            self.client.call('snapshot', timeout=0.1)
        self.assertEqual('unavailable', failure.exception.code)
        self.assertEqual('synthetic-auth-sentinel', marker.read_text())
        self.assertEqual([marker], list(self.root.iterdir()))


class LauncherContractTests(unittest.TestCase):
    def test_background_mode_starts_service_without_opening_ui(self):
        with tempfile.TemporaryDirectory(prefix='syncbar-launcher-') as temporary:
            root = Path(temporary)
            systemctl = root / 'systemctl'
            log = root / 'calls'
            systemctl.write_text('#!/bin/sh\nif [ "$2" = show ]; then printf "0\\n"; else printf "%s\\n" "$*" > "$SYNCBAR_SYSTEMCTL_LOG"; fi\n')
            systemctl.chmod(0o700)
            shutil.copy2(ROOT / 'Ubuntu/packaging/prepare_session.py', root / 'prepare-session.py')
            environment = dict(os.environ, PATH=str(root) + os.pathsep + os.environ['PATH'],
                               SYNCBAR_SYSTEMCTL_LOG=str(log), SYNCBAR_APP_DIR=str(root), XDG_RUNTIME_DIR=str(root),
                               CODEX_HOME=str(root / 'synthetic-codex'))
            environment.pop('SYNCBAR_DEV', None)
            result = subprocess.run(['bash', str(ROOT / 'Ubuntu/packaging/codex-syncbar'), '--background'],
                                    env=environment, capture_output=True, text=True, timeout=5)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual('--user start codex-syncbar.service', log.read_text().strip())


if __name__ == '__main__':
    unittest.main(verbosity=2)
