#!/usr/bin/env python3
"""Select a supported upstream gh or Codex release and pin it for a verified baseline image.

Each tool is selected, verified and pinned on its own, so an unusable or incompatible release of
one tool never holds back a verified update of the other.

select TOOL DIRECTORY  Compare TOOL's (gh or codex) latest stable upstream release with this
                       checkout's catalog. Writes DIRECTORY/selection.json, changed=true and the
                       checked-out base commit only for a newer supported version; never changes
                       the checkout. A newer but unusable release fails.
apply SELECTION        Pin the selection in this checkout's catalog (scripts/compose-image.py and
                       image/default.json), so verification and composition use exactly it.
commit SELECTION       Pin the verified selection on main in one commit that GitHub accepts only
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
SCHEMA = 'repoagent.tool-selection/v2'
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
# Product -> its section in the default manifest.
SECTIONS = {'gh': 'githubCli', 'codex': 'agent'}
COMMIT = 'mutation($input: CreateCommitOnBranchInput!) { createCommitOnBranch(input: $input) { commit { oid } } }'


def version_key(version):
    return tuple(int(part) for part in version.split('.'))


def download(product, version, name):
    repository, prefix, _ = UPSTREAM[product]
    return f'https://github.com/{repository}/releases/download/{prefix}{version}/{name}'


def pinned():
    """Product versions of this checkout's default manifest, which must match its catalog."""
    manifest = json.loads(MANIFEST.read_text())
    versions = {product: manifest[section]['version'] for product, section in SECTIONS.items()}
    catalog = runpy.run_path(str(CATALOG))['ARTIFACTS']
    if any(catalog[tool][0] != versions[product] for tool, product in PRODUCTS.items()):
        raise ValueError('The default manifest differs from the reviewed catalog')
    return versions, {tool: dict(zip(('version', 'url', 'sha256'), catalog[tool])) for tool in PRODUCTS}


def checked_out():
    """The commit this checkout's files come from."""
    result = subprocess.run(['git', 'rev-parse', '--verify', 'HEAD^{commit}'], capture_output=True, text=True)
    if result.returncode or not re.fullmatch(r'[0-9a-f]{40}', result.stdout.strip()):
        raise ValueError('This checkout has no commit to select from')
    return result.stdout.strip()


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


def select(product, directory):
    if product not in UPSTREAM:
        raise ValueError(f"Unknown tool {product!r}; expected {' or '.join(UPSTREAM)}")
    base = checked_out()
    current, _ = pinned()
    update = newer(product, latest(UPSTREAM[product][0]), current[product])
    if update:
        directory.mkdir(parents=True, exist_ok=True)
        selection = {'schema': SCHEMA, 'tool': product, 'base': base, 'pinned': current, 'artifacts': update}
        (directory / 'selection.json').write_text(json.dumps(selection, indent=2, sort_keys=True) + '\n')
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write(f"changed={'true' if update else 'false'}\nbase={base}\n")
    print(f"Selected for verification: {LABELS[product]} {current[product]} -> {update[product]['version']}" if update
          else f'No supported {LABELS[product]} update: {current[product]} is current')


def load(path):
    value = json.loads(Path(path).read_text())
    product, artifacts, current = value.get('tool'), value.get('artifacts'), value.get('pinned')
    if value.get('schema') != SCHEMA or product not in UPSTREAM or not re.fullmatch(r'[0-9a-f]{40}', value.get('base') or '') \
            or not isinstance(current, dict) or set(current) != set(UPSTREAM) or not isinstance(artifacts, dict):
        raise ValueError('Not a tool selection')
    version = (artifacts.get(product) or {}).get('version') or ''
    if not all(re.fullmatch(r'\d+\.\d+\.\d+', text or '') for text in (version, *current.values())) \
            or version_key(version) <= version_key(current[product]):
        raise ValueError('Selection has an unsupported version')
    _, _, assets = UPSTREAM[product]
    if set(artifacts) != set(assets(version)):
        raise ValueError(f'Selection must change exactly the {LABELS[product]} artifacts')
    for tool, name in assets(version).items():
        artifact = artifacts[tool]
        if artifact != {'version': version, 'url': download(product, version, name), 'sha256': artifact.get('sha256')} \
                or not re.fullmatch(r'[0-9a-f]{64}', artifact['sha256'] or ''):
            raise ValueError(f'Selection has an unsupported {tool} artifact')
    return value


