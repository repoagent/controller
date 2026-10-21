#!/usr/bin/env python3
"""Fail-closed source, registry and image contracts for automatic auth delivery."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = 'ghcr.io/repoagent/controller-auth'


def command(args, *, capture=False, cwd=ROOT):
    result = subprocess.run(args, cwd=cwd, capture_output=capture, text=True)
    if result.returncode:
        raise ValueError(f'{args[0]} failed during auth delivery; no provider response is printed')
    return result.stdout if capture else None


def source(root=ROOT):
    for name in ('service/auth/global.json', 'service/auth/verify.sh', 'service/auth/Dockerfile',
                 'infra/auth/validate.sh', 'infra/auth/deploy.sh', 'infra/auth/main.bicep'):
        if not (root / name).is_file():
            raise ValueError(f'Missing {name}; deliver service #30/#31 and infrastructure #47 before auth CI can deploy')
    sdk = json.loads((root / 'service/auth/global.json').read_text()).get('sdk', {}).get('version', '')
    if not re.fullmatch(r'10\.\d+\.\d+', sdk):
        raise ValueError('service/auth/global.json must pin a stable .NET 10 SDK')


def repository():
    if os.environ.get('AUTH_IMAGE_REPOSITORY') != REPOSITORY:
        raise ValueError(f'AUTH_IMAGE_REPOSITORY must be the dedicated {REPOSITORY}')
    return REPOSITORY


def check_publication():
    source()
    uuid = r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}'
    for name in ('AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID'):
        if not re.fullmatch(uuid, os.environ.get(name, '')):
            raise ValueError(f'{name} is missing or invalid')
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
    expected = os.environ.get('AUTH_EXPECTED_SOURCE_SHA')
    if expected is not None and (not re.fullmatch(r'[0-9a-f]{40}', expected)
            or data.get('config', {}).get('Labels', {}).get('org.opencontainers.image.revision') != expected):
        raise ValueError('Image revision differs from the accepted full source SHA')


def publish():
    check_publication()
    sha = os.environ.get('GITHUB_SHA', '')
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('GITHUB_SHA must identify the reviewed source commit')
    image = build_publish(sha, 'service/auth')
    os.environ['AUTH_IMAGE'] = image
    attest()  # Anonymous pull confirms public access to this exact digest.
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write('image=' + image + '\n')


def build_publish(sha, context, bootstrap=False):
    tag = repository() + ':sha-' + sha
    command(['docker', 'build', '--platform', 'linux/amd64', '--label',
             'org.opencontainers.image.revision=' + sha, '--tag', tag, str(context)], capture=bootstrap)
    data = json.loads(command(['docker', 'image', 'inspect', tag], capture=True))[0]
    validate_config(data.get('Config', {}))
    if data.get('Os') != 'linux' or data.get('Architecture') != 'amd64':
        raise ValueError('Built auth image must target Linux amd64')
    if bootstrap and data.get('Config', {}).get('Labels', {}).get('org.opencontainers.image.revision') != sha:
        raise ValueError('Built image revision differs from archived source')
    command(['docker', 'push', tag], capture=bootstrap)
    data = json.loads(command(['docker', 'image', 'inspect', tag], capture=True))[0]
    digests = [value for value in data.get('RepoDigests', [])
               if re.fullmatch(re.escape(REPOSITORY) + r'@sha256:[0-9a-f]{64}', value)]
    if len(digests) != 1:
        raise ValueError('Published image did not yield one unambiguous repository digest')
    return digests[0]


def bootstrap_publish():
    repository()
    sha = os.environ.get('GITHUB_SHA', '')
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('GITHUB_SHA must identify the accepted full source commit')
    if command(['git', 'rev-parse', '--verify', sha + '^{commit}'], capture=True).strip() != sha:
        raise ValueError('Accepted source is not the exact Git commit')
    with tempfile.TemporaryDirectory(prefix='auth-bootstrap-') as directory:
        archive, checkout = Path(directory) / 'source.tar', Path(directory) / 'source'
        checkout.mkdir()
        command(['git', 'archive', '--format=tar', '--output', str(archive), sha], capture=True)
        command(['tar', '-xf', str(archive), '-C', str(checkout)], capture=True)
        source(checkout)
        command(['bash', 'service/auth/verify.sh'], cwd=checkout, capture=True)
        image = build_publish(sha, checkout / 'service/auth', bootstrap=True)
    receipt = dict(source=sha, image=image, publicationComplete=False)
    print(json.dumps(receipt), flush=True)
    os.environ.update(AUTH_IMAGE=image, AUTH_EXPECTED_SOURCE_SHA=sha)
    try:
        attest()
    except ValueError:
        raise ValueError('Publication incomplete; operator must make controller-auth public, grant Actions access, '
                         'then anonymously attest the recorded digest/source; do not rebuild to resume') from None
    print(json.dumps(dict(receipt, publicationComplete=True)))


if __name__ == '__main__':
    try:
        verbs = {'source': source, 'check': check_publication, 'attest': attest, 'publish': publish,
                 'bootstrap-publish': bootstrap_publish}
        if len(sys.argv) != 2 or sys.argv[1] not in verbs:
            raise ValueError('Expected source, check, attest, publish or bootstrap-publish')
        verbs[sys.argv[1]]()
    except (ValueError, OSError, KeyError, IndexError) as error:
        sys.exit(str(error))
