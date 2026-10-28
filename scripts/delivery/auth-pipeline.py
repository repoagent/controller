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
    for name in ('RepoAgent.slnx', 'scripts/delivery/verify-dotnet.sh', 'global.json', 'Directory.Build.props', '.editorconfig', 'service/auth/Dockerfile.dockerignore', 'service/auth/global.json', 'service/auth/verify.sh', 'service/auth/Dockerfile',
                 'infra/auth/deploy.sh', 'infra/auth/main.bicep'):
        if not (root / name).is_file():
            raise ValueError(f'Missing {name}; deliver service #30/#31 and infrastructure #47 before auth CI can deploy')
    policy = json.loads((root / 'global.json').read_text()).get('sdk', {})
    broker_policy = json.loads((root / 'service/auth/global.json').read_text()).get('sdk', {})
    if (not re.fullmatch(r'11\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?', policy.get('version', ''))
            or policy.get('rollForward') != 'disable' or policy.get('allowPrerelease') is not True):
        raise ValueError('global.json must pin the reviewed .NET 11 SDK without roll-forward')
    if broker_policy != policy:
        raise ValueError('Root and broker SDK policies must match exactly')


def repository():
    if os.environ.get('AUTH_IMAGE_REPOSITORY') != REPOSITORY:
        raise ValueError(f'AUTH_IMAGE_REPOSITORY must be the dedicated {REPOSITORY}')
    return REPOSITORY


def accepted_source():
    sha = os.environ.get('AUTH_EXPECTED_SOURCE_SHA', '')
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('Expected an accepted full main source SHA')
    def git(*args):
        return command(['git', *args], capture=True).strip()
    remote = git('ls-remote', 'https://github.com/repoagent/controller.git', 'refs/heads/main').split()
    if (os.environ.get('GITHUB_REF') != 'refs/heads/main' or os.environ.get('GITHUB_SHA') != sha
            or git('rev-parse', 'HEAD') != sha or remote != [sha, 'refs/heads/main']):
        raise ValueError('Accepted source differs from checkout or current main')
    return sha


def check_publication():
    source()
    repository()


def validate_config(config):
    if (config.get('Labels') or {}).get('io.repoagent.role') != 'auth-service':
        raise ValueError('Image must declare io.repoagent.role=auth-service')
    user = config.get('User', '')
    if not re.fullmatch(r'[1-9][0-9]*(?::[0-9]+)?', user):
        raise ValueError('Auth image must configure an explicit nonzero numeric USER')


def broker_version(root=Path('.')):
    path = Path(root) / 'service/auth/version.txt'
    if not path.exists():
        return None  # Historical source archives predate semantic broker versions.
    version = path.read_text().strip()
    if not re.fullmatch(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)', version):
        raise ValueError('Broker version must be a stable MAJOR.MINOR.PATCH version')
    return version


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
    image_source = os.environ.get('AUTH_EXPECTED_IMAGE_SOURCE_SHA')
    guarded_source = os.environ.get('AUTH_EXPECTED_SOURCE_SHA')
    if image_source and guarded_source is not None and image_source != guarded_source:
        raise ValueError('Selected image source conflicts with the guarded activation source')
    expected = image_source or guarded_source
    if expected is not None and (not re.fullmatch(r'[0-9a-f]{40}', expected)
            or data.get('config', {}).get('Labels', {}).get('org.opencontainers.image.revision') != expected):
        raise ValueError('Image revision differs from the accepted full source SHA')
    version = os.environ.get('AUTH_EXPECTED_BROKER_VERSION') or None
    if version is not None and (not re.fullmatch(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)', version)
            or data.get('config', {}).get('Labels', {}).get('org.opencontainers.image.version') != version):
        raise ValueError('Image version differs from the accepted broker release')


def publish():
    check_publication()
    sha = os.environ.get('GITHUB_SHA', '')
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('GITHUB_SHA must identify the reviewed source commit')
    image = build_publish(sha, '.')
    os.environ.update(AUTH_IMAGE=image, AUTH_EXPECTED_SOURCE_SHA=sha)
    version = broker_version()
    if version is not None:
        os.environ['AUTH_EXPECTED_BROKER_VERSION'] = version
    attest()  # Anonymous pull confirms public access to this exact digest.
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write('image=' + image + '\n')
        if version is not None:
            output.write('version=' + version + '\n')


