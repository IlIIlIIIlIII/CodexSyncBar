#!/usr/bin/env bash
# Real SSH transport smoke; run as an ordinary user, never through sudo.
# Dependencies: python3, bash, ssh, ssh-keygen, sshd, jq, and sshd's shared libs.
# Ubuntu CI: sudo apt-get update && sudo apt-get install -y openssh-server jq
# A portable sshd may be selected using argument 1 or SSHD_EXECUTABLE. Supply its
# shared library directory through LD_LIBRARY_PATH when needed; this script does
# not install packages, modify SSH config, or use the real HOME/authentication.
# Optional: CODEX_SYNCBAR_SSH_QA_REPORT=/path/to/report.json
set -euo pipefail
qa_repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
exec python3 - "$qa_repo" "$@" <<'PY'
from pathlib import Path
import base64, hashlib, json, os, pwd, shlex, shutil, socket, subprocess, sys, tempfile, time

repo = Path(sys.argv[1])
sshd = sys.argv[2] if len(sys.argv) > 2 else os.environ.get('SSHD_EXECUTABLE', shutil.which('sshd') or '/usr/sbin/sshd')
if os.getuid() == 0:
    raise SystemExit('Run this test as an ordinary user, not sudo/root.')
for name in ('ssh', 'ssh-keygen', 'jq', 'bash'):
    if not shutil.which(name):
        raise SystemExit('Missing dependency: ' + name)
if not Path(sshd).is_file():
    raise SystemExit('Missing sshd. Install openssh-server or set SSHD_EXECUTABLE to a portable daemon.')
