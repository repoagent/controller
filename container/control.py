"""Test-runner mailbox. This helper has storage access, never Docker access."""
import json
from pathlib import Path
import sys
import time
import base64
import os
import subprocess
import urllib.request

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
elif command in ('invalidate-session', 'restore-session'):
    state_path = Path('/data/state/bridge.json')
    state = json.loads(state_path.read_text())
    discussion = sys.argv[2]
    record = state['discussions'][discussion]
    backup = root / 'native-session-backup.json'
    if command == 'invalidate-session':
        backup.write_text(json.dumps({'discussion': discussion, 'sessionId': record['sessionId']}))
        record['sessionId'] = 'invalid-native-session'
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
elif command in ('read-only-credential', 'restore-credential'):
    # Disposable live fault fixture: GitHub itself rejects receipt writes with this valid read-only token.
    folder = Path('/data/state/bridge.json.github-app')
    cache = folder / 'installation-token.json'
    backup = folder / 'receipt-original.json'
    if command == 'restore-credential':
        if backup.exists():
            readonly_token = json.loads(cache.read_text())['token']
            backup.replace(cache)
            request = urllib.request.Request('https://api.github.com/installation/token', method='DELETE',
                headers={'Authorization': 'Bearer ' + readonly_token, 'User-Agent': 'repoagent-receipt-fault'})
            with urllib.request.urlopen(request, timeout=60):
                pass
    else:
        if backup.exists():
            raise ValueError('A receipt-failure fixture is already active')
        config = json.loads(Path('/data/config/settings.json').read_text())
        def encode(value):
            return base64.urlsafe_b64encode(value).rstrip(b'=')
        now = int(time.time())
        signed = encode(b'{"alg":"RS256","typ":"JWT"}') + b'.' + encode(json.dumps({'iat': now - 60, 'exp': now + 540, 'iss': config['appId']}).encode())
        signature = subprocess.check_output(['openssl', 'dgst', '-sha256', '-sign', '/run/secrets/github-app.pem'], input=signed)
        jwt = (signed + b'.' + encode(signature)).decode()
        def api(path, body=None):
            request = urllib.request.Request('https://api.github.com/' + path, data=json.dumps(body).encode() if body else None,
                headers={'Authorization': 'Bearer ' + jwt, 'User-Agent': 'repoagent-receipt-fault', 'Accept': 'application/vnd.github+json', 'Content-Type': 'application/json'})
            with urllib.request.urlopen(request, timeout=60) as response:
                return json.load(response)
        installation = api('repos/' + config['repository'] + '/installation')['id']
        result = api(f'app/installations/{installation}/access_tokens', {'repositories': [config['repository'].split('/')[1]], 'permissions': {'discussions': 'read'}})
        os.umask(0o077)
        backup.write_bytes(cache.read_bytes())
        backup.chmod(0o600)
        temporary = folder / 'receipt-readonly.tmp'
        temporary.write_text(json.dumps({'token': result['token'], 'expiresAt': result['expires_at']}))
        temporary.chmod(0o600)
        temporary.replace(cache)
