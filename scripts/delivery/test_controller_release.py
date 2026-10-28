"""Controller release commands protect the artifact version/notes and immutable publication boundary."""
import hashlib
import io
import tarfile
import shutil
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
COMMAND = ROOT / 'scripts/delivery/controller-release.py'

class ControllerReleaseCommands(unittest.TestCase):
    def test_version_specific_notes_are_generated_before_packaging(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / 'source'
            release = source / 'releases/controller'
            release.mkdir(parents=True)
            (release / 'version.txt').write_text('1.2.3\n')
            (release / 'CHANGELOG.md').write_text('# Changelog\n\n## [1.2.3] (2026-10-28)\n\n### Fixes\n\n* Fix token retry.\n\n## 1.2.2\n\n* Old change.\n')
            notes = Path(temporary) / 'RELEASE_NOTES.md'
            result = subprocess.run([sys.executable, str(COMMAND), 'notes', str(source), str(notes)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('Fix token retry.', notes.read_text())
            self.assertNotIn('Old change.', notes.read_text())
            value = json.loads(result.stdout)
            self.assertEqual(value['version'], '1.2.3')
            self.assertEqual(value['notesSha256'], hashlib.sha256(notes.read_bytes()).hexdigest())
            self.assertTrue(value['approved'])

    def test_composed_image_has_one_source_version_and_the_exact_offline_notes(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / 'context'
            result = subprocess.run([sys.executable, str(ROOT / 'scripts/compose-image.py'), '--source', str(ROOT),
                                     '--manifest', str(ROOT / 'image/default.json'), '--output', str(output)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            inventory = json.loads((output / 'image-inventory.json').read_text())
            version = (ROOT / 'releases/controller/version.txt').read_text().strip()
            self.assertEqual(inventory['controller']['version'], version)
            notes = (output / 'releases/controller/RELEASE_NOTES.md').read_bytes()
            self.assertEqual(inventory['controller']['releaseNotesSha256'], hashlib.sha256(notes).hexdigest())
            self.assertIn('org.opencontainers.image.version="' + version + '"', (output / 'container/Containerfile').read_text())
    def test_shipped_image_version_and_notes_must_match_the_inventory(self):
        with tempfile.TemporaryDirectory() as temporary:
            artifact = Path(temporary)
            notes = b'## 1.2.3\n\nApproved fix.\n'
            (artifact / 'RELEASE_NOTES.md').write_bytes(notes)
            (artifact / 'image-inventory.json').write_text(json.dumps({'controller': {'version': '1.2.3', 'releaseNotesSha256': hashlib.sha256(notes).hexdigest()},
                'githubCli': {'version': '2.0.0'}, 'agent': {'version': '3.0.0'}}))
            output = 'RepoAgentController version: 1.2.2\ngh version 2.0.0 (date)\ncodex-cli 3.0.0\n'
            (artifact / 'versions.txt').write_text(output)
            environment = dict(os.environ, GITHUB_SHA='a' * 40)
            result = subprocess.run([sys.executable, str(ROOT / 'scripts/delivery/image-release.py'), 'check', str(artifact)], env=environment, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0, 'Wrong compiled controller version was accepted')

@unittest.skipIf(os.name == 'nt', 'Release artifact execution uses Unix command fixtures')
class ControllerPublication(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.binary = self.root / 'bin'
        self.binary.mkdir()
        self.remote = self.root / 'remote'
        self.remote.mkdir()
        self.release = self.root / 'release.json'
        self.ledger = self.root / 'ledger.jsonl'
        gh = self.binary / 'gh'
        gh.write_text('#!' + sys.executable + '\n' + r'''
import json, os, pathlib, shutil, sys
args = sys.argv[1:]
release = pathlib.Path(os.environ['FIXTURE_RELEASE'])
remote = pathlib.Path(os.environ['FIXTURE_REMOTE'])
with open(os.environ['FIXTURE_LEDGER'], 'a') as ledger: ledger.write(json.dumps(args) + '\n')
value = json.loads(release.read_text())
if args[:1] == ['api']: print(json.dumps(value))
elif args[:2] == ['release', 'upload']:
    path = pathlib.Path(args[3]); destination = remote / path.name
    if destination.exists(): sys.exit(1)
    shutil.copyfile(path, destination)
    value.setdefault('assets', []).append({'name': path.name})
    release.write_text(json.dumps(value))
elif args[:2] == ['release', 'download']:
    name = args[args.index('--pattern') + 1]
    output = pathlib.Path(args[args.index('--dir') + 1]); output.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(remote / name, output / name)
elif args[:2] == ['release', 'edit']:
    value['body'] = pathlib.Path(args[args.index('--notes-file') + 1]).read_text()
    release.write_text(json.dumps(value))
else: sys.exit(2)
''')
        gh.chmod(0o755)
        self.environment = dict(os.environ, PATH=str(self.binary) + os.pathsep + os.environ['PATH'], GH_REPO='owner/controller',
            FIXTURE_RELEASE=str(self.release), FIXTURE_REMOTE=str(self.remote), FIXTURE_LEDGER=str(self.ledger),
            SOURCE_COMMIT='a' * 40, GITHUB_SHA='a' * 40, IMAGE='ghcr.io/repoagent/controller@sha256:' + 'b' * 64,
            REFERENCE='ghcr.io/repoagent/controller:controller-v1.2.3')

    def execute(self, *args, cwd=None):
        return subprocess.run([sys.executable, str(COMMAND), *map(str, args)], cwd=cwd, env=self.environment, capture_output=True, text=True)

    def test_select_requires_exact_tagged_main_source_and_approved_notes(self):
        source = self.root / 'source'
        directory = source / 'releases/controller'
        directory.mkdir(parents=True)
        (directory / 'version.txt').write_text('1.2.3\n')
        notes = '## 1.2.3\n\n### Fixes\n\n* Fix renewed credentials.\n'
        (directory / 'CHANGELOG.md').write_text('# Changelog\n\n' + notes)
        def git(*args):
            return subprocess.run(['git', *args], cwd=source, check=True, capture_output=True, text=True).stdout.strip()
        git('init', '-q'); git('config', 'user.name', 'Fixture'); git('config', 'user.email', 'fixture@example.invalid')
        git('add', '.'); git('commit', '-qm', 'chore: release controller1.2.3')
        commit = git('rev-parse', 'HEAD')
        git('tag', 'controller-v1.2.3'); git('update-ref', 'refs/remotes/origin/main', commit)
        self.release.write_text(json.dumps({'tag_name': 'controller-v1.2.3', 'draft': False, 'prerelease': False,
            'target_commitish': commit, 'body': notes, 'assets': []}))
        for supplied in ('RECOVERY_RUN', 'RECOVERY_ARTIFACT'):
            with self.subTest(incomplete=supplied):
                self.environment[supplied] = '123'
                refused = self.root / ('incomplete-' + supplied)
                result = self.execute('select', 'controller-v1.2.3', refused, cwd=source)
                self.assertNotEqual(result.returncode, 0, 'Incomplete recovery must fail before publishing plan outputs')
                self.assertFalse(refused.exists())
                self.assertFalse(self.ledger.exists(), 'Incomplete selection must not contact GitHub')
                del self.environment[supplied]
        output = self.root / 'plan'
        result = self.execute('select', 'controller-v1.2.3', output, cwd=source)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)['source'], commit)
        self.assertEqual((output / 'RELEASE_NOTES.md').read_text(), notes)
        self.environment.update(RECOVERY_RUN='123', RECOVERY_ARTIFACT='456')
        recovered = self.execute('select', 'controller-v1.2.3', self.root / 'recovery-plan', cwd=source)
        self.assertEqual(recovered.returncode, 0, recovered.stderr)
        self.assertEqual(json.loads(recovered.stdout)['source'], commit)
        del self.environment['RECOVERY_RUN'], self.environment['RECOVERY_ARTIFACT']
        value = json.loads(self.release.read_text()); value['body'] = 'Unreviewed changed notes.'
        self.release.write_text(json.dumps(value))
        result = self.execute('select', 'controller-v1.2.3', self.root / 'other-plan', cwd=source)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any(call[0] == 'release' for call in map(json.loads, self.ledger.read_text().splitlines())))

    def test_registry_write_requires_positive_absence_and_never_overwrites_an_existing_alias(self):
        skopeo = self.binary / 'skopeo'
        skopeo.write_text('#!' + sys.executable + '\n' + r'''
import os, sys
if '--no-creds' not in sys.argv: sys.exit(3)
state = os.environ['FIXTURE_REGISTRY']
if state == 'absent': print('manifest unknown', file=sys.stderr); sys.exit(1)
if state == 'unavailable': print('TLS handshake failed', file=sys.stderr); sys.exit(1)
print('{}')
''')
        skopeo.chmod(0o755)
        for state, accepted in [('absent', True), ('unavailable', False), ('present', False)]:
            with self.subTest(state=state):
                self.environment['FIXTURE_REGISTRY'] = state
                result = self.execute('image-free', '1.2.3')
                self.assertEqual(result.returncode == 0, accepted, result.stderr)
    def artifact(self):
        artifact = self.root / 'artifact'; artifact.mkdir()
        notes = b'## 1.2.3\n\nApproved controller fix.\n'
        (artifact / 'RELEASE_NOTES.md').write_bytes(notes)
        inventory = {'controller': {'version': '1.2.3', 'releaseNotesSha256': hashlib.sha256(notes).hexdigest()},
            'githubCli': {'version': '2.0.0'}, 'agent': {'version': '3.0.0'}}
        (artifact / 'image-inventory.json').write_text(json.dumps(inventory))
        (artifact / 'versions.txt').write_text('RepoAgentController version: 1.2.3+' + 'a' * 40 + '\ngh version 2.0.0 (date)\ncodex-cli 3.0.0\n')
        (artifact / 'build.json').write_text(json.dumps({'containerimage.config.digest': 'sha256:' + 'c' * 64}))
        archive = artifact / 'repoagent-controller-1.2.3-linux-amd64.tar.gz'
        files = {'VERSION': ('version=1.2.3\nsource_commit=' + 'a' * 40 + '\nruntime=linux-x64\n').encode(),
            'RELEASE_NOTES.md': notes, 'RepoAgentController': b'verified fixture executable',
            'github-discussion/SKILL.md': b'fixture skill', 'repoagent-orientation/SKILL.md': b'fixture skill'}
        files['SHA256SUMS'] = ''.join(hashlib.sha256(content).hexdigest() + '  ' + name + '\n' for name, content in files.items()).encode()
        with tarfile.open(archive, 'w:gz') as package:
            directory = tarfile.TarInfo('repoagent-controller-1.2.3-linux-amd64'); directory.type = tarfile.DIRTYPE; package.addfile(directory)
            for name, content in files.items():
                entry = tarfile.TarInfo('repoagent-controller-1.2.3-linux-amd64/' + name); entry.size = len(content); package.addfile(entry, io.BytesIO(content))
        (artifact / (archive.name + '.sha256')).write_text(hashlib.sha256(archive.read_bytes()).hexdigest() + '  ' + archive.name + '\n')
        plan = self.root / 'plan.json'
        plan.write_text(json.dumps({'version': '1.2.3', 'tag': 'controller-v1.2.3', 'source': 'a' * 40, 'notesSha256': hashlib.sha256(notes).hexdigest()}))
        self.release.write_text(json.dumps({'tag_name': 'controller-v1.2.3', 'body': notes.decode(), 'assets': []}))
        return artifact, plan

    def test_retry_recovers_only_the_exact_alias_from_retained_verified_artifacts(self):
        artifact, plan = self.artifact()
        state = self.root / 'registry.json'
        state.write_text(json.dumps({'alias': False, 'fail': True}))
        self.environment.update(FIXTURE_REGISTRY_STATE=str(state), GITHUB_REF='refs/heads/main',
                                GITHUB_REPOSITORY='repoagent/controller', GITHUB_RUN_ID='123', GITHUB_RUN_ATTEMPT='1')
        fixture = '#!' + sys.executable + '\n' + r'''
import json, os, pathlib, sys
args = sys.argv[1:]
state = pathlib.Path(os.environ['FIXTURE_REGISTRY_STATE'])
value = json.loads(state.read_text())
with open(os.environ['FIXTURE_LEDGER'], 'a') as log: log.write(json.dumps([pathlib.Path(sys.argv[0]).name, *args]) + '\n')
if pathlib.Path(sys.argv[0]).name == 'docker':
    if args[0] == 'push' and ':controller-v' in args[-1]: value['alias'] = True; state.write_text(json.dumps(value))
elif args[0] == 'inspect':
    if ':controller-v' in args[-1]:
        if not value['alias']: print('manifest unknown', file=sys.stderr); sys.exit(1)
        if value['fail']: value['fail'] = False; state.write_text(json.dumps(value)); sys.exit(1)
    print('sha256:' + ('d' if value.get('conflict') else 'b') * 64)
elif args[0] == 'copy':
    target = pathlib.Path(args[-1].removeprefix('dir:')); target.mkdir(exist_ok=True)
    (target / 'manifest.json').write_text(json.dumps({'config': {'digest': 'sha256:' + 'c' * 64}}))
else: sys.exit(2)
'''
        for name in ('docker', 'skopeo'):
            tool = self.binary / name; tool.write_text(fixture); tool.chmod(0o755)
        first = self.execute('publish-image', artifact, plan)
        self.assertNotEqual(first.returncode, 0, 'The injected post-alias observation must fail')
        self.assertTrue(json.loads(state.read_text())['alias'], first.stderr)
        self.assertTrue((artifact / 'publication.json').exists(), first.stderr)
        pushes = lambda: [call for call in map(json.loads, self.ledger.read_text().splitlines()) if call[:2] == ['docker', 'push']]
        self.assertEqual(len(pushes()), 2)
        second = self.execute('publish-image', artifact, plan, '--recover')
        self.assertEqual(second.returncode, 0, second.stderr)
        self.assertEqual(json.loads(second.stdout)['image'], self.environment['IMAGE'])
        self.assertEqual(len(pushes()), 2, 'Recovery must not push or move any alias')
        retained = (artifact / 'publication.json').read_bytes()
        (artifact / 'publication.json').unlink()
        self.assertNotEqual(self.execute('publish-image', artifact, plan).returncode, 0, 'An unrecorded alias is ambiguous')
        (artifact / 'publication.json').write_bytes(retained)
        value = json.loads(state.read_text()); value['conflict'] = True; state.write_text(json.dumps(value))
        self.assertNotEqual(self.execute('publish-image', artifact, plan).returncode, 0)
        self.assertEqual(len(pushes()), 2)

        value['conflict'] = False; state.write_text(json.dumps(value))
        for name in ('RELEASE_NOTES.md', 'repoagent-controller-1.2.3-linux-amd64.tar.gz', 'image-inventory.json'):
            with self.subTest(changed=name):
                path = artifact / name; original = path.read_bytes()
                path.write_bytes(b'Changed artifact')
                self.assertNotEqual(self.execute('publish-image', artifact, plan, '--recover').returncode, 0)
                self.assertEqual(len(pushes()), 2)
                path.write_bytes(original)

    def test_recovery_artifact_must_come_from_the_main_release_workflow(self):
        observations = self.root / 'observations.json'
        run = dict(path='.github/workflows/controller-release.yml', event='workflow_dispatch', head_branch='main')
        artifact = dict(name='controller-publication-123-1', expired=False, workflow_run=dict(id=123))
        observations.write_text(json.dumps(dict(run=run, artifact=artifact)))
        self.environment['FIXTURE_OBSERVATIONS'] = str(observations)
        gh = self.binary / 'gh'
        gh.write_text('#!' + sys.executable + '\n' + r'''
import json, os, pathlib, sys
value = json.loads(pathlib.Path(os.environ['FIXTURE_OBSERVATIONS']).read_text())
print(json.dumps(value['artifact' if '/artifacts/' in sys.argv[-1] else 'run']))
''')
        gh.chmod(0o755)
        self.assertEqual(self.execute('recovery-artifact', '123', '456').returncode, 0)
        for field, value in [('path', '.github/workflows/controller.yml'), ('head_branch', 'untrusted-branch'), ('event', 'pull_request')]:
            changed = dict(run); changed[field] = value
            observations.write_text(json.dumps(dict(run=changed, artifact=artifact)))
            self.assertNotEqual(self.execute('recovery-artifact', '123', '456').returncode, 0, field)

    def test_verified_publication_is_retry_safe_and_never_overwrites_conflicting_assets(self):
        artifact, plan = self.artifact()
        first = self.execute('finalize', artifact, plan)
        self.assertEqual(first.returncode, 0, first.stderr)
        self.assertTrue(json.loads(first.stdout)['verified'])
        uploads = lambda: [call for call in map(json.loads, self.ledger.read_text().splitlines()) if call[:2] == ['release', 'upload']]
        self.assertEqual(len(uploads()), 4)
        second = self.execute('finalize', artifact, plan)
        self.assertEqual(second.returncode, 0, second.stderr)
        self.assertEqual(len(uploads()), 4)
        (self.remote / 'RELEASE_NOTES.md').write_text('Conflicting released content')
        third = self.execute('finalize', artifact, plan)
        self.assertNotEqual(third.returncode, 0)
        self.assertEqual((self.remote / 'RELEASE_NOTES.md').read_text(), 'Conflicting released content')
        self.assertEqual(len(uploads()), 4)
if __name__ == '__main__':
    unittest.main()
