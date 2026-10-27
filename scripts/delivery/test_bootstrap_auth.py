"""Bootstrap CLI with real Git archives and captured Docker context bytes.

The committed verify script is a minimal fixture, not actual service verification.
Git/archive/files are real; only malformed ls-remote replies and provider processes are doubled.
"""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from test_auth_pipeline import SCRIPT, SOURCE_FILES, REPOSITORY, IMAGE, CANARY, SDK


BOUNDARY = '''import json, os, pathlib, subprocess, sys
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
if name == 'docker' and args[:1] == ['build'] and settings.get('advance'):
    subprocess.run([os.environ['REAL_GIT'], '--git-dir', os.environ['REMOTE'], 'update-ref', 'refs/heads/main', os.environ['NEXT_SHA']], check=True)
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
        for name in ('global.json', 'service/auth/global.json'):
            (self.root / name).write_text(json.dumps(dict(sdk=dict(version=SDK, rollForward='disable', allowPrerelease=True))))
        (self.root / 'service/auth/verify.sh').write_text('test "$(cat service/auth/Dockerfile)" = "committed bytes"\n')
        self.git('init', '-qb', 'main')
        self.git('add', '.')
        self.git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.test', 'commit', '-qm', 'accepted source')
        self.sha = self.git('rev-parse', 'HEAD').strip()
        self.remote = self.root / 'remote.git'
        self.next_sha = self.git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.test',
                                 'commit-tree', 'HEAD^{tree}', '-p', self.sha, '-m', 'advanced main').strip()
        self.git('clone', '--bare', '-q', str(self.root), str(self.remote))
        self.binaries = self.root / 'bin'
        self.binaries.mkdir()
        for name in ('docker', 'skopeo', 'gh', 'az'):
            executable = self.binaries / name
            executable.write_text(f'#!{sys.executable}\n' + BOUNDARY)
            executable.chmod(0o700)
        for name in ('tar', 'bash', 'cat'):
            (self.binaries / name).symlink_to(shutil.which(name))
        git = self.binaries / 'git'
        git.write_text(f'#!{sys.executable}\n' + '''import json, os, pathlib, sys
settings = json.loads(pathlib.Path(os.environ['SETTINGS']).read_text())
if sys.argv[1:4] == ['ls-remote', 'https://github.com/repoagent/controller.git', 'refs/heads/main'] and 'remote' in settings:
    print(settings['remote'])
    sys.exit(settings.get('remote_exit', 0))
os.execv(os.environ['REAL_GIT'], ['git', *sys.argv[1:]])
''')
        git.chmod(0o700)
        self.ledger = self.root / 'calls.jsonl'
        self.settings = self.root / 'settings.json'
        self.env = dict(PATH=str(self.binaries), HOME=str(self.root), LANG='C.UTF-8',
                        LEDGER=str(self.ledger), SETTINGS=str(self.settings), CANARY=CANARY,
                        AUTH_IMAGE_REPOSITORY=REPOSITORY, AUTH_IMAGE=IMAGE, GITHUB_SHA=self.sha,
                        AUTH_PACKAGE_ACTIONS_ACCESS_CONFIRMED='true',
                        AUTH_EXPECTED_SOURCE_SHA=self.sha, GITHUB_REF='refs/heads/main',
                        REAL_GIT=shutil.which('git'), REMOTE=str(self.remote), NEXT_SHA=self.next_sha,
                        GIT_CONFIG_COUNT='1', GIT_CONFIG_KEY_0='url.' + self.remote.as_uri() + '.insteadOf',
                        GIT_CONFIG_VALUE_0='https://github.com/repoagent/controller.git')
        self.env.update({key: 'false' for key in ('AUTH_DEPLOYMENT_READY', 'AUTH_PROVIDER_READY',
                        'AUTH_STORAGE_READY', 'AUTH_CLEANUP_READY', 'AUTH_RUNTIME_ACCESS_READY')})

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
        self.assertEqual(build['context']['service/auth/Dockerfile'], 'committed bytes\n')
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
        for verb in ('bootstrap-publish', 'bootstrap-publish-actions'):
            with self.subTest(verb=verb):
                result = self.invoke({'private': True}, verb=verb)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(json.loads(result.stdout), dict(source=self.sha, image=IMAGE, publicationComplete=False))
                self.assertIn('grant Actions access', result.stderr)
                self.assertIn('do not rebuild to resume', result.stderr)
                result = self.invoke(verb='attest', env={'AUTH_EXPECTED_SOURCE_SHA': self.sha})
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual([call['name'] for call in self.calls], ['skopeo'])

    def test_public_digest_without_confirmed_actions_access_is_still_incomplete(self):
        for verb in ('bootstrap-publish', 'bootstrap-publish-actions'):
            result = self.invoke(env={'AUTH_PACKAGE_ACTIONS_ACCESS_CONFIRMED': ''}, verb=verb)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(json.loads(result.stdout), dict(source=self.sha, image=IMAGE, publicationComplete=False))
            self.assertIn('Actions package access', result.stderr)

    def test_actions_accepted_main_publishes_without_readiness_or_azure(self):
        result = self.invoke(verb='bootstrap-publish-actions')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([call['name'] for call in self.calls], ['docker'] * 4 + ['skopeo'])
        self.assertEqual(json.loads(result.stdout.splitlines()[-1]),
                         dict(source=self.sha, image=IMAGE, publicationComplete=True))

    def test_actions_rejects_invalid_source_or_remote_before_build(self):
        cases = [({'AUTH_EXPECTED_SOURCE_SHA': value}, {}) for value in ('', self.sha[:12], 'main;echo bad')]
        cases += [({key: value}, {}) for key, value in (('GITHUB_SHA', 'f' * 40),
                  ('AUTH_EXPECTED_SOURCE_SHA', 'f' * 40), ('GITHUB_REF', ''), ('GITHUB_REF', 'refs/heads/topic'))]
        cases += [({}, {'remote': value}) for value in ('', 'malformed', self.next_sha + ' refs/heads/main',
                  (self.sha + ' refs/heads/main\n') * 2)]
        cases += [({}, {'remote': CANARY, 'remote_exit': 1})]
        for env, settings in cases:
            with self.subTest(env=env, settings=settings):
                result = self.invoke(settings, env, 'bootstrap-publish-actions')
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.calls, [], 'Rejected source must never build')
        self.git('update-ref', 'refs/heads/main', self.next_sha)
        result = self.invoke(verb='bootstrap-publish-actions')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.calls, [])

    def test_actions_main_advancing_during_build_never_pushes(self):
        result = self.invoke({'advance': True}, verb='bootstrap-publish-actions')
        self.assertEqual([call['args'][0] for call in self.calls], ['build', 'image'])
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, '')

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
