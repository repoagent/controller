#!/usr/bin/env python3
"""Explicit selected-container version observation, separate from GET-only diagnostics."""
import datetime as dt
import json
import os
from pathlib import Path
import re
import runpy
import selectors
import signal
import subprocess
import sys
import time
from types import SimpleNamespace

guard = SimpleNamespace(**runpy.run_path(str(Path(__file__).with_name('broker-diagnostics.py'))))


def selection(image, revision, replica):
    app = guard.read(guard.APP)
    if app.get('id', '').lower() != guard.APP.lower() or any(app.get('tags', {}).get(key) != value for key, value in
            dict(repositoryId='1426154477', ownerId='345029828', managedBy='repoagent-auth').items()):
        raise guard.Unavailable('target-ownership-mismatch')
    properties = app['properties']
    if properties.get('provisioningState') != 'Succeeded' or properties.get('latestReadyRevisionName') != revision:
        raise guard.Unavailable('selected-runtime-mismatch')
    revision_id = guard.APP+'/revisions/'+revision
    snapshot = guard.read(revision_id)
    if snapshot.get('id', '').lower() != revision_id.lower() or snapshot.get('name') != revision \
            or snapshot['properties'].get('active') is not True or snapshot['properties'].get('healthState') != 'Healthy':
        raise guard.Unavailable('selected-runtime-mismatch')
    for template in (properties.get('template', {}), snapshot['properties'].get('template', {})):
        containers = template.get('containers', [])
        if len(containers) != 1 or containers[0].get('name') != 'auth-service' or containers[0].get('image') != image:
            raise guard.Unavailable('selected-runtime-mismatch')
    replica_id = revision_id+'/replicas/'+replica
    snapshot = guard.read(replica_id)
    containers = snapshot['properties'].get('containers', [])
    if snapshot.get('id', '').lower() != replica_id.lower() or snapshot.get('name') != replica \
            or snapshot['properties'].get('runningState') != 'Running' or len(containers) != 1 \
            or containers[0].get('name') != 'auth-service' or containers[0].get('ready') is not True \
            or containers[0].get('runningState') != 'Running':
        raise guard.Unavailable('selected-runtime-mismatch')


def console(revision, replica):
    import pty
    # Only stdin is a PTY. No terminal input is ever sent; stdout stays a bounded
    # private pipe and stderr is discarded, including auth-token/provider errors.
    master, slave = pty.openpty()
    process = None
    try:
        process = subprocess.Popen(['az', 'containerapp', 'exec', '--subscription', guard.SUBSCRIPTION,
            '--resource-group', guard.RESOURCE_GROUP, '--name', 'repoagent-auth', '--revision', revision,
            '--replica', replica, '--container', 'auth-service', '--command',
            'dotnet /app/RepoAgent.Auth.dll --version --json', '--only-show-errors'], stdin=slave,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, start_new_session=True,
            env=dict(os.environ, AZURE_CORE_COLLECT_TELEMETRY='0', AZURE_EXTENSION_USE_DYNAMIC_INSTALL='no'))
        os.close(slave)
        slave = None
        deadline, output = time.monotonic()+30, bytearray()
        with selectors.DefaultSelector() as reader:
            reader.register(process.stdout, selectors.EVENT_READ)
            while reader.get_map():
                remaining = deadline-time.monotonic()
                if remaining <= 0:
                    raise guard.Unavailable('console-timeout')
                for key, _ in reader.select(remaining):
                    chunk = os.read(key.fileobj.fileno(), 8192)
                    if not chunk:
                        reader.unregister(key.fileobj)
                    output.extend(chunk)
                    if len(output) > 65536:
                        raise guard.Unavailable('console-response-limit')
        if process.wait(timeout=max(.01, deadline-time.monotonic())):
            raise guard.Unavailable('console-command-failed')
        candidates = []
        for line in output.decode('utf-8', errors='strict').splitlines():
            if line.startswith('{'):
                value = json.loads(line)
                if not isinstance(value, dict) or set(value) != {'component', 'version', 'source'} \
                        or value['component'] != 'broker' or not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', value['version']) \
                        or len(value['version']) > 32 or not re.fullmatch('[0-9a-f]{40}', value['source']):
                    raise guard.Unavailable('unexpected-version-metadata')
                candidates.append(value)
        if len(candidates) != 1:
            raise guard.Unavailable('unexpected-version-metadata')
        return candidates[0]
    except (OSError, UnicodeError, subprocess.TimeoutExpired):
        raise guard.Unavailable('console-command-failed') from None
    finally:
        if process is not None:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
            process.stdout.close()
        if slave is not None:
            os.close(slave)
        os.close(master)


def collect(output):
    image = os.environ.get('AUTH_EXPECTED_IMAGE', '')
    revision = os.environ.get('AUTH_EXPECTED_REVISION', '')
    replica = os.environ.get('AUTH_EXPECTED_REPLICA', '')
    if not re.fullmatch('ghcr.io/repoagent/controller-auth@sha256:[0-9a-f]{64}', image) \
            or not re.fullmatch('repoagent-auth--[a-z0-9-]{1,64}', revision) \
            or not re.fullmatch(re.escape(revision)+'-[a-z0-9-]{1,80}', replica):
        raise guard.Refused('Expected exact selected immutable image, revision and replica')
    source = guard.checked_source()
    if not output.is_absolute() or output.exists():
        raise guard.Refused('Use a new absolute evidence directory')
    output.mkdir(mode=0o700, parents=True)
    report = dict(schema='repoagent.broker-version-probe/v1', source=source, observedAt=dt.datetime.now(dt.timezone.utc).isoformat(),
        target=dict(image=image, revision=revision, replica=replica, container='auth-service'),
        state='unavailable', remoteExitStatusEstablished=False, settlementProven=False,
        limitations='Version metadata observation only. Azure CLI session completion does not establish remote exit status; '
                    'before/after target checks do not lock resources or prove authority settlement.')
    try:
        selection(image, revision, replica)
        metadata = console(revision, replica)
        selection(image, revision, replica)
        report.update(state='observed', metadata=metadata)
    except (guard.Unavailable, ValueError, TypeError, KeyError, AttributeError) as error:
        report['reason'] = str(error) if isinstance(error, guard.Unavailable) else 'invalid-response'
    with (output/'version-probe.json').open('x', encoding='utf-8') as stream:
        os.chmod(stream.name, 0o600)
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print('Version-only console probe: '+report['state']+'; remote exit status and settlement remain unproved.')
    return 0 if report['state'] == 'observed' else 1


if __name__ == '__main__':
    try:
        if len(sys.argv) != 2:
            raise guard.Refused('Expected one new absolute evidence directory')
        sys.exit(collect(Path(sys.argv[1])))
    except (guard.Refused, guard.Unavailable, OSError, ValueError, TypeError, KeyError, AttributeError):
        sys.exit('Version probe refused: source/account/target could not be verified; no raw console output retained.')
