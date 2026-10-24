"""Bounded, nonsecret lifecycle operations on explicitly mounted private volumes."""
import json
import os
from pathlib import Path
import re
import stat
import sys
import tempfile

ENROLLMENT = Path('/data/enrollment')
MARKER = ENROLLMENT / 'disabled.json'
LAUNCH = ENROLLMENT / 'launch-state.json'
REQUIRED = ENROLLMENT / 'launch-state.required'
TARGETS = (ENROLLMENT / 'enrollment.json', Path('/data/credentials/codex/auth.json'),
           Path('/data/state/bridge.json.github-app/installation-token.json'),
           Path('/data/state/bridge.json.github-app/installation-token.json.tmp'))


def checked(path, missing=False):
    for item in reversed((path, *path.parents)):
        try:
            info = item.lstat()
        except FileNotFoundError:
            if missing:
                return False
            raise ValueError('Missing selected file')
        if item.as_posix().startswith(('/data/enrollment', '/data/config', '/data/credentials', '/data/state')) and stat.S_ISDIR(info.st_mode):
            if info.st_uid != 1000 or info.st_mode & 0o022:
                raise ValueError('Unsafe selected directory ownership or mode')
        if stat.S_ISLNK(info.st_mode) or (item != path and not stat.S_ISDIR(info.st_mode)):
            raise ValueError('Unsafe selected directory')
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_uid != 1000 or info.st_mode & 0o077:
        raise ValueError('Unsafe selected file')
    return True


def pairs(items):
    result = {}
    for key, value in items:
        if key.lower() in {name.lower() for name in result}:
            raise ValueError('Duplicate field')
        result[key] = value
    return result


def document(data):
    if len(data) > 65536:
        raise ValueError('Oversized document')
    value = json.loads(data, object_pairs_hook=pairs)
    def depth(node, level=0):
        if level > 8:
            raise ValueError('Deep document')
        for child in node.values() if isinstance(node, dict) else node if isinstance(node, list) else ():
            depth(child, level + 1)
    depth(value)
    if not isinstance(value, dict):
        raise ValueError('Expected object')
    return value


def read(path):
    checked(path)
    with path.open('rb') as stream:
        return document(stream.read(65537))


def marker(value):
    if set(value) != {'schemaVersion', 'state', 'instance', 'image', 'binding'}:
        raise ValueError('Invalid disable fields')
    if type(value['schemaVersion']) is not int or value['schemaVersion'] != 1 or value['state'] != 'disabled':
        raise ValueError('Invalid disable state')
    patterns = {'instance': r'[a-z0-9][a-z0-9-]{0,40}', 'image': r'sha256:[a-f0-9]{64}'}
    binding = value['binding']
    fields = {'repository', 'repositoryId', 'deviceId', 'appId', 'installationId', 'brokerOrigin', 'profile',
              'approvalKind', 'approvalVariable', 'expectedValueSha256', 'githubEnvironment', 'environmentId'}
    if not isinstance(binding, dict) or set(binding) != fields:
        raise ValueError('Invalid disable binding')
    for name, pattern in patterns.items():
        if not isinstance(value[name], str) or not re.fullmatch(pattern, value[name]):
            raise ValueError('Invalid identity')
    for name in ('repositoryId', 'appId', 'installationId'):
        if type(binding[name]) is not int or not 0 < binding[name] < 2**63:
            raise ValueError('Invalid numeric identity')
    patterns = {'repository': r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', 'deviceId': r'[A-Za-z0-9_-]{1,128}',
                'brokerOrigin': r'https://[A-Za-z0-9.-]+(?::[0-9]+)?', 'profile': r'(polling|webhook)(-clone)?(-settings)?',
                'approvalVariable': r'[A-Z_][A-Z0-9_]{0,99}', 'expectedValueSha256': r'[a-f0-9]{64}'}
    for name, pattern in patterns.items():
        if not isinstance(binding[name], str) or not re.fullmatch(pattern, binding[name]):
            raise ValueError('Invalid binding')
    # Repository-settings profiles exist only with Actions-variable approval (setup v3, ADR 0008).
    if binding['profile'].endswith('-settings') and binding['approvalKind'] != 'repository-actions-variable':
        raise ValueError('Invalid settings binding')
    # Actions variables (setup v3) and legacy Agent variables (setup v2) are distinct
    # direct repository locations without Environment identity.
    if binding['approvalKind'] in ('repository-actions-variable', 'repository-agent-variable'):
        if binding['githubEnvironment'] is not None or binding['environmentId'] is not None:
            raise ValueError('Unexpected legacy binding')
    elif (binding['approvalKind'] != 'environment-variable'
          or not isinstance(binding['githubEnvironment'], str)
          or not re.fullmatch(r'[^\x00-\x1f\x7f]{1,255}', binding['githubEnvironment'])
          or type(binding['environmentId']) is not int
          or not 0 < binding['environmentId'] < 2**63):
        raise ValueError('Invalid legacy binding')
    return value


