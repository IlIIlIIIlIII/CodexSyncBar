"""Credential-free, asynchronous client for the per-user SyncBar service."""
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import socket
import struct
import uuid

from gi.repository import GLib


class ServiceError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def default_socket():
    runtime = os.environ.get('XDG_RUNTIME_DIR', f'/run/user/{os.getuid()}')
    return str(Path(runtime) / 'codex-syncbar' / 'control.sock')


class Client:
    def __init__(self, path=None):
        self.path = path or default_socket()
        self.pool = ThreadPoolExecutor(max_workers=4, thread_name_prefix='syncbar-ipc')

    def call(self, method, params=None, timeout=35):
        request_id = uuid.uuid4().hex
        data = json.dumps({'id': request_id, 'method': method, 'params': params or {}}, ensure_ascii=False)
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
                connection.settimeout(timeout)
                connection.connect(self.path)
                _pid, peer_uid, _gid = struct.unpack('3i', connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
                if peer_uid != os.getuid():
                    raise ServiceError('invalid_peer', '다른 사용자의 서비스에는 연결할 수 없습니다.')
                connection.sendall(data.encode('utf-8') + b'\n')
                reader = connection.makefile('rb')
                response_line = reader.readline(4 * 1024 * 1024 + 1)
                if len(response_line) > 4 * 1024 * 1024:
                    raise ServiceError('invalid_response', '서비스 응답이 너무 큽니다.')
                response = json.loads(response_line)
                if not isinstance(response, dict):
                    raise ServiceError('invalid_response', '서비스 응답 형식이 올바르지 않습니다.')
                if response.get('id') != request_id:
                    raise ServiceError('invalid_response', '서비스 응답을 확인하지 못했습니다.')
                if response.get('ok') is not True:
                    error = response.get('error') or {}
                    if not isinstance(error, dict):
                        raise ServiceError('invalid_response', '서비스 오류 응답 형식이 올바르지 않습니다.')
                    raise ServiceError(error.get('code', 'service_error'), error.get('message', '작업을 완료하지 못했습니다.'))
                return response.get('result')
        except (OSError, ValueError) as error:
            raise ServiceError('unavailable', '백그라운드 서비스에 연결하지 못했습니다. 앱을 다시 실행하거나 재시도해 주세요.') from error

    def submit(self, method, params=None, callback=None, timeout=35):
        future = self.pool.submit(self.call, method, params, timeout)
        def done(completed):
            try:
                result, error = completed.result(), None
            except Exception as failure:
                result, error = None, failure
            if callback:
                GLib.idle_add(callback, result, error)
        future.add_done_callback(done)
        return future

    def close(self):
        self.pool.shutdown(wait=False, cancel_futures=True)
