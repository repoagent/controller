"""Test-runner mailbox. This helper has storage access, never Docker access."""
import json
from pathlib import Path
import sys
import time
import subprocess

root = Path('/data/control')
command = sys.argv[1]
if command == 'request':
    request = root / 'request.json'
    if request.exists():
        value = json.loads(request.read_text())
        request.unlink()
        for key in ('id', 'operation', 'argument'):
            sys.stdout.write((value.get(key) or '') + '\0')
    else:
        time.sleep(0.5)
elif command == 'response':
    identifier, status = sys.argv[2:4]
    if len(identifier) != 32 or not all(c in '0123456789abcdef' for c in identifier):
        raise ValueError('Invalid response correlation ID')
    (root / f'response-{identifier}.json').write_text(json.dumps({'ok': status == '0', 'error': None if status == '0' else 'Host lifecycle operation failed; inspect retained host log.'}))
elif command == 'capture':
    (root / 'bridge.log').write_text((root / 'archive.log').read_text() if (root / 'archive.log').exists() else '')
    with (root / 'bridge.log').open('a') as output:
        output.write(sys.stdin.read())
elif command == 'archive':
    with (root / 'archive.log').open('a') as output:
        output.write(sys.stdin.read())
elif command == 'inspection':
    value = json.load(sys.stdin)[0]
    host = value['HostConfig']
    assert value['Config']['User'] in ('agent', '1000')
    assert not host['Privileged'] and host['NetworkMode'] != 'host'
    assert not host.get('PortBindings')
    mounts = value['Mounts']
    assert not any('docker.sock' in mount['Destination'] for mount in mounts)
    assert all(mount['Destination'] == '/run/secrets/github-app.pem' for mount in mounts if mount['Type'] == 'bind')
    config = next(mount for mount in mounts if mount['Destination'] == '/data/config')
    key = next(mount for mount in mounts if mount['Destination'] == '/run/secrets/github-app.pem')
    assert not config['RW'] and not key['RW']
    (root / 'runtime-inspection.json').write_text(json.dumps({'image': value['Image'], 'user': value['Config']['User'],
        'privileged': host['Privileged'], 'networkMode': host['NetworkMode'], 'portBindings': host.get('PortBindings'),
        'mounts': [{'type': m['Type'], 'destination': m['Destination'], 'writable': m['RW']} for m in mounts]}, indent=2))
elif command == 'artifact':
    name = sys.argv[2]
    assert name in ('versions.txt', 'skills.json')
    (root / name).write_text(sys.stdin.read())
elif command in ('invalidate-session', 'restore-session'):
    state_path = Path('/data/state/bridge.json')
    state = json.loads(state_path.read_text())
    discussion = sys.argv[2]
    record = state['discussions'][discussion]
    backup = root / 'agent-session-backup.json'
    if command == 'invalidate-session':
        backup.write_text(json.dumps({'discussion': discussion, 'sessionId': record['sessionId']}))
        record['sessionId'] = 'invalid-agent-session'
    else:
        saved = json.loads(backup.read_text())
        assert saved['discussion'] == discussion
        record['sessionId'] = saved['sessionId']
        # Only after the suite observed native rejection, so no accepted input is replayed.
        record.pop('pending', None)
    temporary = state_path.with_suffix('.tmp')
    temporary.write_text(json.dumps(state, indent=2))
    temporary.chmod(0o600)
    temporary.replace(state_path)
elif command == 'revoke-credential':
    # Disposable live fault fixture (C12). The controller holds its installation token only in memory, so
    # nothing can substitute it; the fault is GitHub revoking that live token. The controller's own App gh
    # wrapper reaches it through the credential handoff, as the agent's gh does, so the token never reaches
    # this process, a file or an argument. GitHub then rejects the controller's requests with it until a new
    # controller lifetime acquires a fresh token. Without a running controller nothing is revoked.
    state = sys.argv[2] if len(sys.argv) > 2 else '/data/state/bridge.json'
    result = subprocess.run([state + '.github-app/bin/gh', 'api', '--method', 'DELETE', 'installation/token'],
                            stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=60)
    if result.returncode:
        sys.exit('No running controller credential was revoked: ' + (result.stderr.strip() or f'exit {result.returncode}'))