def verify_binding(expected, config=None):
    binding = expected['binding']
    config = read(Path('/data/config/settings.json')) if config is None else config
    fields = {'repository', 'appId', 'installationId', 'brokerUrl', 'keyPath', 'mentionUser', 'model', 'mode',
              'publicUrl', 'listenPort', 'reconcileInterval', 'toolUpdates', 'webSearch', 'clone', 'enroll', 'repositoryId',
              'repositorySettings'}
    if set(config) - fields or any(not isinstance(config.get(key), str) for key in ('repository', 'appId', 'brokerUrl', 'mode')):
        raise ValueError('Invalid runtime settings')
    types = {'clone': bool, 'listenPort': int, 'reconcileInterval': int, 'enroll': list, 'repositoryId': int, 'repositorySettings': bool}
    for key, value in config.items():
        if value is not None and type(value) is not types.get(key, int if key == 'installationId' else str):
            raise ValueError('Invalid runtime setting type')
    if config['mode'] not in ('polling', 'fixed-url', 'quick-tunnel') or config.get('toolUpdates', 'latest') not in ('latest', 'pinned') or config.get('webSearch', 'live') not in ('live', 'cached', 'disabled'):
        raise ValueError('Invalid runtime setting choice')
    if any(not isinstance(value, str) for value in config.get('enroll', [])):
        raise ValueError('Invalid discussion enrollment list')
    for field, key in (('repository', 'repository'), ('appId', 'appId'), ('brokerOrigin', 'brokerUrl')):
        if str(config.get(key)) != str(binding[field]):
            raise ValueError('Selected configuration differs')
    if config.get('installationId') and str(config['installationId']) != str(binding['installationId']):
        raise ValueError('Selected installation differs')
    if config.get('repositoryId') is not None and config['repositoryId'] != binding['repositoryId']:
        raise ValueError('Selected immutable repository differs')
    settings = bool(config.get('repositorySettings'))
    profile = (('polling' if config.get('mode') == 'polling' else 'webhook') + ('-clone' if config.get('clone') else '')
               + ('-settings' if settings else ''))
    if profile != binding['profile']:
        raise ValueError('Selected profile differs')
    if checked(TARGETS[0], missing=True):
        credential = read(TARGETS[0])
        fields = {'protocolVersion', 'brokerOrigin', 'githubHost', 'deviceId', 'deviceSecret', 'appId', 'appSlug', 'installationId', 'repositoryId', 'repository', 'permissions'}
        # Clone (v2) and repository-settings (v3) enrollments name their profile and policy.
        if credential.get('protocolVersion') in (2, 3):
            fields |= {'profile', 'policyVersion'}
        if type(credential.get('protocolVersion')) is not int:
            raise ValueError('Invalid enrollment version')
        if set(credential) != fields or credential['protocolVersion'] != (3 if settings else 2 if config.get('clone') else 1) or credential['githubHost'] != 'github.com':
            raise ValueError('Invalid enrollment')
        for key in ('repositoryId', 'appId', 'installationId'):
            if type(credential[key]) is not int or not 0 < credential[key] < 2**63:
                raise ValueError('Invalid enrollment identity')
        if any(not isinstance(credential[key], str) or not credential[key] for key in ('deviceId', 'deviceSecret', 'brokerOrigin', 'repository', 'appSlug')):
            raise ValueError('Invalid enrollment text')
        permissions = {'discussions': 'write'}
        if profile.startswith('webhook'):
            permissions['repository_hooks'] = 'write'
        if config.get('clone'):
            permissions['contents'] = 'read'
        if settings:
            permissions['actions_variables'] = 'read'
        if credential['protocolVersion'] > 1 and 'metadata' in credential['permissions']:
            permissions['metadata'] = 'read'
        if credential['permissions'] != permissions:
            raise ValueError('Enrollment permissions differ')
        policy = {'repository-actions-variable': 'repository-actions-settings-approval-v3'}.get(binding['approvalKind']) if settings else \
            {'repository-actions-variable': 'repository-actions-clone-approval-v3',
             'repository-agent-variable': 'repository-agent-clone-approval-v2'}.get(binding['approvalKind'])
        if credential['protocolVersion'] > 1 and credential['policyVersion'] != policy:
            raise ValueError('Enrollment policy differs')
        for key in ('repository', 'repositoryId', 'deviceId', 'appId', 'installationId', 'brokerOrigin'):
            if credential[key] != binding[key]:
                raise ValueError('Selected enrollment differs')
        if credential.get('profile', profile) != profile:
            raise ValueError('Selected enrollment profile differs')


