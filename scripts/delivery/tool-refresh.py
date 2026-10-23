#!/usr/bin/env python3
"""Select supported upstream gh and Codex releases and pin them for a verified baseline image.

select DIRECTORY   Compare the latest stable upstream releases with this checkout's catalog.
                   Writes DIRECTORY/selection.json and changed=true only for newer supported
                   versions; never changes the checkout. A newer but unusable release fails.
apply SELECTION    Pin the selection in this checkout's catalog (scripts/compose-image.py and
                   image/default.json), so verification and composition use exactly it.
commit SELECTION   Pin the verified selection on main in one commit that GitHub accepts only
                   while main is still the verified commit.
"""
import base64
import json
import os
from pathlib import Path
import re
import runpy
import subprocess
import sys

CATALOG = Path('scripts/compose-image.py')
MANIFEST = Path('image/default.json')
SCHEMA = 'repoagent.tool-selection/v1'
# Catalog artifact -> the upstream product that releases it.
PRODUCTS = {'codex': 'codex', 'codex-code-mode-host': 'codex', 'gh': 'gh'}
# Product -> (upstream repository, stable release tag prefix, catalog artifact -> release asset name).
UPSTREAM = {
    'gh': ('cli/cli', 'v', lambda version: {'gh': f'gh_{version}_linux_amd64.tar.gz'}),
    'codex': ('openai/codex', 'rust-v', lambda version: {
        'codex': 'codex-x86_64-unknown-linux-musl.tar.gz',
        'codex-code-mode-host': 'codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz'}),
}
LABELS = {'gh': 'gh', 'codex': 'Codex'}
COMMIT = 'mutation($input: CreateCommitOnBranchInput!) { createCommitOnBranch(input: $input) { commit { oid } } }'


def version_key(version):
    return tuple(int(part) for part in version.split('.'))


def download(product, version, name):
    repository, prefix, _ = UPSTREAM[product]
    return f'https://github.com/{repository}/releases/download/{prefix}{version}/{name}'


def pinned():
    """Product versions of this checkout's default manifest, which must match its catalog."""
    manifest = json.loads(MANIFEST.read_text())
    versions = {'gh': manifest['githubCli']['version'], 'codex': manifest['agent']['version']}
    catalog = runpy.run_path(str(CATALOG))['ARTIFACTS']
    if any(catalog[tool][0] != versions[product] for tool, product in PRODUCTS.items()):
        raise ValueError('The default manifest differs from the reviewed catalog')
    return versions, {tool: dict(zip(('version', 'url', 'sha256'), catalog[tool])) for tool in PRODUCTS}


def latest(repository):
    result = subprocess.run(['gh', 'api', f'repos/{repository}/releases/latest'], capture_output=True, text=True)
    if result.returncode:
        raise ValueError(f'Reading the latest {repository} release failed')
    return json.loads(result.stdout)


def newer(product, release, current):
    """Catalog artifacts for a newer supported stable release, or None to keep the current pin."""
    repository, prefix, assets = UPSTREAM[product]
    match = re.fullmatch(re.escape(prefix) + r'(\d+\.\d+\.\d+)', release.get('tag_name') or '')
    if match is None or release.get('draft') is not False or release.get('prerelease') is not False:
        raise ValueError(f'The latest {repository} release is not a supported stable release')
    version = match[1]
    if version_key(version) <= version_key(current):
        return None
    published = {asset.get('name'): asset for asset in release.get('assets') or []}
    artifacts = {}
    for tool, name in assets(version).items():
        asset = published.get(name) or {}
        digest = asset.get('digest') or ''
        url = download(product, version, name)
        if asset.get('browser_download_url') != url or not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
            raise ValueError(f'{repository} {version} has no checksummed {name} release asset')
        artifacts[tool] = {'version': version, 'url': url, 'sha256': digest.removeprefix('sha256:')}
    return artifacts


def select(directory):
    current, artifacts = pinned()
    changes = []
    for product, (repository, _, _) in UPSTREAM.items():
        update = newer(product, latest(repository), current[product])
        if update:
            artifacts.update(update)
            changes.append(f"{LABELS[product]} {current[product]} -> {update[product]['version']}")
    if changes:
        directory.mkdir(parents=True, exist_ok=True)
        selection = {'schema': SCHEMA, 'base': os.environ['GITHUB_SHA'], 'pinned': current, 'artifacts': artifacts}
        (directory / 'selection.json').write_text(json.dumps(selection, indent=2, sort_keys=True) + '\n')
    summary = ', '.join(changes) or f"gh {current['gh']} and Codex {current['codex']} are current"
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write(f"changed={'true' if changes else 'false'}\n")
    print(('Selected for verification: ' if changes else 'No supported tool update: ') + summary)


