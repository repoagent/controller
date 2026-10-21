"""Bootstrap CLI with real Git archives and captured Docker context bytes.

The committed verify script is a minimal fixture, not actual service verification.
Only Docker/registry/Azure processes are replaced; Git, tar, Bash and files are real.
"""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from test_auth_pipeline import SCRIPT, SOURCE_FILES, REPOSITORY, IMAGE, CANARY


BOUNDARY = '''import json, os, pathlib, sys
name = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
record = dict(name=name, args=args)
if name == 'docker' and args[:1] == ['build']:
    context = pathlib.Path(args[-1])
    record['context'] = {str(p.relative_to(context)): p.read_text() for p in context.rglob('*') if p.is_file()}
with open(os.environ['LEDGER'], 'a') as stream:
    stream.write(json.dumps(record) + '\\n')
settings = json.loads(pathlib.Path(os.environ['SETTINGS']).read_text())
sha = os.environ['GITHUB_SHA']
config = dict(Labels={'io.repoagent.role': 'auth-service', 'org.opencontainers.image.revision': sha}, User='10001:10001')
if name == 'docker' and args[:2] == ['image', 'inspect']:
    print(json.dumps([dict(Config=settings.get('config', config), Os=settings.get('os', 'linux'),
        Architecture=settings.get('arch', 'amd64'), RepoDigests=settings.get('digests', [os.environ['AUTH_IMAGE']]))]))
elif name == 'skopeo':
    if settings.get('private'):
        sys.exit(os.environ['CANARY'])
    print(json.dumps(dict(config=config, os='linux', architecture='amd64')))
elif name != 'docker' or args[:1] not in (['build'], ['push']):
    sys.exit('Unexpected provider command')
'''


class BootstrapArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        script = self.root / 'scripts/delivery/auth-pipeline.py'
        script.parent.mkdir(parents=True)
        shutil.copyfile(SCRIPT, script)
        for name in SOURCE_FILES:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('committed bytes\n')
        (self.root / 'service/auth/global.json').write_text('{"sdk":{"version":"10.0.100"}}')
        (self.root / 'service/auth/verify.sh').write_text('test "$(cat service/auth/Dockerfile)" = "committed bytes"\n')
        self.git('init', '-q')
        self.git('add', '.')
        self.git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.test', 'commit', '-qm', 'accepted source')
        self.sha = self.git('rev-parse', 'HEAD').strip()
        self.binaries = self.root / 'bin'
        self.binaries.mkdir()
        for name in ('docker', 'skopeo', 'gh', 'az'):
            executable = self.binaries / name
            executable.write_text(f'#!{sys.executable}\n' + BOUNDARY)
            executable.chmod(0o700)
        for name in ('git', 'tar', 'bash', 'cat'):
            (self.binaries / name).symlink_to(shutil.which(name))
        self.ledger = self.root / 'calls.jsonl'
        self.settings = self.root / 'settings.json'
        self.env = dict(PATH=str(self.binaries), HOME=str(self.root), LANG='C.UTF-8',
                        LEDGER=str(self.ledger), SETTINGS=str(self.settings), CANARY=CANARY,
                        AUTH_IMAGE_REPOSITORY=REPOSITORY, AUTH_IMAGE=IMAGE, GITHUB_SHA=self.sha,
                        AUTH_PACKAGE_ACTIONS_ACCESS_CONFIRMED='true')

    def git(self, *args):
        return subprocess.run(['git', *args], cwd=self.root, check=True, capture_output=True, text=True).stdout

    def invoke(self, settings=None, env=None, verb='bootstrap-publish'):
        self.settings.write_text(json.dumps(settings or {}))
        self.ledger.unlink(missing_ok=True)
        result = subprocess.run([sys.executable, '-B', str(self.root / 'scripts/delivery/auth-pipeline.py'), verb],
                                cwd=self.root, env={**self.env, **(env or {})}, capture_output=True, text=True, timeout=10)
        self.calls = [json.loads(line) for line in self.ledger.read_text().splitlines()] if self.ledger.exists() else []
        self.assertNotIn(CANARY, result.stdout + result.stderr + json.dumps(self.calls))
        return result

    def test_bootstrap_builds_verified_accepted_archive_not_dirty_or_untracked_bytes(self):
        (self.root / 'service/auth/Dockerfile').write_text('dirty ambient bytes\n')
        (self.root / 'service/auth/untracked-private.txt').write_text(CANARY)
        (self.root / 'service/auth/verify.sh').write_text('exit 99\n')
        result = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        receipts = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(receipts, [dict(source=self.sha, image=IMAGE, publicationComplete=value) for value in (False, True)])
        build = next(call for call in self.calls if call['args'][:1] == ['build'])
        self.assertEqual(build['context']['Dockerfile'], 'committed bytes\n')
        self.assertNotIn('untracked-private.txt', build['context'])
        self.assertFalse(Path(build['args'][-1]).exists(), 'Archived context must be removed')
        self.assertEqual([call['name'] for call in self.calls], ['docker'] * 4 + ['skopeo'])
        self.assertIn('org.opencontainers.image.revision=' + self.sha, build['args'])

    def test_bad_or_absent_accepted_commit_never_builds(self):
        for sha in ('', self.sha[:12], 'f' * 40, 'main;echo bad'):
            with self.subTest(sha=sha):
                result = self.invoke(env={'GITHUB_SHA': sha})
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.calls, [])

    def test_bootstrap_refuses_a_repository_outside_the_dedicated_package(self):
        result = self.invoke(env={'AUTH_IMAGE_REPOSITORY': 'ghcr.io/repoagent/controller'})
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.calls, [])

    def test_private_push_preserves_incomplete_receipt_and_resumes_attestation_without_rebuild(self):
        result = self.invoke({'private': True})
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout), dict(source=self.sha, image=IMAGE, publicationComplete=False))
        self.assertIn('grant Actions access', result.stderr)
        self.assertIn('do not rebuild to resume', result.stderr)
        result = self.invoke(verb='attest', env={'AUTH_EXPECTED_SOURCE_SHA': self.sha})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([call['name'] for call in self.calls], ['skopeo'])

    def test_public_digest_without_confirmed_actions_access_is_still_incomplete(self):
        result = self.invoke(env={'AUTH_PACKAGE_ACTIONS_ACCESS_CONFIRMED': ''})
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(json.loads(result.stdout)['publicationComplete'])
        self.assertIn('Actions package access', result.stderr)

    def test_wrong_role_user_platform_or_revision_never_pushes(self):
        valid = dict(Labels={'io.repoagent.role': 'auth-service', 'org.opencontainers.image.revision': self.sha}, User='10001')
        for settings in ({'os': 'windows'}, {'arch': 'arm64'},
                         {'config': {**valid, 'User': '0'}},
                         {'config': {**valid, 'Labels': {'io.repoagent.role': 'controller'}}},
                         {'config': {**valid, 'Labels': {**valid['Labels'], 'org.opencontainers.image.revision': 'a' * 40}}}):
            with self.subTest(settings=settings):
                result = self.invoke(settings)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual([call['args'][0] for call in self.calls], ['build', 'image'])

    def test_ambiguous_or_wrong_actual_digest_never_emits_a_receipt(self):
        for digests in ([], [IMAGE, IMAGE], [REPOSITORY + ':latest'], ['ghcr.io/other/auth@sha256:' + 'a' * 64]):
            with self.subTest(digests=digests):
                result = self.invoke({'digests': digests})
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, '')
                self.assertEqual([call['name'] for call in self.calls], ['docker'] * 4)


if __name__ == '__main__':
    unittest.main()