def build_publish(sha, context, bootstrap=False, *, before_push=None):
    tag = repository() + ':sha-' + sha
    version = broker_version(Path(context))
    arguments = ['--build-arg', 'BROKER_VERSION=' + version, '--build-arg', 'SOURCE_REVISION=' + sha,
                 '--label', 'org.opencontainers.image.version=' + version] if version is not None else []
    command(['docker', 'build', '--platform', 'linux/amd64', *arguments, '--label',
             'org.opencontainers.image.revision=' + sha, '--tag', tag, '--file', str(Path(context) / 'service/auth/Dockerfile'), str(context)], capture=bootstrap)
    data = json.loads(command(['docker', 'image', 'inspect', tag], capture=True))[0]
    validate_config(data.get('Config', {}))
    if data.get('Os') != 'linux' or data.get('Architecture') != 'amd64':
        raise ValueError('Built auth image must target Linux amd64')
    if bootstrap and data.get('Config', {}).get('Labels', {}).get('org.opencontainers.image.revision') != sha:
        raise ValueError('Built image revision differs from archived source')
    if version is not None:
        labels = data.get('Config', {}).get('Labels', {})
        if labels.get('org.opencontainers.image.version') != version or labels.get('org.opencontainers.image.revision') != sha:
            raise ValueError('Built image version/source differs from approved broker source')
        diagnostic = json.loads(command(['docker', 'run', '--rm', '--network', 'none', tag, '--version', '--json'], capture=True))
        if diagnostic != dict(component='broker', version=version, source=sha):
            raise ValueError('Compiled broker diagnostics differ from image version/source')
    if before_push is not None:
        before_push()
    command(['docker', 'push', tag], capture=bootstrap)
    data = json.loads(command(['docker', 'image', 'inspect', tag], capture=True))[0]
    digests = [value for value in data.get('RepoDigests', [])
               if re.fullmatch(re.escape(REPOSITORY) + r'@sha256:[0-9a-f]{64}', value)]
    if len(digests) != 1:
        raise ValueError('Published image did not yield one unambiguous repository digest')
    return digests[0]


def bootstrap_publish(*, before_push=None):
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
        image = build_publish(sha, checkout, bootstrap=True, before_push=before_push)
    receipt = dict(source=sha, image=image, publicationComplete=False)
    print(json.dumps(receipt), flush=True)
    os.environ.update(AUTH_IMAGE=image, AUTH_EXPECTED_SOURCE_SHA=sha)
    try:
        attest()
    except ValueError:
        raise ValueError('Publication incomplete; operator must make controller-auth public, grant Actions access, '
                         'then anonymously attest the recorded digest/source; do not rebuild to resume') from None
    if os.environ.get('AUTH_PACKAGE_ACTIONS_ACCESS_CONFIRMED') != 'true':
        raise ValueError('Public digest attested; publication incomplete until operator confirms repository Actions package access')
    print(json.dumps(dict(receipt, publicationComplete=True)))


def bootstrap_publish_actions():
    def guard():
        try:
            accepted_source()
        except (ValueError, subprocess.SubprocessError):
            raise ValueError('Cannot verify accepted current main source; bootstrap refused') from None

    guard()
    bootstrap_publish(before_push=guard)


if __name__ == '__main__':
    try:
        verbs = {'source': source, 'source-check': accepted_source, 'check': check_publication, 'attest': attest, 'publish': publish,
                 'bootstrap-publish': bootstrap_publish, 'bootstrap-publish-actions': bootstrap_publish_actions}
        if len(sys.argv) != 2 or sys.argv[1] not in verbs:
            raise ValueError('Expected source, source-check, check, attest, publish, bootstrap-publish or bootstrap-publish-actions')
        verbs[sys.argv[1]]()
    except (ValueError, OSError, KeyError, IndexError) as error:
        sys.exit(str(error))
