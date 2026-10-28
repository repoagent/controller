"""Public broker-release CLI with recording GitHub/registry command transports.

The real release selection and publication decisions run here. These fixtures do not
prove GitHub Actions, GHCR or Azure behavior.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from scripts.delivery.test_auth_pipeline import SOURCE_FILES, SDK


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/delivery/broker-release.py'
SHA = 'b' * 40
VERSION = '1.0.0'
TAG = 'broker-v' + VERSION
REPOSITORY = 'ghcr.io/repoagent/controller-auth'
IMAGE = REPOSITORY + '@sha256:' + 'a' * 64
ALIAS = REPOSITORY + ':' + TAG
API = ['gh', 'api', '--hostname', 'github.com', '--include']
RELEASE_API = API + ['repos/repoagent/controller/releases/tags/' + TAG]
TAG_API = API + ['repos/repoagent/controller/git/ref/tags/' + TAG]
ASSET_API = API + ['-H', 'Accept:application/octet-stream', 'repos/repoagent/controller/releases/assets/42']
ATTEST = ['skopeo', 'inspect', '--no-creds', '--config', '--override-os', 'linux',
          '--override-arch', 'amd64', 'docker://' + IMAGE]
ALIAS_INSPECT = ['skopeo', 'inspect', '--no-creds', '--override-os', 'linux',
                 '--override-arch', 'amd64', 'docker://' + ALIAS]
MANIFEST = dict(schema='repoagent.broker-release/v1', component='broker', version=VERSION,
                tag=TAG, source=SHA, image=IMAGE, imageTag=ALIAS,
                run='https://github.com/repoagent/controller/actions/runs/123')
LABELS = {'io.repoagent.role': 'auth-service', 'org.opencontainers.image.revision': SHA,
          'org.opencontainers.image.version': VERSION}
REMOTE = dict(os='linux', architecture='amd64', config=dict(Labels=LABELS, User='1654:1654'))
DOUBLE = '''import json, os, pathlib, sys
argv = [pathlib.Path(sys.argv[0]).name, *sys.argv[1:]]
argv = [arg.replace(os.environ['TEST_ROOT'], '<root>') for arg in argv]
ledger = pathlib.Path(os.environ['TEST_LEDGER'])
rows = ledger.read_text().splitlines() if ledger.exists() else []
with ledger.open('a') as out: out.write(json.dumps(argv) + '\\n')
steps = json.loads(pathlib.Path(os.environ['TEST_TRANSCRIPT']).read_text())
if len(rows) >= len(steps) or steps[len(rows)]['argv'] != argv:
    sys.exit('Unexpected provider command: ' + json.dumps(argv))
step = steps[len(rows)]
if 'raw' in step: print(step['raw'])
elif 'response' in step: print(json.dumps(step['response']))
if 'stderr' in step: print(step['stderr'], file=sys.stderr)
sys.exit(step.get('exit', 0))
'''


def http(argv, response, status=200):
    return dict(argv=argv, raw=f'HTTP/2.0 {status} Status\r\nContent-Type: application/json\r\n\r\n' + json.dumps(response),
                exit=0 if status == 200 else 1)


def release(assets=True):
    return http(RELEASE_API, dict(id=7, tag_name=TAG, draft=False, prerelease=False,
                                 assets=[dict(id=42, name='broker-release.json')] if assets else []))


def reference():
    return http(TAG_API, dict(object=dict(type='commit', sha=SHA)))


class BrokerReleaseCliTests(unittest.TestCase):
    def invoke(self, verb, transcript, *, manifest=MANIFEST, environment=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            scripts = root / 'scripts/delivery'
            scripts.mkdir(parents=True)
            if SCRIPT.exists():
                (scripts / SCRIPT.name).write_text(SCRIPT.read_text())
            (scripts / 'auth-pipeline.py').write_text((ROOT / 'scripts/delivery/auth-pipeline.py').read_text())
            (root / 'service/auth').mkdir(parents=True)
            (root / 'service/auth/version.txt').write_text(VERSION + '\n')
            for name in SOURCE_FILES:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('# Source contract fixture only\n')
            policy = json.dumps(dict(sdk=dict(version=SDK, rollForward='disable', allowPrerelease=True)))
            (root / 'global.json').write_text(policy)
            (root / 'service/auth/global.json').write_text(policy)
            binaries = root / 'bin'
            binaries.mkdir()
            for name in ('gh', 'git', 'skopeo', 'docker'):
                path = binaries / name
                path.write_text('#!' + sys.executable + '\n' + DOUBLE)
                path.chmod(0o700)
            ledger, fixture, output = root / 'ledger', root / 'transcript', root / 'output'
            fixture.write_text(json.dumps(transcript))
            output.write_text('')
            (root / 'broker-release.json').write_text(json.dumps(manifest))
            env = dict(os.environ, PATH=str(binaries) + os.pathsep + os.defpath,
                       TEST_LEDGER=str(ledger), TEST_TRANSCRIPT=str(fixture), GITHUB_OUTPUT=str(output),
                       TEST_ROOT=str(root),
                       AUTH_RELEASE_TAG=TAG, AUTH_IMAGE_REPOSITORY=REPOSITORY, GITHUB_REF='refs/heads/main',
                       GITHUB_REPOSITORY='repoagent/controller', GITHUB_SHA=SHA, GITHUB_RUN_ID='123',
                       AUTH_RELEASE_MANIFEST=str(root / 'broker-release.json'))
            env.update(environment or {})
            result = subprocess.run([sys.executable, str(scripts / SCRIPT.name), verb], cwd=root,
                                    env=env, capture_output=True, text=True, timeout=10)
            calls = [json.loads(row) for row in ledger.read_text().splitlines()] if ledger.exists() else []
            return result, calls, output.read_text()

    def test_semantic_rollback_resolves_the_verified_source_and_immutable_digest(self):
        transcript = [release(), reference(), http(ASSET_API, MANIFEST), dict(argv=ATTEST, response=REMOTE)]
        result, calls, output = self.invoke('resolve', transcript)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [step['argv'] for step in transcript])
        self.assertEqual(output, f'image={IMAGE}\nsource={SHA}\nversion={VERSION}\ntag={TAG}\n')

    def test_a_source_change_waits_for_a_new_approved_release_instead_of_republishing_the_old_version(self):
        transcript = [release(), reference(), dict(argv=['git', 'rev-parse', 'HEAD'], raw='c' * 40)]
        result, calls, output = self.invoke('plan', transcript, environment={'AUTH_RELEASE_TAG': ''})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [step['argv'] for step in transcript])
        self.assertEqual(output, 'release=false\n')

    def test_published_version_retry_reuses_its_digest_without_rebuilding_or_overwriting(self):
        transcript = [release(), reference(), dict(argv=['git', 'rev-parse', 'HEAD'], raw=SHA),
                      dict(argv=['git', 'merge-base', '--is-ancestor', SHA, 'refs/remotes/origin/main']),
                      http(ASSET_API, MANIFEST), dict(argv=ATTEST, response=REMOTE),
                      dict(argv=ALIAS_INSPECT, response=dict(Digest=IMAGE.split('@')[1]))]
        result, calls, output = self.invoke('publish', transcript)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [step['argv'] for step in transcript])
        self.assertEqual(json.loads(result.stdout), MANIFEST)
        self.assertEqual(output, f'image={IMAGE}\nsource={SHA}\nversion={VERSION}\ntag={TAG}\n')

    def test_partial_publication_recovers_the_existing_verified_version_tag_without_rebuilding(self):
        transcript = [release(assets=False), reference(), dict(argv=['git', 'rev-parse', 'HEAD'], raw=SHA),
                      dict(argv=['git', 'merge-base', '--is-ancestor', SHA, 'refs/remotes/origin/main']),
                      dict(argv=ALIAS_INSPECT, response=dict(Digest=IMAGE.split('@')[1])),
                      dict(argv=ATTEST, response=REMOTE)]
        result, calls, output = self.invoke('publish', transcript)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [step['argv'] for step in transcript])
        self.assertEqual(json.loads(result.stdout), MANIFEST)
        self.assertIn('image=' + IMAGE, output)

    def test_first_publication_verifies_the_digest_before_creating_the_version_alias(self):
        unique = REPOSITORY + ':sha-' + SHA
        unique_inspect = ALIAS_INSPECT[:-1] + ['docker://' + unique]
        missing = lambda argv: dict(argv=argv, stderr='manifest unknown', exit=1)
        build = ['docker', 'build', '--platform', 'linux/amd64', '--build-arg', 'BROKER_VERSION=' + VERSION,
                 '--build-arg', 'SOURCE_REVISION=' + SHA, '--label', 'org.opencontainers.image.version=' + VERSION,
                 '--label', 'org.opencontainers.image.revision=' + SHA, '--tag', unique,
                 '--file', 'service/auth/Dockerfile', '.']
        local = dict(Config=dict(Labels=LABELS, User='1654:1654'), Os='linux', Architecture='amd64')
        transcript = [release(assets=False), reference(), dict(argv=['git', 'rev-parse', 'HEAD'], raw=SHA),
                      dict(argv=['git', 'merge-base', '--is-ancestor', SHA, 'refs/remotes/origin/main']),
                      missing(ALIAS_INSPECT), missing(unique_inspect),
                      dict(argv=['git', 'status', '--porcelain', '--untracked-files=all']),
                      dict(argv=['git', 'ls-files', '--others', '--ignored', '--exclude-standard', '--', 'service/auth/src']),
                      dict(argv=build),
                      dict(argv=['docker', 'image', 'inspect', unique], response=[local]),
                      dict(argv=['docker', 'run', '--rm', '--network', 'none', unique, '--version', '--json'],
                           response=dict(component='broker', version=VERSION, source=SHA)),
                      dict(argv=['docker', 'push', unique]),
                      dict(argv=['docker', 'image', 'inspect', unique], response=[dict(local, RepoDigests=[IMAGE])]),
                      dict(argv=ATTEST, response=REMOTE), missing(ALIAS_INSPECT),
                      dict(argv=['docker', 'pull', '--platform', 'linux/amd64', IMAGE]),
                      dict(argv=['docker', 'tag', IMAGE, ALIAS]), dict(argv=['docker', 'push', ALIAS]),
                      dict(argv=ALIAS_INSPECT, response=dict(Digest=IMAGE.split('@')[1]))]
        result, calls, output = self.invoke('publish', transcript)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [step['argv'] for step in transcript])
        self.assertEqual(json.loads(result.stdout), MANIFEST)
        self.assertIn('image=' + IMAGE, output)

    def test_release_metadata_is_confirmed_after_upload_without_clobbering_approved_notes(self):
        transcript = [release(assets=False), reference(), dict(argv=ATTEST, response=REMOTE),
                      dict(argv=ALIAS_INSPECT, response=dict(Digest=IMAGE.split('@')[1])),
                      dict(argv=['gh', 'release', 'upload', TAG, '<root>/broker-release.json', '--repo', 'repoagent/controller']),
                      release(), http(ASSET_API, MANIFEST)]
        result, calls, output = self.invoke('record', transcript)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [step['argv'] for step in transcript])
        self.assertNotIn('--clobber', [part for call in calls for part in call])
        self.assertIn('image=' + IMAGE, output)

    def test_a_changed_version_tag_is_refused_without_registry_writes(self):
        transcript = [release(), reference(), dict(argv=['git', 'rev-parse', 'HEAD'], raw=SHA),
                      dict(argv=['git', 'merge-base', '--is-ancestor', SHA, 'refs/remotes/origin/main']),
                      http(ASSET_API, MANIFEST), dict(argv=ATTEST, response=REMOTE),
                      dict(argv=ALIAS_INSPECT, response=dict(Digest='sha256:' + 'c' * 64))]
        result, calls, output = self.invoke('publish', transcript)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, [step['argv'] for step in transcript])
        self.assertEqual(output, '')

    def test_registry_uncertainty_is_not_treated_as_a_missing_version(self):
        canary = 'PRIVATE-PROVIDER-DIAGNOSTIC'
        transcript = [release(assets=False), reference(), dict(argv=['git', 'rev-parse', 'HEAD'], raw=SHA),
                      dict(argv=['git', 'merge-base', '--is-ancestor', SHA, 'refs/remotes/origin/main']),
                      dict(argv=ALIAS_INSPECT, stderr=canary, exit=1)]
        result, calls, output = self.invoke('publish', transcript)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, [step['argv'] for step in transcript])
        self.assertEqual(output, '')
        self.assertNotIn(canary, result.stdout + result.stderr)

    def test_metadata_upload_lost_response_recovers_only_the_exact_recorded_identity(self):
        transcript = [release(assets=False), reference(), dict(argv=ATTEST, response=REMOTE),
                      dict(argv=ALIAS_INSPECT, response=dict(Digest=IMAGE.split('@')[1])),
                      dict(argv=['gh', 'release', 'upload', TAG, '<root>/broker-release.json', '--repo', 'repoagent/controller'], exit=1),
                      release(), http(ASSET_API, MANIFEST)]
        result, calls, output = self.invoke('record', transcript)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [step['argv'] for step in transcript])
        self.assertIn('image=' + IMAGE, output)

    def test_malformed_tag_observation_fails_without_traceback_or_provider_disclosure(self):
        transcript = [release(), http(TAG_API, dict(object=None, private='PRIVATE-TAG-DIAGNOSTIC'))]
        result, calls, output = self.invoke('resolve', transcript)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, [step['argv'] for step in transcript])
        self.assertEqual(output, '')
        self.assertNotIn('Traceback', result.stderr)
        self.assertNotIn('PRIVATE-TAG-DIAGNOSTIC', result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
