"""Discover concrete user SSH aliases; let OpenSSH resolve connection options."""
import glob
from pathlib import Path
import re
import shlex
import subprocess


HOST = re.compile(r'[A-Za-z0-9][A-Za-z0-9._:-]{0,252}\Z')


def list_hosts(config=None):
    config = Path(config) if config else Path.home() / '.ssh/config'
    root = config.parent
    aliases, visited = {}, set()

    def read(path, depth=0):
        path = path.resolve()
        if path in visited or depth > 16:
            return
        visited.add(path)
        if not path.exists():
            return
        for number, line in enumerate(path.read_text().splitlines(), 1):
            try:
                parts = shlex.split(line, comments=True)
            except ValueError as error:
                raise ValueError(f'SSH config 구문을 확인해 주세요: {path.name}:{number}') from error
            if not parts:
                continue
            # OpenSSH accepts both "Host alias" and "Host=alias".
            key, equal, value = parts[0].partition('=')
            values = ([value] if value else []) + parts[1:] if equal else parts[1:]
            if values and values[0] == '=':
                values = values[1:]
            if key.lower() == 'host':
                for alias in values:
                    if HOST.fullmatch(alias):
                        aliases.setdefault(alias.lower(), alias)
            elif key.lower() == 'include':
                for pattern in values:
                    include = Path(pattern).expanduser()
                    if not include.is_absolute():
                        include = root / include
                    for match in sorted(glob.glob(str(include))):
                        read(Path(match), depth + 1)

    read(config)
    return list(aliases.values())


def device_from_host(alias, config=None):
    if not HOST.fullmatch(alias):
        raise ValueError('SSH 호스트 이름을 확인해 주세요.')
    command = ['ssh', '-G', '-o', 'PermitLocalCommand=no']
    if config is not None:
        command += ['-F', str(config)]
    command.append(alias)
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=10, check=True)
    except (OSError, subprocess.SubprocessError) as error:
        raise ValueError('SSH 설정을 읽지 못했습니다. ssh config 구문과 OpenSSH 설치를 확인해 주세요.') from error
    options = dict(line.split(None, 1) for line in result.stdout.splitlines() if line.startswith(('user ', 'port ')))
    if not options.get('user') or not options.get('port', '').isdigit():
        raise ValueError('SSH 설정에서 사용자 이름과 포트를 확인하지 못했습니다.')
    return {'displayName': alias[:64], 'host': alias, 'username': options['user'],
            'port': int(options['port']), 'authentication': 'openSSHConfig', 'enabled': False}
