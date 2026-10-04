#!/usr/bin/env python3
"""Pass only desktop session context to this application's user service."""
import fcntl
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile

VARIABLES = ('DISPLAY', 'WAYLAND_DISPLAY', 'DBUS_SESSION_BUS_ADDRESS', 'XAUTHORITY',
             'XDG_SESSION_TYPE', 'XDG_CURRENT_DESKTOP', 'XDG_DATA_DIRS')


def private(path, directory=False):
    info = path.lstat()
    expected = stat.S_ISDIR if directory else stat.S_ISREG
    if (not expected(info.st_mode) or info.st_uid != os.getuid()
            or info.st_mode & 0o077 or (not directory and info.st_nlink != 1)):
        raise RuntimeError('The application session path must be private and owned by this user.')


def quote(value):
    if any(character in value for character in ('\n', '\r', '\0')):
        raise RuntimeError('The desktop session contains an unsupported environment value.')
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"').replace('$', '\\$').replace('`', '\\`') + '"'


def codex_home(environment):
    value = environment.get('CODEX_HOME') or str(Path(environment.get('HOME') or str(Path.home())) / '.codex')
    path = Path(value)
    if not path.is_absolute():
        raise RuntimeError('CODEX_HOME must be an absolute directory path.')
    return str(path.resolve())


def running_environment(pid):
    raw = Path('/proc', str(pid), 'environ').read_bytes()
    return dict(item.decode().split('=', 1) for item in raw.split(b'\0') if b'=' in item)


def start(environment=None, runner=subprocess.run, read_running=running_environment):
    environment = os.environ if environment is None else environment
    runtime = Path(environment.get('XDG_RUNTIME_DIR') or f'/run/user/{os.getuid()}')
    if not runtime.is_absolute():
        raise RuntimeError('XDG_RUNTIME_DIR must be an absolute directory path.')
    private(runtime, directory=True)
    directory = runtime / 'codex-syncbar'
    try:
        directory.mkdir(mode=0o700)
    except FileExistsError:
        pass
    private(directory, directory=True)
    target = directory / 'session.env'
    if target.exists() or target.is_symlink():
        private(target)
    descriptor = os.open(directory / '.session.lock', os.O_WRONLY | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        private(directory / '.session.lock')
        fcntl.flock(descriptor, fcntl.LOCK_EX)
        context = {key: environment[key] for key in VARIABLES if key in environment}
        context['CODEX_HOME'] = codex_home(environment)
        text = ''.join(key + '=' + quote(value) + '\n' for key, value in context.items())
        status = runner(['systemctl', '--user', 'show', 'codex-syncbar.service', '-p', 'MainPID', '--value'],
                        capture_output=True, text=True, check=True)
        pid = int(status.stdout.strip() or '0')
        if pid and codex_home(read_running(pid)) != context['CODEX_HOME']:
            raise RuntimeError('Codex SyncBar is already using another CODEX_HOME. Finish its operation '
                               'and stop its user service before opening a different account directory.')
        if not pid:
            handle, temporary = tempfile.mkstemp(prefix='.session-', dir=directory)
            try:
                with os.fdopen(handle, 'w') as stream:
                    stream.write(text)
                os.replace(temporary, target)
            finally:
                Path(temporary).unlink(missing_ok=True)
        runner(['systemctl', '--user', 'start', 'codex-syncbar.service'], check=True)
    finally:
        os.close(descriptor)


if __name__ == '__main__':
    try:
        start()
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        # No environment values, process environments or credentials are printed.
        print(str(error) if isinstance(error, RuntimeError) else 'Codex SyncBar could not prepare its user service.', file=sys.stderr)
        raise SystemExit(1)
