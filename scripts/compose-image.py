#!/usr/bin/env python3
"""Validate nonsecret image choices and emit an isolated, reproducible build context.

Runs inside the pinned helper container; also callable with Python for development.
No subprocesses, network, credential lookup, or host configuration are needed.
"""
import argparse
import hashlib
import json
import re
from pathlib import Path
import sys

SDK = 'mcr.microsoft.com/dotnet/sdk@sha256:bfabf51e74c5a7cf2fc4eac852145db86f4867d475257d809518d6a2b0fe71e1'
RUNTIME = 'mcr.microsoft.com/dotnet/runtime-deps@sha256:f41a8006ae1231098108901b6faa4db8e4a31fe83f77523170b8793f3170c089'
SNAPSHOT = '20261020T000000Z'
# Exact reviewed artifacts. New versions require a reviewed catalog update.
ARTIFACTS = {
    'codex': ('0.163.0', 'https://github.com/openai/codex/releases/download/rust-v0.163.0/codex-x86_64-unknown-linux-musl.tar.gz', 'beb339e8ecd9ba057a6bb2dc81ad8ca474812edfbf0fc5056ca818797648dde4'),
    'codex-code-mode-host': ('0.163.0', 'https://github.com/openai/codex/releases/download/rust-v0.163.0/codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz', '314263128ba6592e7e807c265deeaf928a692b72dcbfeb5e0b20c9423b703529'),
    'gh': ('2.102.0', 'https://github.com/cli/cli/releases/download/v2.102.0/gh_2.102.0_linux_amd64.tar.gz', 'bb766f710eef8ede859c18578c72c327597cd4c8a85b06001b1f3843c6019386'),
    'cloudflared': ('2026.10.1', 'https://github.com/cloudflare/cloudflared/releases/download/2026.10.1/cloudflared-linux-amd64', '3bfc539f45d2620c71debb7a86be0efa737ed5ccbad9eb85c787116900e5c586'),
}
# Explicit files, not recursive directory copies: unexpected files cannot enter.
SOURCE_FILES = [
    'global.json', '.agents/skills/github-discussion/SKILL.md',
    *['src/RepoAgentController/' + name for name in (
        'AppGitHubCommand.cs', 'Bridge.cs', 'BridgeCommand.cs', 'BrokerTokenSource.cs',
        'Codex.cs', 'DirectAppTokenSource.cs', 'DiscussionWebhooks.cs', 'GitHub.cs',
        'GitHubAppCredentials.cs', 'MentionUser.cs', 'Program.cs', 'RepoAgentController.csproj', 'StateStore.cs')],
    'tests/NativeSmoke/NativeSmoke.csproj', 'tests/NativeSmoke/Program.cs',
    'tests/RepoAgent.LiveTests/RepoAgent.LiveTests.csproj', 'tests/RepoAgent.LiveTests/DiscussionJourney.cs',
    'container/Containerfile', 'container/runtime.py', 'container/control.py',
    'container/quick_tunnel.py', 'container/update_tools.py',
    'container/install-tools.sh', 'container/install-cloudflared.sh', 'container/prepare-runtime.sh',
]


def object_keys(value, required, optional=()):
    if not isinstance(value, dict) or set(value) - set(required) - set(optional) or set(required) - set(value):
        raise ValueError('Unexpected or missing manifest fields')


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate manifest field')
        result[key] = value
    return result


def validate(path):
    value = json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=unique_object)
    object_keys(value, ('schemaVersion', 'platform', 'controller', 'agent', 'githubCli', 'tools'), ('baseImage',))
    if type(value['schemaVersion']) is not int or value['schemaVersion'] != 1:
        raise ValueError('Unsupported schemaVersion; expected 1')
    if value['platform'] != 'linux/amd64':
        raise ValueError('Only linux/amd64 is supported')
    if 'baseImage' in value:
        object_keys(value['baseImage'], ('reference',))
        reference = value['baseImage']['reference']
        if not isinstance(reference, str) or not re.fullmatch(r'(?:[a-z0-9][a-z0-9._:/-]*@)?sha256:[0-9a-f]{64}', reference):
            raise ValueError('baseImage.reference must be a digest-pinned registry/repository@sha256 identity')
    object_keys(value['controller'], ('source',), ('sha256',))
    if value['controller']['source'] != 'checkout':
        raise ValueError('Only controller source checkout is supported')
    digest = value['controller'].get('sha256')
    if digest is not None and (not isinstance(digest, str) or len(digest) != 64 or any(c not in '0123456789abcdef' for c in digest)):
        raise ValueError('Controller sha256 must be 64 lowercase hex digits')
    object_keys(value['agent'], ('profile', 'version'))
    if value['agent']['profile'] != 'codex':
        raise ValueError('Only the codex agent profile is supported')
    object_keys(value['githubCli'], ('version',))
    object_keys(value['tools'], (), ('cloudflared', 'developerUtilities'))
    if 'developerUtilities' in value['tools'] and type(value['tools']['developerUtilities']) is not bool:
        raise ValueError('tools.developerUtilities must be a boolean')
    for name, version in [('codex', value['agent']['version']), ('gh', value['githubCli']['version']), *[(key, version) for key, version in value['tools'].items() if key != 'developerUtilities']]:
        if version != ARTIFACTS[name][0]:
            raise ValueError(f'Unsupported {name} version; use the reviewed catalog version')
    return value