def load(path):
    value = json.loads(Path(path).read_text())
    artifacts = value.get('artifacts')
    if value.get('schema') != SCHEMA or not re.fullmatch(r'[0-9a-f]{40}', value.get('base') or '') \
            or not isinstance(artifacts, dict) or set(artifacts) != set(PRODUCTS):
        raise ValueError('Not a tool selection')
    for product, (_, _, assets) in UPSTREAM.items():
        version = artifacts[product].get('version') or ''
        if not re.fullmatch(r'\d+\.\d+\.\d+', version):
            raise ValueError('Selection has an unsupported version')
        for tool, name in assets(version).items():
            artifact = artifacts[tool]
            if artifact != {'version': version, 'url': download(product, version, name), 'sha256': artifact.get('sha256')} \
                    or not re.fullmatch(r'[0-9a-f]{64}', artifact['sha256'] or ''):
                raise ValueError(f'Selection has an unsupported {tool} artifact')
    return value


def pin(value):
    current, _ = pinned()
    if current != value['pinned']:
        raise ValueError('This checkout is not the catalog the selection was made from')
    text = CATALOG.read_text()
    for tool in PRODUCTS:
        artifact = value['artifacts'][tool]
        line = f"    '{tool}': ('{artifact['version']}', '{artifact['url']}', '{artifact['sha256']}'),"
        text, count = re.subn(rf"(?m)^    '{re.escape(tool)}': \('[^'\n]*', '[^'\n]*', '[^'\n]*'\),$",
                              lambda _: line, text)
        if count != 1:
            raise ValueError('The reviewed catalog layout changed; update this script with it')
    CATALOG.write_text(text)
    manifest = json.loads(MANIFEST.read_text())
    manifest['agent']['version'] = value['artifacts']['codex']['version']
    manifest['githubCli']['version'] = value['artifacts']['gh']['version']
    MANIFEST.write_text(json.dumps(manifest, indent=2) + '\n')
    if pinned()[1] != value['artifacts']:
        raise ValueError('The catalog does not read back as the selection')


def apply(path):
    value = load(path)
    pin(value)
    print(f"Pinned gh {value['artifacts']['gh']['version']} and Codex {value['artifacts']['codex']['version']}.")


def commit(path):
    value = load(path)
    base = os.environ.get('GITHUB_SHA')
    if value['base'] != base:
        raise ValueError('The selection was verified for another commit; nothing was pinned')
    pin(value)
    gh, codex = value['artifacts']['gh']['version'], value['artifacts']['codex']['version']
    run = f"{os.environ['GITHUB_SERVER_URL']}/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
    body = '\n'.join([
        f'Latest stable upstream releases, verified with the selected tools by {run}.', '',
        *[f"{LABELS[product]} {value['pinned'][product]} -> {value['artifacts'][product]['version']}"
          for product in UPSTREAM if value['pinned'][product] != value['artifacts'][product]['version']]])
    change = {'branch': {'repositoryNameWithOwner': os.environ['GITHUB_REPOSITORY'], 'branchName': 'main'},
              'expectedHeadOid': base,
              'message': {'headline': f'chore: refresh baseline tools to gh {gh} and Codex {codex}', 'body': body},
              'fileChanges': {'additions': [{'path': str(file), 'contents': base64.b64encode(file.read_bytes()).decode()}
                                            for file in (CATALOG, MANIFEST)]}}
    result = subprocess.run(['gh', 'api', 'graphql', '--input', '-'], capture_output=True, text=True,
                            input=json.dumps({'query': COMMIT, 'variables': {'input': change}}))
    created = json.loads(result.stdout or '{}') if result.returncode == 0 else {}
    oid = (((created.get('data') or {}).get('createCommitOnBranch') or {}).get('commit') or {}).get('oid')
    if not re.fullmatch(r'[0-9a-f]{40}', oid or ''):
        raise ValueError('main changed after verification or refused the pin; nothing was published. '
                         'The next refresh selects and verifies again.')
    print(f'Pinned gh {gh} and Codex {codex} on main as {oid}.')


if __name__ == '__main__':
    try:
        verbs = {'select': lambda argument: select(Path(argument)), 'apply': apply, 'commit': commit}
        if len(sys.argv) != 3 or sys.argv[1] not in verbs:
            raise ValueError('Expected select DIRECTORY, apply SELECTION or commit SELECTION')
        verbs[sys.argv[1]](sys.argv[2])
    except (ValueError, OSError, KeyError, TypeError, AttributeError, json.JSONDecodeError) as error:
        sys.exit(f'Tool refresh refused: {error}')