def launch_state(value):
    fields = {'schemaVersion', 'instance', 'volumes', 'storageOriginImage', 'currentImage', 'currentLaunch',
              'priorImage', 'priorLaunch', 'targetImage', 'targetLaunch', 'pending', 'operationId', 'revision'}
    if set(value) != fields or type(value['schemaVersion']) is not int or value['schemaVersion'] != 1:
        raise ValueError('Invalid launch fields')
    if (value['pending'] not in ('none', 'update', 'rollback') or type(value['revision']) is not int
            or not 0 < value['revision'] < 2**63 or not re.fullmatch(r'[a-f0-9]{32}', value['operationId'])):
        raise ValueError('Invalid launch operation')
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,40}', value['instance']):
        raise ValueError('Invalid launch instance')
    expected = {name: 'repoagent-' + value['instance'] + '-' + name
                for name in ('config', 'credentials', 'workspace', 'history', 'state', 'tools', 'enrollment')}
    if value['volumes'] != expected or not re.fullmatch(r'sha256:[a-f0-9]{64}', value['storageOriginImage']):
        raise ValueError('Invalid launch storage')
    for prefix in ('current', 'prior', 'target'):
        image, launch = value[prefix + 'Image'], value[prefix + 'Launch']
        if image is None and launch is None and prefix != 'current' and value['pending'] == 'none':
            continue
        if not isinstance(launch, dict) or set(launch) != {'binding', 'settings'}:
            raise ValueError('Invalid saved launch')
        selected = marker(dict(schemaVersion=1, state='disabled', instance=value['instance'], image=image, binding=launch['binding']))
        verify_binding(selected, launch['settings'])
        if launch['settings'].get('keyPath'):
            raise ValueError('Host credential paths are not launch state')
    return value


def main():
    operation = sys.argv[-1]
    existing = marker(read(MARKER)) if checked(MARKER, missing=True) else None
    saved = launch_state(read(LAUNCH)) if checked(LAUNCH, missing=True) else None
    required = checked(REQUIRED, missing=True)
    if operation == 'inspect':
        return {'marker': existing, 'launchState': saved, 'launchRequired': required}
    if required and saved is None:
        raise ValueError('Missing authoritative launch state')
    if saved is not None and sys.argv[1] not in (saved['currentImage'], saved['priorImage']):
        raise ValueError('Restore a recorded current or prior write helper')
    expected = document(sys.stdin.buffer.read(65537))
    if operation in ('launch', 'restore'):
        if existing is not None:
            raise ValueError('Selected instance is disabled')
        if operation == 'launch':
            checked(TARGETS[0])
        expected = launch_state(expected)
        if operation == 'restore' and (saved != expected or expected['pending'] != 'rollback'):
            raise ValueError('Recorded rollback intent required')
        if operation == 'launch' and expected['currentLaunch']['settings'] != read(Path('/data/config/settings.json')):
            raise ValueError('Selected launch settings differ')
        if operation == 'launch' and not required:
            descriptor = os.open(REQUIRED, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(descriptor, 'wb') as stream:
                stream.write(b'1'); stream.flush(); os.fsync(stream.fileno())
    else:
        expected = marker(expected)
    if operation not in ('launch', 'restore') and existing is not None and existing != expected:
        raise ValueError('Disable identity differs')
    if operation not in ('launch', 'restore'):
        verify_binding(expected)
    if operation == 'verify':
        return {'verified': True, 'launch': {'binding': expected['binding'], 'settings': read(Path('/data/config/settings.json'))}}
    if operation in ('disable', 'launch', 'restore'):
        destination = LAUNCH if operation == 'launch' else Path('/data/config/settings.json') if operation == 'restore' else MARKER
        if operation == 'restore':
            expected = expected['targetLaunch']['settings']
        checked(destination, missing=True)
        if operation in ('launch', 'restore') or existing is None:
            descriptor, name = tempfile.mkstemp(prefix='.lifecycle-', dir=destination.parent)
            try:
                with os.fdopen(descriptor, 'wb') as stream:
                    stream.write(json.dumps(expected).encode())
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(name, destination)
                directory = os.open(destination.parent, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    os.fsync(directory)
                finally:
                    os.close(directory)
            finally:
                if os.path.exists(name):
                    os.unlink(name)
        return {'launchState': expected} if operation == 'launch' else {'state': 'restored' if operation == 'restore' else 'disabled'}
    if operation != 'clear' or existing is None:
        raise ValueError('Durable disable required')
    results = {}
    for path in TARGETS:
        try:
            if checked(path, missing=True):
                path.unlink()
            results[str(path)] = 'cleared'
        except (OSError, ValueError):
            results[str(path)] = 'unconfirmed'
    return {'files': results}


try:
    print(json.dumps(main()))
except Exception:
    print('Selected lifecycle operation is unconfirmed; inspect private file safety and binding.', file=sys.stderr)
    sys.exit(1)
