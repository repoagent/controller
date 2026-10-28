#!/usr/bin/env python3
"""Generate exact controller notes and select/finalize an approved immutable semantic release."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tempfile

SEMVER = r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)'
MARKER = '<!-- repoagent.controller-release/v2 -->'


def digest(content):
    return hashlib.sha256(content).hexdigest()


def release_files(source, supplied_notes=None, version=None):
    recorded = (source / 'releases/controller/version.txt').read_text().strip()
    if not re.fullmatch(SEMVER, recorded):
        raise ValueError('Controller version file must contain a stable semantic version')
    version = version or recorded
    if version != recorded and version != '0.0.0-ci':
        raise ValueError('Requested version differs from the source approved version')
    changelog = (source / 'releases/controller/CHANGELOG.md').read_text()
    lines = changelog.splitlines()
    start = next((i for i, line in enumerate(lines) if re.match(r'^##\s+\[?' + re.escape(version) + r'(?:\]|\s|$)', line)), None)
    end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith('## ')), len(lines)) if start is not None else None
    approved = start is not None
    if supplied_notes is not None:
        notes = supplied_notes.read_text().replace('\r\n', '\n').strip() + '\n'
        if not approved or '\n'.join(lines[start:end]).strip() != notes.strip():
            raise ValueError('GitHub notes differ from the version-specific approved changelog')
    elif approved:
        notes = '\n'.join(lines[start:end]).strip() + '\n'
    else:
        notes = f'# Controller {version}\n\nDevelopment snapshot; release notes have not been approved. A source version is not proof of publication.\n'
    return {'version': version, 'notesSha256': digest(notes.encode()), 'approved': approved}, notes


def notes_command(args):
    value, notes = release_files(args.source, version=args.version)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(notes.encode())
    print(json.dumps(value, sort_keys=True))



def run(argv, *, capture=True):
    result = subprocess.run(argv, capture_output=capture, text=True, timeout=180)
    if result.returncode:
        raise ValueError('Required release operation failed; existing artifacts are preserved')
    return result.stdout


def repository():
    value = os.environ.get('GH_REPO') or os.environ.get('GITHUB_REPOSITORY', '')
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', value):
        raise ValueError('Explicit release repository is required')
    return value


def released(tag):
    return json.loads(run(['gh', 'api', f'repos/{repository()}/releases/tags/{tag}']))


def body_notes(body):
    return body.split(MARKER, 1)[0].replace('\r\n', '\n').strip() + '\n'


def image_free(version):
    if not re.fullmatch(SEMVER, version):
        raise ValueError('Stable controller version is required')
    with tempfile.TemporaryDirectory() as temporary:
        auth = Path(temporary) / 'auth.json'
        auth.write_text('{"auths":{}}')
        result = subprocess.run(['skopeo', 'inspect', '--authfile', str(auth), '--no-creds', '--raw',
            f'docker://ghcr.io/repoagent/controller:controller-v{version}'], capture_output=True, text=True, timeout=60)
    if result.returncode == 0:
        raise ValueError('Version image already exists; preserve it and inspect partial publication before recovery')
    if not any(code in result.stderr.lower() for code in ('manifest unknown', 'name unknown')):
        raise ValueError('Registry absence is unconfirmed; no version image may be written')


def select_release(tag, output):
    match = re.fullmatch('controller-v(' + SEMVER + ')', tag)
    if not match:
        raise ValueError('Expected a stable controller-vMAJOR.MINOR.PATCH tag')
    release = released(tag)
    if release.get('draft') is not False or release.get('prerelease') is not False or release.get('tag_name') != tag:
        raise ValueError('An approved stable GitHub release is required')
    commit = run(['git', 'rev-parse', '--verify', f'refs/tags/{tag}^{{commit}}']).strip()
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Release source must be a full commit')
    run(['git', 'merge-base', '--is-ancestor', commit, 'origin/main'])
    if release.get('target_commitish') != commit:
        raise ValueError('Release target differs from immutable tagged source')
    output.mkdir(parents=True, exist_ok=True)
    notes_path = output / 'RELEASE_NOTES.md'
    notes_path.write_bytes(body_notes(release.get('body') or '').encode())
    with tempfile.TemporaryDirectory() as temporary:
        source = Path(temporary)
        directory = source / 'releases/controller'
        directory.mkdir(parents=True)
        for name in ('version.txt', 'CHANGELOG.md'):
            content = run(['git', 'show', f'{commit}:releases/controller/{name}'])
            (directory / name).write_text(content)
        value, _ = release_files(source, notes_path)
    if value['version'] != match[1] or not value['approved']:
        raise ValueError('Tag, source version and reviewed notes must agree')
    plan = dict(value, tag=tag, source=commit, complete=False)
    if MARKER in (release.get('body') or ''):
        block = re.search(re.escape(MARKER) + r'\s*```json\s*(.*?)\s*```', release['body'], re.S)
        metadata = json.loads(block[1]) if block else None
        if not metadata or metadata.get('schema') != 'repoagent.controller-release/v2' or any(metadata.get(key) != expected for key, expected in
            [('release', tag), ('version', value['version']), ('source', commit), ('notesSha256', value['notesSha256'])]):
            raise ValueError('Existing release metadata conflicts with the approved source')
        verify_remote_assets(tag, {**metadata['assets'], 'controller-release.json': digest((json.dumps(metadata, sort_keys=True, indent=2) + '\n').encode())}, output / 'existing')
        image = metadata.get('image', '')
        if not re.fullmatch(r'ghcr\.io/repoagent/controller@sha256:[0-9a-f]{64}', image):
            raise ValueError('Existing immutable image identity is invalid')
        with tempfile.TemporaryDirectory() as temporary:
            auth = Path(temporary) / 'auth.json'
            auth.write_text('{"auths":{}}')
            actual = run(['skopeo', 'inspect', '--authfile', str(auth), '--no-creds', '--format', '{{.Digest}}', 'docker://' + image]).strip()
        if image.split('@', 1)[1] != actual:
            raise ValueError('Existing image is not anonymously verified')
        plan['complete'] = True
    (output / 'plan.json').write_text(json.dumps(plan, sort_keys=True, indent=2) + '\n')
    print(json.dumps(plan, sort_keys=True))


def verify_remote_assets(tag, assets, output):
    output.mkdir(parents=True, exist_ok=True)
    for name, expected in assets.items():
        if not re.fullmatch(r'[A-Za-z0-9_.-]{1,200}', name) or not re.fullmatch(r'[0-9a-f]{64}', expected):
            raise ValueError('Invalid released asset identity')
        run(['gh', 'release', 'download', tag, '--repo', repository(), '--pattern', name, '--dir', str(output)])
        if digest((output / name).read_bytes()) != expected:
            raise ValueError('A released asset failed its recorded hash; it is preserved')


def verify_native(archive, version, commit, notes):
    prefix = f'repoagent-controller-{version}-linux-amd64/'
    with tarfile.open(archive, 'r:gz') as package:
        members = package.getmembers()
        if len({item.name for item in members}) != len(members) or any((not item.name.startswith(prefix) and not (item.isdir() and item.name.rstrip('/') == prefix.rstrip('/'))) or '..' in Path(item.name).parts or item.issym() or item.islnk() for item in members):
            raise ValueError('Unsafe or duplicate native archive entries')
        checksums = package.extractfile(prefix + 'SHA256SUMS').read().decode().splitlines()
        checked = {}
        for line in checksums:
            match = re.fullmatch(r'([0-9a-f]{64})  ([A-Za-z0-9_./-]+)', line)
            if not match or '..' in Path(match[2]).parts or match[2].startswith('/'):
                raise ValueError('Invalid native artifact checksum entry')
            item = package.getmember(prefix + match[2])
            if not item.isfile() or digest(package.extractfile(item).read()) != match[1]:
                raise ValueError('Native artifact checksum mismatch')
            checked[match[2]] = match[1]
        if not {'RepoAgentController', 'VERSION', 'RELEASE_NOTES.md', 'github-discussion/SKILL.md', 'repoagent-orientation/SKILL.md'}.issubset(checked):
            raise ValueError('Native artifact checksum manifest omits required files')
        version_file = package.extractfile(prefix + 'VERSION').read().decode()
        if f'version={version}\n' not in version_file or f'source_commit={commit}\n' not in version_file:
            raise ValueError('Native archive version/source differs from the release')
        if package.extractfile(prefix + 'RELEASE_NOTES.md').read() != notes:
            raise ValueError('Native archive release notes differ from the image')


def verified_artifact(artifact, plan_path):
    plan = json.loads(plan_path.read_text())
    version, tag, commit = plan['version'], plan['tag'], plan['source']
    if tag != 'controller-v' + version or not re.fullmatch(SEMVER, version) or not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Invalid approved release plan')
    notes = (artifact / 'RELEASE_NOTES.md').read_bytes()
    if digest(notes) != plan['notesSha256']:
        raise ValueError('Artifact notes differ from the approved release plan')
    inventory = json.loads((artifact / 'image-inventory.json').read_text())
    if inventory['controller']['version'] != version or inventory['controller']['releaseNotesSha256'] != digest(notes):
        raise ValueError('Image inventory differs from the approved release')
    versions = run([sys.executable, str(Path(__file__).with_name('image-release.py')), 'check', str(artifact)])
    versions = json.loads(versions)
    if versions['controller'] != commit or versions['version'] != version:
        raise ValueError('Compiled source/version differs from the approved release')
    archive = artifact / f'repoagent-controller-{version}-linux-amd64.tar.gz'
    verify_native(archive, version, commit, notes)
    checksum = artifact / (archive.name + '.sha256')
    if checksum.read_text().split()[0] != digest(archive.read_bytes()):
        raise ValueError('Native checksum differs from the verified archive')
    config_digest = json.loads((artifact / 'build.json').read_text()).get('containerimage.config.digest', '')
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', config_digest):
        raise ValueError('Verified image config digest is required')
    files = [archive, checksum, artifact / 'RELEASE_NOTES.md']
    assets = {file.name: digest(file.read_bytes()) for file in files}
    return plan, notes, versions, config_digest, files, assets


def alias_image(reference, auth):
    result = subprocess.run(['skopeo', 'inspect', '--authfile', str(auth), '--no-creds', '--format', '{{.Digest}}',
                             'docker://' + reference], capture_output=True, text=True, timeout=60)
    if result.returncode:
        if any(code in result.stderr.lower() for code in ('manifest unknown', 'name unknown')):
            return None
        raise ValueError('Registry alias observation is unconfirmed; no alias may be written')
    value = result.stdout.strip()
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', value):
        raise ValueError('Registry did not return one immutable image digest')
    return 'ghcr.io/repoagent/controller@' + value


def verify_public_image(image, config_digest, auth, directory):
    run(['skopeo', 'copy', '--src-authfile', str(auth), '--src-no-creds', '--preserve-digests',
         'docker://' + image, 'dir:' + str(directory)])
    manifest = json.loads((directory / 'manifest.json').read_text())
    if manifest.get('config', {}).get('digest') != config_digest:
        raise ValueError('Public immutable image differs from the verified artifact config')


def publish_image(artifact, plan_path, recover=False):
    if os.environ.get('GITHUB_REF') != 'refs/heads/main' or os.environ.get('GITHUB_REPOSITORY') != 'repoagent/controller':
        raise ValueError('Controller image publication requires the repository main workflow')
    plan, notes, versions, config_digest, files, assets = verified_artifact(artifact, plan_path)
    if os.environ.get('SOURCE_COMMIT') != plan['source']:
        raise ValueError('Publication source differs from the approved release plan')
    reference = 'ghcr.io/repoagent/controller:' + plan['tag']
    identity = dict(version=plan['version'], source=plan['source'], notesSha256=digest(notes), assets=assets,
                    imageConfigDigest=config_digest, inventorySha256=digest((artifact / 'image-inventory.json').read_bytes()))
    receipt = artifact / 'publication.json'
    if recover and not receipt.exists():
        raise ValueError('Recovery requires retained publication evidence; no image may be pushed')
    with tempfile.TemporaryDirectory() as temporary:
        directory = Path(temporary)
        auth = directory / 'auth.json'; auth.write_text('{"auths":{}}')
        existing = alias_image(reference, auth)
        if existing is not None:
            retained = json.loads(receipt.read_text()) if receipt.exists() else None
            if retained != dict(identity, image=existing):
                raise ValueError('Existing version alias lacks matching retained verified publication evidence; it is preserved')
            image = existing
            verify_public_image(image, config_digest, auth, directory / 'image')
        else:
            if receipt.exists():
                raise ValueError('Retained publication alias is absent; inspect before recovery')
            run_id, attempt = os.environ.get('GITHUB_RUN_ID', ''), os.environ.get('GITHUB_RUN_ATTEMPT', '')
            if not re.fullmatch(r'[0-9]+', run_id) or not re.fullmatch(r'[0-9]+', attempt):
                raise ValueError('Hosted run identity is required')
            candidate = f"ghcr.io/repoagent/controller:sha-{plan['source']}-{run_id}-{attempt}"
            local = 'repoagent-controller:semantic-release'
            run(['docker', 'tag', local, candidate]); run(['docker', 'push', candidate])
            image = alias_image(candidate, auth)
            if image is None:
                raise ValueError('Published candidate digest is unconfirmed')
            verify_public_image(image, config_digest, auth, directory / 'image')
            # Retain the identity before the write whose acknowledgement/observation can be lost.
            receipt.write_text(json.dumps(dict(identity, image=image), sort_keys=True, indent=2) + '\n')
            if alias_image(reference, auth) is not None:
                raise ValueError('Version alias appeared concurrently; inspect before recovery')
            run(['docker', 'tag', local, reference]); run(['docker', 'push', reference])
            if alias_image(reference, auth) != image:
                raise ValueError('Version alias publication is unconfirmed; retain the publication artifact for recovery')
    outputs = dict(image=image, reference=reference)
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            for name, value in outputs.items():
                output.write(name + '=' + value + '\n')
    print(json.dumps(outputs, sort_keys=True))


def recovery_artifact(run_id, artifact_id):
    if not re.fullmatch(r'[0-9]+', run_id) or not re.fullmatch(r'[0-9]+', artifact_id):
        raise ValueError('Recovery requires both original numeric run and artifact IDs')
    prefix = f'repos/{repository()}/actions/'
    workflow = json.loads(run(['gh', 'api', prefix + 'runs/' + run_id]))
    artifact = json.loads(run(['gh', 'api', prefix + 'artifacts/' + artifact_id]))
    if (workflow.get('path') != '.github/workflows/controller-release.yml'
            or workflow.get('event') != 'workflow_dispatch' or workflow.get('head_branch') != 'main'
            or artifact.get('expired') is not False or artifact.get('workflow_run', {}).get('id') != int(run_id)
            or not re.fullmatch('controller-publication-' + run_id + r'-[0-9]+', artifact.get('name', ''))):
        raise ValueError('Recovery artifact must be retained from the repository main controller-release workflow')


def finalize(artifact, plan_path):
    plan, notes, versions, config_digest, files, assets = verified_artifact(artifact, plan_path)
    version, tag, commit = plan['version'], plan['tag'], plan['source']
    image = os.environ.get('IMAGE', '')
    reference = os.environ.get('REFERENCE', '')
    if not re.fullmatch(r'ghcr\.io/repoagent/controller@sha256:[0-9a-f]{64}', image) or reference != 'ghcr.io/repoagent/controller:' + tag:
        raise ValueError('Verified immutable image and component version alias are required')
    metadata = {'schema': 'repoagent.controller-release/v2', 'release': tag, 'version': version, 'source': commit,
        'platform': 'linux/amd64', 'image': image, 'imageTag': reference, 'imageConfigDigest': config_digest,
        'inventorySha256': digest((artifact / 'image-inventory.json').read_bytes()), 'notesSha256': digest(notes),
        'versions': versions, 'assets': assets}
    metadata_path = artifact / 'controller-release.json'
    metadata_path.write_text(json.dumps(metadata, indent=2, sort_keys=True) + '\n')
    release = released(tag)
    if body_notes(release.get('body') or '').encode() != notes:
        raise ValueError('Release prose changed during verification; existing artifacts are preserved')
    body = notes.decode().rstrip() + '\n\n' + MARKER + '\n```json\n' + json.dumps(metadata, sort_keys=True, indent=2) + '\n```\n'
    if MARKER in (release.get('body') or '') and release['body'].strip() != body.strip():
        raise ValueError('Existing release metadata differs; it is preserved')
    existing = {item['name'] for item in release.get('assets', [])}
    with tempfile.TemporaryDirectory() as temporary:
        for file in [*files, metadata_path]:
            if file.name in existing:
                verify_remote_assets(tag, {file.name: digest(file.read_bytes())}, Path(temporary) / file.name)
            else:
                run(['gh', 'release', 'upload', tag, str(file), '--repo', repository()])
        verify_remote_assets(tag, {file.name: digest(file.read_bytes()) for file in [*files, metadata_path]}, Path(temporary) / 'verified')
    if released(tag).get('body') != release.get('body'):
        raise ValueError('Release prose changed during upload; existing artifacts are preserved')
    notes_path = artifact / 'published-body.md'
    notes_path.write_text(body)
    run(['gh', 'release', 'edit', tag, '--repo', repository(), '--notes-file', str(notes_path)])
    print(json.dumps({'release': tag, 'image': image, 'verified': True}, sort_keys=True))

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    notes = commands.add_parser('notes')
    notes.add_argument('source', type=Path)
    notes.add_argument('output', type=Path)
    notes.add_argument('--version')
    select = commands.add_parser('select')
    select.add_argument('tag')
    select.add_argument('output', type=Path)
    free = commands.add_parser('image-free')
    free.add_argument('version')
    finish = commands.add_parser('finalize')
    finish.add_argument('artifact', type=Path)
    finish.add_argument('plan', type=Path)
    publish = commands.add_parser('publish-image')
    publish.add_argument('artifact', type=Path)
    publish.add_argument('plan', type=Path)
    publish.add_argument('--recover', action='store_true')
    recovery = commands.add_parser('recovery-artifact')
    recovery.add_argument('run')
    recovery.add_argument('artifact')
    args = parser.parse_args()
    if args.command == 'notes':
        notes_command(args)
    elif args.command == 'select':
        select_release(args.tag, args.output)
    elif args.command == 'image-free':
        image_free(args.version)
    elif args.command == 'finalize':
        finalize(args.artifact, args.plan)
    elif args.command == 'publish-image':
        publish_image(args.artifact, args.plan, args.recover)
    elif args.command == 'recovery-artifact':
        recovery_artifact(args.run, args.artifact)

if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, KeyError, TypeError, json.JSONDecodeError, tarfile.TarError, subprocess.SubprocessError) as error:
        sys.exit(f'Controller release refused: {error}')
