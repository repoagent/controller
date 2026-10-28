#!/usr/bin/env python3
"""Approved broker releases: immutable version/source/image mapping and deliberate deployment selection."""
import json
import os
from pathlib import Path
import re
import runpy
import subprocess
import sys


REPOSITORY = 'repoagent/controller'
IMAGE_REPOSITORY = 'ghcr.io/repoagent/controller-auth'
SCHEMA = 'repoagent.broker-release/v1'
VERSION = r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)'
pipeline = runpy.run_path(str(Path(__file__).with_name('auth-pipeline.py')))


def run(arguments, *, check=True):
    result = subprocess.run(arguments, capture_output=True, text=True, timeout=600)
    if check and result.returncode:
        raise ValueError(Path(arguments[0]).name + ' failed during broker release; provider output remains private')
    return result


def github(endpoint, *, headers=(), absent=False):
    result = run(['gh', 'api', '--hostname', 'github.com', '--include', *headers,
                  'repos/' + REPOSITORY + '/' + endpoint], check=False)
    match = re.match(r'HTTP/\S+ (\d{3})[^\r\n]*\r?\n.*?\r?\n\r?\n(.*)\Z', result.stdout, re.S)
    if not match or len(result.stdout) > 8 * 1024 * 1024:
        raise ValueError('GitHub response is unavailable or outside the release observation bound')
    status = int(match[1])
    if absent and status == 404:
        return None
    if result.returncode or status != 200:
        raise ValueError('GitHub did not confirm the broker release observation')
    value = json.loads(match[2])
    if not isinstance(value, dict):
        raise ValueError('GitHub release observation must be an object')
    return value


def selected_tag():
    tag = os.environ.get('AUTH_RELEASE_TAG', '')
    if not re.fullmatch('broker-v' + VERSION, tag):
        raise ValueError('AUTH_RELEASE_TAG must select a stable broker-vMAJOR.MINOR.PATCH release')
    return tag


def release(tag, *, absent=False):
    value = github('releases/tags/' + tag, absent=absent)
    if value is None:
        return None
    if value.get('tag_name') != tag or value.get('draft') is not False or value.get('prerelease') is not False:
        raise ValueError('Only the exact published stable broker release is accepted')
    return value


def source(tag):
    item = github('git/ref/tags/' + tag).get('object', {})
    for _ in range(4):
        if not isinstance(item, dict):
            break
        sha = item.get('sha', '')
        if not re.fullmatch(r'[0-9a-f]{40}', sha):
            break
        if item.get('type') == 'commit':
            return sha
        if item.get('type') != 'tag':
            break
        item = github('git/tags/' + sha).get('object', {})
    raise ValueError('Broker release tag does not identify one bounded Git commit')


def manifest(released, tag, sha, *, absent=False):
    rows = released.get('assets')
    if not isinstance(rows, list) or any(not isinstance(item, dict) for item in rows):
        raise ValueError('GitHub did not confirm the release asset inventory')
    assets = [item for item in rows if item.get('name') == 'broker-release.json']
    if not assets and absent:
        return None
    if len(assets) != 1 or type(assets[0].get('id')) is not int or assets[0]['id'] <= 0:
        raise ValueError('Broker release must have one immutable broker-release.json asset')
    value = github('releases/assets/' + str(assets[0]['id']), headers=['-H', 'Accept:application/octet-stream'])
    return validate_manifest(value, tag, sha)


def validate_manifest(value, tag, sha):
    if not isinstance(value, dict) or set(value) != {'schema', 'component', 'tag', 'version', 'source', 'image', 'imageTag', 'run'}:
        raise ValueError('Broker release metadata has an unsupported schema shape')
    if (value.get('schema') != SCHEMA or value.get('component') != 'broker' or value.get('tag') != tag
            or value.get('version') != tag.removeprefix('broker-v') or value.get('source') != sha
            or value.get('imageTag') != IMAGE_REPOSITORY + ':' + tag
            or not re.fullmatch(re.escape(IMAGE_REPOSITORY) + r'@sha256:[0-9a-f]{64}', value.get('image', ''))
            or not re.fullmatch(r'https://github\.com/repoagent/controller/actions/runs/[0-9]+', value.get('run', ''))):
        raise ValueError('Broker release metadata differs from its version/source/image binding')
    return value


def alias_digest(tag):
    result = run(['skopeo', 'inspect', '--no-creds', '--override-os', 'linux', '--override-arch', 'amd64',
                  'docker://' + IMAGE_REPOSITORY + ':' + tag], check=False)
    if result.returncode:
        if re.search(r'\b(?:manifest unknown|name unknown|MANIFEST_UNKNOWN|NAME_UNKNOWN)\b', result.stderr):
            return None
        raise ValueError('Registry did not confirm whether the broker image tag exists')
    digest = json.loads(result.stdout).get('Digest', '')
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
        raise ValueError('Broker image tag did not resolve to one immutable digest')
    return IMAGE_REPOSITORY + '@' + digest


def save(value):
    path = Path(os.environ['AUTH_RELEASE_MANIFEST'])
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')
    outputs(value)
    print(json.dumps(value, sort_keys=True))


def new_manifest(tag, sha, image):
    run_id = os.environ.get('GITHUB_RUN_ID', '')
    if not re.fullmatch(r'[0-9]+', run_id):
        raise ValueError('A hosted workflow run is required for publication provenance')
    return dict(schema=SCHEMA, component='broker', version=tag.removeprefix('broker-v'), tag=tag, source=sha,
                image=image, imageTag=IMAGE_REPOSITORY + ':' + tag,
                run='https://github.com/' + REPOSITORY + '/actions/runs/' + run_id)


