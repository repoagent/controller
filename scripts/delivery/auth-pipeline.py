#!/usr/bin/env python3
"""Fail-closed source, registry and image contracts for automatic auth delivery."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = 'ghcr.io/repoagent/controller-auth'


def command(args, *, capture=False):
    result = subprocess.run(args, cwd=ROOT, capture_output=capture, text=True)
    if result.returncode:
        raise ValueError(f'{args[0]} failed during auth delivery; no provider response is printed')
    return result.stdout if capture else None


def source():
    for name in ('service/auth/global.json', 'service/auth/verify.sh', 'service/auth/Dockerfile',
                 'infra/auth/validate.sh', 'infra/auth/deploy.sh', 'infra/auth/main.bicep'):
        if not (ROOT / name).is_file():
            raise ValueError(f'Missing {name}; deliver service #30/#31 and infrastructure #47 before auth CI can deploy')
    sdk = json.loads((ROOT / 'service/auth/global.json').read_text()).get('sdk', {}).get('version', '')
    if not re.fullmatch(r'10\.\d+\.\d+', sdk):
        raise ValueError('service/auth/global.json must pin a stable .NET 10 SDK')


def repository():
    if os.environ.get('AUTH_IMAGE_REPOSITORY') != REPOSITORY:
        raise ValueError(f'AUTH_IMAGE_REPOSITORY must be the dedicated {REPOSITORY}')
    return REPOSITORY


def check_publication():
    source()
    repository()
    if os.environ.get('AUTH_DEPLOYMENT_READY') != 'true':
        raise ValueError('AUTH_DEPLOYMENT_READY must be true after service and infrastructure acceptance')
    # Infrastructure owns its readiness and versioned-reference contract.
    command(['bash', 'infra/auth/deploy.sh', 'check'])
    try:
        package = json.loads(command(['gh', 'api', 'users/repoagent/packages/container/controller-auth'], capture=True))
    except ValueError:
        raise ValueError('GHCR package metadata is unavailable; bootstrap public controller-auth and grant repository Actions access') from None
    if package.get('visibility') != 'public':
        raise ValueError('GHCR controller-auth package must already exist and be public before automatic publication')


def validate_config(config):
    if (config.get('Labels') or {}).get('io.repoagent.role') != 'auth-service':
        raise ValueError('Image must declare io.repoagent.role=auth-service')
    user = config.get('User', '')
    if not re.fullmatch(r'[1-9][0-9]*(?::[0-9]+)?', user):
        raise ValueError('Auth image must configure an explicit nonzero numeric USER')


def attest():
    repo = repository()
    image = os.environ.get('AUTH_IMAGE', '')
    if not re.fullmatch(re.escape(repo) + r'@sha256:[0-9a-f]{64}', image):
        raise ValueError('AUTH_IMAGE must be the dedicated repository with an immutable SHA256 digest')
    data = json.loads(command(['skopeo', 'inspect', '--no-creds', '--config', '--override-os', 'linux',
                              '--override-arch', 'amd64', 'docker://' + image], capture=True))
    if data.get('os') != 'linux' or data.get('architecture') != 'amd64':
        raise ValueError('Auth image must support Linux amd64')
    validate_config(data.get('config', {}))


def publish():
    check_publication()
    sha = os.environ.get('GITHUB_SHA', '')
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('GITHUB_SHA must identify the reviewed source commit')
    tag = repository() + ':sha-' + sha
    command(['docker', 'build', '--platform', 'linux/amd64', '--label',
             'org.opencontainers.image.revision=' + sha, '--tag', tag, 'service/auth'])
    data = json.loads(command(['docker', 'image', 'inspect', tag], capture=True))[0]
    validate_config(data.get('Config', {}))
    if data.get('Os') != 'linux' or data.get('Architecture') != 'amd64':
        raise ValueError('Built auth image must target Linux amd64')
    command(['docker', 'push', tag])
    data = json.loads(command(['docker', 'image', 'inspect', tag], capture=True))[0]
    digests = [value for value in data.get('RepoDigests', [])
               if re.fullmatch(re.escape(REPOSITORY) + r'@sha256:[0-9a-f]{64}', value)]
    if len(digests) != 1:
        raise ValueError('Published image did not yield one unambiguous repository digest')
    os.environ['AUTH_IMAGE'] = digests[0]
    attest()  # Anonymous pull after publication confirms public access to this exact digest.
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write('image=' + digests[0] + '\n')


if __name__ == '__main__':
    try:
        verbs = {'source': source, 'check': check_publication, 'attest': attest, 'publish': publish}
        if len(sys.argv) != 2 or sys.argv[1] not in verbs:
            raise ValueError('Expected source, check, attest or publish')
        verbs[sys.argv[1]]()
    except (ValueError, OSError, KeyError, IndexError) as error:
        sys.exit(str(error))