def regular_file(path):
    if any(p.is_symlink() for p in [path, *path.parents]) or not path.is_file():
        raise ValueError('Build inputs must be regular files without symlink ancestors')
    return path.read_bytes()


def compose(root, manifest, output, derived=None):
    value = validate(manifest)
    inputs = {name: regular_file(root / name) for name in SOURCE_FILES}
    digest = hashlib.sha256()
    for name, content in sorted(inputs.items()):
        digest.update(name.encode() + b'\0' + hashlib.sha256(content).digest())
    source_digest = digest.hexdigest()
    if value['controller'].get('sha256', source_digest) != source_digest:
        raise ValueError('Controller source sha256 does not match this checkout')
    if output.exists() and any(output.iterdir()):
        raise ValueError('Output context must be empty')
    extension = regular_file(derived) if derived else None
    inventory = dict(value, controller={'source': 'checkout', 'sha256': source_digest},
                     baseImages={'sdk': SDK, 'runtime': value.get('baseImage', {}).get('reference', RUNTIME)}, ubuntuSnapshot=SNAPSHOT,
                     artifacts={key: {'version': v, 'url': url, 'sha256': sha} for key, (v, url, sha) in ARTIFACTS.items()
                                if key != 'cloudflared' or key in value['tools']})
    if extension is not None:
        inventory['derivedDockerfileSha256'] = hashlib.sha256(extension).hexdigest()
    output.mkdir(parents=True, exist_ok=True)
    for name, content in inputs.items():
        destination = output / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(content)
    (output / 'image-inventory.json').write_text(json.dumps(inventory, indent=2, sort_keys=True) + '\n')
    reference = inventory['baseImages']['runtime']
    (output / 'base-reference.txt').write_text(reference + '\n')
    from_reference = 'repoagent-local-base:' + reference[7:] if reference.startswith('sha256:') else reference
    dockerfile = (output / 'container/Containerfile').read_text()
    dockerfile = dockerfile.replace('mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2', SDK).replace('mcr.microsoft.com/dotnet/runtime-deps:11.0.0-rc.2', from_reference)
    dockerfile = dockerfile.replace('RUN apt-get update', f'RUN sed -i -E "s|https?://[^ /]+/ubuntu/?|https://snapshot.ubuntu.com/ubuntu/{SNAPSHOT}/|g" /etc/apt/sources.list.d/ubuntu.sources && apt-get update')
    dockerfile = dockerfile.replace('RUN bash /tmp/prepare-runtime.sh', 'RUN bash /tmp/prepare-runtime.sh ' + ('true' if value['tools'].get('developerUtilities', False) else 'false'))
    if 'cloudflared' not in value['tools']:
        dockerfile = '\n'.join(line for line in dockerfile.splitlines() if 'install-cloudflared.sh' not in line) + '\n'
    (output / 'container/Containerfile').write_text(dockerfile)
    if extension is not None:
        (output / 'Derived.Dockerfile').write_bytes(extension)
    return inventory


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--derived-dockerfile', type=Path)
    args = parser.parse_args()
    try:
        inventory = compose(args.source, args.manifest, args.output, args.derived_dockerfile)
    except (ValueError, OSError) as error:
        # Never echo manifest values or arbitrary input file contents.
        print(f'Image composition failed: {error.__class__.__name__}: {error if isinstance(error, ValueError) and not isinstance(error, json.JSONDecodeError) else "invalid or unreadable input"}', file=sys.stderr)
        return 2
    print(json.dumps(inventory, sort_keys=True))
    return 0


if __name__ == '__main__':
    sys.exit(main())