def ensure_alias(tag, image):
    existing = alias_digest(tag)
    if existing is not None:
        if existing != image:
            raise ValueError('A broker version tag already identifies different content; it is never overwritten')
        return
    alias = IMAGE_REPOSITORY + ':' + tag
    run(['docker', 'pull', '--platform', 'linux/amd64', image])
    run(['docker', 'tag', image, alias])
    run(['docker', 'push', alias])
    if alias_digest(tag) != image:
        raise ValueError('Version alias publication is unconfirmed; inspect before retrying')


def attest(value):
    os.environ.update(AUTH_IMAGE_REPOSITORY=IMAGE_REPOSITORY, AUTH_IMAGE=value['image'],
                      AUTH_EXPECTED_SOURCE_SHA=value['source'], AUTH_EXPECTED_BROKER_VERSION=value['version'])
    pipeline['attest']()


def outputs(value):
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        for name in ('release', 'image', 'source', 'version', 'tag'):
            if name in value:
                output.write(name + '=' + value[name] + '\n')


def require_main():
    if os.environ.get('GITHUB_REF') != 'refs/heads/main' or os.environ.get('GITHUB_REPOSITORY') != REPOSITORY:
        raise ValueError('Broker release publication requires the repository main workflow')


def require_clean_source():
    if run(['git', 'status', '--porcelain', '--untracked-files=all']).stdout.strip():
        raise ValueError('Broker publication requires an unchanged verified checkout')
    ignored = run(['git', 'ls-files', '--others', '--ignored', '--exclude-standard', '--', 'service/auth/src']).stdout.splitlines()
    if any(not {'bin', 'obj'}.intersection(Path(path).parts) for path in ignored):
        raise ValueError('Ignored broker source inputs require inspection before publication')


def plan():
    require_main()
    explicit = bool(os.environ.get('AUTH_RELEASE_TAG'))
    tag = selected_tag() if explicit else 'broker-v' + (pipeline['broker_version']() or '')
    if not re.fullmatch('broker-v' + VERSION, tag):
        raise ValueError('Current broker source does not declare a stable release version')
    released = release(tag, absent=not explicit)
    if released is None:
        outputs(dict(release='false'))
        return
    sha = source(tag)
    if not explicit and run(['git', 'rev-parse', 'HEAD']).stdout.strip() != sha:
        outputs(dict(release='false'))
        return
    run(['git', 'merge-base', '--is-ancestor', sha, 'refs/remotes/origin/main'])
    outputs(dict(release='true', source=sha, version=tag.removeprefix('broker-v'), tag=tag))


def publish():
    require_main()
    tag = selected_tag()
    released = release(tag)
    sha = source(tag)
    if run(['git', 'rev-parse', 'HEAD']).stdout.strip() != sha or pipeline['broker_version']() != tag.removeprefix('broker-v'):
        raise ValueError('Checked-out broker source/version differs from the approved release tag')
    run(['git', 'merge-base', '--is-ancestor', sha, 'refs/remotes/origin/main'])
    value = manifest(released, tag, sha, absent=True)
    if value is None:
        image = alias_digest(tag)
        if image is None:
            image = alias_digest('sha-' + sha)
            if image is None:
                require_clean_source()
                pipeline['check_publication']()
                image = pipeline['build_publish'](sha, '.')
            value = new_manifest(tag, sha, image)
            attest(value)
            ensure_alias(tag, image)
            save(value)
            return
        value = new_manifest(tag, sha, image)
        attest(value)
        save(value)
        return
    attest(value)
    if alias_digest(tag) != value['image']:
        raise ValueError('Published broker tag differs from its immutable release metadata')
    save(value)


def record():
    require_main()
    tag = selected_tag()
    released = release(tag)
    sha = source(tag)
    path = Path(os.environ['AUTH_RELEASE_MANIFEST'])
    if path.stat().st_size > 65536:
        raise ValueError('Broker release metadata exceeds its publication bound')
    value = validate_manifest(json.loads(path.read_text()), tag, sha)
    existing = manifest(released, tag, sha, absent=True)
    identity = ('schema', 'component', 'version', 'tag', 'source', 'image', 'imageTag')
    if existing is not None and any(existing[key] != value[key] for key in identity):
        raise ValueError('A released broker version already records different content; it is never overwritten')
    attest(value)
    if alias_digest(tag) != value['image']:
        raise ValueError('Broker version alias differs from the release metadata candidate')
    if existing is None:
        # Never clobber: a failed or uncertain upload is recovered by reading the exact asset back.
        run(['gh', 'release', 'upload', tag, str(path), '--repo', REPOSITORY], check=False)
        existing = manifest(release(tag), tag, sha)
        if any(existing[key] != value[key] for key in identity):
            raise ValueError('Broker metadata upload is unconfirmed or records different content')
    outputs(existing)
    print(json.dumps(existing, sort_keys=True))


def resolve():
    tag = selected_tag()
    value = manifest(release(tag), tag, source(tag))
    attest(value)
    outputs(value)
    print(json.dumps(value, sort_keys=True))


if __name__ == '__main__':
    try:
        if len(sys.argv) != 2 or sys.argv[1] not in ('resolve', 'plan', 'publish', 'record'):
            raise ValueError('Expected resolve, plan, publish or record')
        globals()[sys.argv[1]]()
    except (ValueError, OSError, KeyError, TypeError, IndexError, subprocess.SubprocessError) as error:
        sys.exit('Broker release refused: ' + str(error))