def pin(value):
    """Pins the selection in this checkout, which must be exactly the commit it was made from."""
    if checked_out() != value['base']:
        raise ValueError('This checkout is not the commit the selection was made from; nothing was pinned')
    current, before = pinned()
    if current != value['pinned']:
        raise ValueError('This checkout is not the catalog the selection was made from')
    product = value['tool']
    text = CATALOG.read_text()
    for tool, artifact in value['artifacts'].items():
        line = f"    '{tool}': ('{artifact['version']}', '{artifact['url']}', '{artifact['sha256']}'),"
        text, count = re.subn(rf"(?m)^    '{re.escape(tool)}': \('[^'\n]*', '[^'\n]*', '[^'\n]*'\),$",
                              lambda _: line, text)
        if count != 1:
            raise ValueError('The reviewed catalog layout changed; update this script with it')
    CATALOG.write_text(text)
    manifest = json.loads(MANIFEST.read_text())
    manifest[SECTIONS[product]]['version'] = value['artifacts'][product]['version']
    MANIFEST.write_text(json.dumps(manifest, indent=2) + '\n')
    # Only this tool's artifacts change; every other pin stays exactly as reviewed.
    if pinned()[1] != before | value['artifacts']:
        raise ValueError('The catalog does not read back as the selection')
    return LABELS[product], value['pinned'][product], value['artifacts'][product]['version']


def apply(path):
    label, _, version = pin(load(path))
    print(f'Pinned {label} {version}.')


def commit(path):
    value = load(path)
    label, previous, version = pin(value)
    run = f"{os.environ['GITHUB_SERVER_URL']}/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
    body = '\n'.join([f'Latest stable upstream release, verified with the selected version by {run}.', '',
                      f'{label} {previous} -> {version}'])
    change = {'branch': {'repositoryNameWithOwner': os.environ['GITHUB_REPOSITORY'], 'branchName': 'main'},
              'expectedHeadOid': value['base'],
              'message': {'headline': f'chore: refresh baseline {label} to {version}', 'body': body},
              'fileChanges': {'additions': [{'path': str(file), 'contents': base64.b64encode(file.read_bytes()).decode()}
                                            for file in (CATALOG, MANIFEST)]}}
    result = subprocess.run(['gh', 'api', 'graphql', '--input', '-'], capture_output=True, text=True,
                            input=json.dumps({'query': COMMIT, 'variables': {'input': change}}))
    created = json.loads(result.stdout or '{}') if result.returncode == 0 else {}
    oid = (((created.get('data') or {}).get('createCommitOnBranch') or {}).get('commit') or {}).get('oid')
    if not re.fullmatch(r'[0-9a-f]{40}', oid or ''):
        raise ValueError('main changed after verification or refused the pin; nothing was published. '
                         'The next refresh selects and verifies again.')
    print(f'Pinned {label} {version} on main as {oid}.')


if __name__ == '__main__':
    try:
        if sys.argv[1:2] == ['select'] and len(sys.argv) == 4:
            select(sys.argv[2], Path(sys.argv[3]))
        elif sys.argv[1:2] in (['apply'], ['commit']) and len(sys.argv) == 3:
            {'apply': apply, 'commit': commit}[sys.argv[1]](sys.argv[2])
        else:
            raise ValueError('Expected select TOOL DIRECTORY, apply SELECTION or commit SELECTION')
    except (ValueError, OSError, KeyError, TypeError, AttributeError, json.JSONDecodeError) as error:
        sys.exit(f'Tool refresh refused: {error}')
