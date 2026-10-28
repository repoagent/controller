#!/usr/bin/env python3
"""Discoverable GitHub Release for one verified, published public baseline controller image.

check ARTIFACT    Require the image's own reported gh/Codex versions to match its inventory.
                  Prints the bundled versions; never contacts GitHub (safe on pull requests).
publish ARTIFACT  Create the release for the anonymously verified immutable image once.
                  An existing release is never replaced.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

REPOSITORY = 'ghcr.io/repoagent/controller'
SCHEMA = 'repoagent.baseline-release/v1'
MARKER = f'<!-- {SCHEMA} -->'


def environment(name, pattern, description):
    value = os.environ.get(name, '')
    if not re.fullmatch(pattern, value):
        raise ValueError(f'{name} must be {description}')
    return value


def bundled(artifact):
    """Versions reported by the shipping image itself, cross-checked against its composed inventory."""
    inventory = json.loads((artifact / 'image-inventory.json').read_text())
    output = (artifact / 'versions.txt').read_text()
    gh = re.findall(r'^gh version (\S+) \(', output, re.M)
    codex = re.findall(r'^codex-cli (\S+)$', output, re.M)
    if len(gh) != 1 or len(codex) != 1:
        raise ValueError('Image versions output must report exactly one gh and one Codex version')
    if gh[0] != inventory['githubCli']['version'] or codex[0] != inventory['agent']['version']:
        raise ValueError('Bundled gh/Codex versions differ from the image inventory')
    controller = environment('SOURCE_COMMIT' if os.environ.get('SOURCE_COMMIT') else 'GITHUB_SHA', r'[0-9a-f]{40}', 'the full source commit')
    versions = {'controller': controller, 'gh': gh[0], 'codex': codex[0]}
    if 'version' in inventory.get('controller', {}):
        version = inventory['controller']['version']
        if not re.fullmatch(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)', version):
            raise ValueError('Controller inventory version must be stable SemVer')
        actual = re.findall(r'^RepoAgentController version: ([0-9A-Za-z.+-]+)$', output, re.M)
        if len(actual) != 1 or actual[0].split('+', 1)[0] != version:
            raise ValueError('Compiled controller version differs from the image inventory')
        notes = hashlib.sha256((artifact / 'RELEASE_NOTES.md').read_bytes()).hexdigest()
        if notes != inventory['controller'].get('releaseNotesSha256'):
            raise ValueError('Bundled release notes differ from the image inventory')
        versions.update(version=version, notesSha256=notes)
    return versions


def describe(artifact):
    versions = bundled(artifact)
    image = environment('IMAGE', re.escape(REPOSITORY) + r'@sha256:[0-9a-f]{64}',
                        f'the published {REPOSITORY}@sha256 digest')
    reference = re.fullmatch(re.escape(REPOSITORY) + r':sha-([0-9a-f]{40})-([0-9]+)-([0-9]+)',
                             os.environ.get('REFERENCE', ''))
    if reference is None or reference[1] != versions['controller']:
        raise ValueError('REFERENCE must be the published image tag for this source commit')
    config_digest = json.loads((artifact / 'build.json').read_text()).get('containerimage.config.digest')
    if not isinstance(config_digest, str) or not re.fullmatch(r'sha256:[0-9a-f]{64}', config_digest):
        raise ValueError('Build metadata lacks the image config digest')
    server = environment('GITHUB_SERVER_URL', r'https://[a-z0-9.-]+', 'an HTTPS origin')
    repository = environment('GITHUB_REPOSITORY', r'[\w.-]+/[\w.-]+', 'OWNER/REPO')
    run = environment('GITHUB_RUN_ID', r'[0-9]+', 'numeric')
    return {
        'schema': SCHEMA,
        'release': f'baseline-{reference[2]}.{reference[3]}',
        'platform': 'linux/amd64',
        'image': image,
        'imageTag': reference[0],
        'imageConfigDigest': config_digest,
        'versions': versions,
        'inventorySha256': hashlib.sha256((artifact / 'image-inventory.json').read_bytes()).hexdigest(),
        'run': f'{server}/{repository}/actions/runs/{run}',
    }


def notes(value):
    versions = value['versions']
    guide = value['run'].split('/actions/', 1)[0] + f"/blob/{versions['controller']}/docs/ci-cd.md#baseline-releases"
    return '\n'.join([
        f"Public linux/amd64 baseline controller image, verified and published from `{versions['controller']}`.",
        '',
        '```sh',
        f"docker pull {value['image']}",
        '```',
        '',
        '| Bundled | Version |',
        '| --- | --- |',
        f"| Controller | `{versions['controller']}` |",
        f"| GitHub CLI | `{versions['gh']}` |",
        f"| Codex CLI | `{versions['codex']}` |",
        '',
        'Publication does not update running controllers. Recreate or update a controller deliberately with '
        f"this immutable image; see [baseline releases]({guide}). [Verification and publication run]({value['run']}).",
        '',
        MARKER,
        '```json',
        json.dumps(value, indent=2, sort_keys=True),
        '```',
        '',
    ])


def check(artifact):
    print(json.dumps(bundled(artifact), sort_keys=True))


def publish(artifact):
    # Retained legacy recovery command; semantic releases use controller-release.py.
    for line in (artifact / 'SHA256SUMS').read_text().splitlines():
        match = re.fullmatch(r'([0-9a-f]{64})  ([A-Za-z0-9_.-]+)', line)
        if not match or hashlib.sha256((artifact / match[2]).read_bytes()).hexdigest() != match[1]:
            raise ValueError('Transferred artifact checksum differs')
    value = describe(artifact)
    tag, versions = value['release'], value['versions']
    if subprocess.run(['gh', 'release', 'view', tag], capture_output=True).returncode == 0:
        raise ValueError(f'Release {tag} already exists; it is preserved. Inspect it before any manual recovery.')
    title = f"Controller baseline {versions['controller'][:12]} (gh {versions['gh']}, Codex {versions['codex']})"
    created = subprocess.run(['gh', 'release', 'create', tag, '--target', versions['controller'],
                              '--title', title, '--notes', notes(value)])
    if created.returncode:
        raise ValueError(f'Creating release {tag} failed; inspect whether it exists before rerunning the release job')


if __name__ == '__main__':
    try:
        verbs = {'check': check, 'publish': publish}
        if len(sys.argv) != 3 or sys.argv[1] not in verbs:
            raise ValueError('Expected check ARTIFACT or publish ARTIFACT')
        verbs[sys.argv[1]](Path(sys.argv[2]))
    except (ValueError, OSError, KeyError, TypeError) as error:
        sys.exit(f'Baseline release refused: {error}')