root = Path(tempfile.mkdtemp(prefix='codex-syncbar-ssh-smoke-'))
daemon = None
checks = []
try:
    root.chmod(0o700)
    fixture_home = root / 'home'; fixture_home.mkdir(mode=0o700)
    bin_dir = root / 'bin'; bin_dir.mkdir(mode=0o700)
    # Never discover or signal the user's running Codex app-server processes.
    for name, code in [('node', 0), ('ps', 0), ('pgrep', 1)]:
        stub = bin_dir / name
        stub.write_text('#!/bin/sh\nexit ' + str(code) + '\n'); stub.chmod(0o700)
    for name in ('host_key', 'client_key'):
        subprocess.run(['ssh-keygen', '-q', '-t', 'ed25519', '-N', '', '-f', str(root / name)], check=True)
    (root / 'authorized_keys').write_bytes((root / 'client_key.pub').read_bytes())
    (root / 'authorized_keys').chmod(0o600)
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0)); port = probe.getsockname()[1]
    key = (root / 'host_key.pub').read_text().split()
    (root / 'known_hosts').write_text(f'[127.0.0.1]:{port} {key[0]} {key[1]}\n')
    variables = {
        'HOME': str(fixture_home), 'CODEX_HOME': str(fixture_home / '.codex'),
        'GPT_SWITCH_STATE_ROOT': str(fixture_home / '.local/share/gpt-switch'),
        'PATH': str(bin_dir) + ':' + os.environ.get('PATH', '/usr/bin:/bin'),
        'LD_LIBRARY_PATH': os.environ.get('LD_LIBRARY_PATH', ''),
    }
    wrapper = '#!/bin/bash\nset -euo pipefail\nunset GPT_SWITCH_CONFIG_FILE\n'
    wrapper += ''.join('export ' + name + '=' + shlex.quote(value) + '\n' for name, value in variables.items())
    wrapper += 'cd "$HOME"\nexec /bin/bash --noprofile --norc -c "$SSH_ORIGINAL_COMMAND"\n'
    forced = root / 'force-command'; forced.write_text(wrapper); forced.chmod(0o700)
    username = pwd.getpwuid(os.getuid()).pw_name
    config = [f'Port {port}', 'ListenAddress 127.0.0.1', f'HostKey {root}/host_key',
        f'PidFile {root}/sshd.pid', f'AuthorizedKeysFile {root}/authorized_keys',
        # The generated keys are inside a mode0700 tempfile, but sshd StrictModes
        # rejects /tmp's shared ancestor. Relax only this private test daemon.
        'StrictModes no', 'UsePAM no', 'PasswordAuthentication no',
        'KbdInteractiveAuthentication no', 'PermitRootLogin no', 'PermitTTY no',
        'PermitUserRC no', 'PermitUserEnvironment no',
        f'AllowUsers {username}', 'AllowTcpForwarding no', 'X11Forwarding no',
        f'ForceCommand {forced}', 'LogLevel VERBOSE']
    sidecars = Path(sshd).resolve().parent.parent / 'lib/openssh'
    for option, name in [('SshdSessionPath', 'sshd-session'), ('SshdAuthPath', 'sshd-auth')]:
        if (sidecars / name).exists():
            config.append(f'{option} {sidecars / name}')
    (root / 'sshd_config').write_text('\n'.join(config) + '\n')
    with (root / 'sshd.log').open('w') as log:
        daemon = subprocess.Popen([sshd, '-D', '-e', '-f', str(root / 'sshd_config')], stdout=log, stderr=log)
    for attempt in range(100):
        output = (root / 'sshd.log').read_text()
        if 'Server listening on' in output: break
        if daemon.poll() is not None: raise RuntimeError('Isolated sshd could not start: ' + output)
        time.sleep(.05)
    else: raise RuntimeError('Isolated sshd did not start within 5 seconds.')
    ssh = ['ssh', '-F', '/dev/null', '-i', str(root / 'client_key'), '-p', str(port),
        '-o', 'IdentitiesOnly=yes', '-o', 'StrictHostKeyChecking=yes',
        '-o', 'UserKnownHostsFile=' + str(root / 'known_hosts'), '-o', 'BatchMode=yes',
        '-o', 'ConnectTimeout=3', username + '@127.0.0.1']
    def run(name, command, data=None, success=True):
        result = subprocess.run(ssh + [command], input=data, capture_output=True, timeout=20)
        if (result.returncode == 0) != success:
            raise RuntimeError(name + ': ' + result.stderr.decode(errors='replace'))
        checks.append({'name': name, 'passed': True, 'exitCode': result.returncode})
        return result.stdout.decode()
    def encoded(value):
        return base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip('=')
    def credentials(index, refresh=''):
        return {'auth_mode': 'chatgpt', 'tokens': {
            'id_token': 'e30.' + encoded({'email': f'ssh-smoke-{index}@example.invalid'}) + '.test',
            'access_token': 'e30.' + encoded({'exp': int(time.time()) + 3600, 'sub': f'ssh-smoke-{index}'}) + '.test',
            'refresh_token': refresh, 'account_id': f'ssh-smoke-account-{index}'}}
    def fp(value): return hashlib.sha256(value.encode()).hexdigest()[:12]
    run('remote helper bootstrap over encrypted SSH',
        'umask 077; mkdir -p "$HOME/.local/bin"; cat > "$HOME/.local/bin/gpt-switch"; chmod 700 "$HOME/.local/bin/gpt-switch"',
        (repo / 'Support/gpt-switch').read_bytes())
    version = run('remote helper version', '"$HOME/.local/bin/gpt-switch" __node version').strip()
    for index in (1, 2):
        auth = credentials(index)
        run('stream access-only profile ' + str(index),
            f'"$HOME/.local/bin/gpt-switch" __node install-access {index} ' + fp(auth['tokens']['account_id']) + ' ' + fp(auth['tokens']['access_token']), json.dumps(auth).encode())
    run('initialize account 1', '"$HOME/.local/bin/gpt-switch" __node initialize 1')
    assert 'active=1' in run('preflight account 2', '"$HOME/.local/bin/gpt-switch" __node preflight 2')
    run('apply account 2 without signalling host clients', '"$HOME/.local/bin/gpt-switch" __node switch 2 0')
    run('verify account 2', '"$HOME/.local/bin/gpt-switch" __node verify 2')
    assert 'active=2' in run('read remote active status', '"$HOME/.local/bin/gpt-switch" __node status')
    run('reverse rollback to account 1', '"$HOME/.local/bin/gpt-switch" __node switch 1 0')
    run('verify rollback account 1', '"$HOME/.local/bin/gpt-switch" __node verify 1')
    bad = credentials(3, 'synthetic-refresh-must-be-rejected')
    run('reject refresh-bearing remote credential',
        '"$HOME/.local/bin/gpt-switch" __node install-access 3 ' + fp(bad['tokens']['account_id']) + ' ' + fp(bad['tokens']['access_token']), json.dumps(bad).encode(), False)
    run('make fixture profile insecure', 'chmod 644 "$GPT_SWITCH_STATE_ROOT/profiles/2.auth.json"')
    run('reject insecure remote profile', '"$HOME/.local/bin/gpt-switch" __node preflight 2', success=False)
    run('restore secure fixture profile', 'chmod 600 "$GPT_SWITCH_STATE_ROOT/profiles/2.auth.json"')
    run('verify failed preflight preserves active account', '"$HOME/.local/bin/gpt-switch" __node verify 1')
    active = fixture_home / '.codex/auth.json'; state = fixture_home / '.local/share/gpt-switch'
    auth = json.loads(active.read_text())
    assert auth['tokens']['account_id'] == 'ssh-smoke-account-1'
    assert auth['tokens']['refresh_token'] == ''
    assert active.stat().st_mode & 0o777 == 0o600
    assert not (state / 'profiles/3.auth.json').exists()
    checks.append({'name': 'active auth mode0600, refresh absent, rejected profile absent', 'passed': True})
    report = {'transport': 'Real OpenSSH loopback TCP with public-key authentication',
        'helperVersion': version, 'home': '<isolated temporary Linux home>', 'checks': checks,
        'serverScope': 'Unprivileged current user; loopback only; generated/pinned key; forced temporary HOME/CODEX_HOME; no user SSH config or production auth',
        'limitations': ['Synthetic accounts only; no real cloud host or account.', 'Shell helper over SSH; C# SshDeviceService adapter is tested separately.']}
    if os.environ.get('CODEX_SYNCBAR_SSH_QA_REPORT'):
        target = Path(os.environ['CODEX_SYNCBAR_SSH_QA_REPORT']); target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'passed': len(checks), 'failed': 0, 'helperVersion': version}))
finally:
    if daemon is not None and daemon.poll() is None:
        daemon.terminate()
        try: daemon.wait(timeout=5)
        except subprocess.TimeoutExpired:
            daemon.kill(); daemon.wait()
    shutil.rmtree(root)
PY
