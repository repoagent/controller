"""Public PowerShell enrollment; GitHub is replaced only at its executable boundary.

Linux execution does not establish native Windows or real broker acceptance.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class HostEnrollmentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which('pwsh'):
            raise RuntimeError('Enrollment acceptance requires PowerShell 7 on PATH')
        if not shutil.which('dotnet'):
            raise RuntimeError('The native gh fixture requires the .NET 10 SDK')
        temporary = tempfile.TemporaryDirectory(prefix='enrollment gh fixture ')
        cls.addClassCleanup(temporary.cleanup)
        build = Path(temporary.name)
        shutil.copytree(Path(__file__).with_name('GhFixture'), build / 'project',
                        ignore=shutil.ignore_patterns('bin', 'obj'))
        # Build outside the repository's unrelated SDK pin.
        (build / 'global.json').write_text(json.dumps({'sdk': {
            'version': '10.0.100', 'rollForward': 'latestFeature', 'allowPrerelease': False}}),
            encoding='utf-8')
        cls.fixture_build = build / 'output'
        result = subprocess.run(['dotnet', 'build', str(build / 'project/GhFixture.csproj'),
                                 '--output', str(cls.fixture_build), '--nologo',
                                 '--verbosity', 'quiet', '--ignore-failed-sources'],
                                cwd=build, capture_output=True, text=True, encoding='utf-8', timeout=120)
        if result.returncode:
            raise RuntimeError('Native gh fixture build failed:\n' + result.stdout + result.stderr)

    def test_preview_identifies_selected_repository_agent_variable_without_mutations(self):
        with tempfile.TemporaryDirectory(prefix='enrollment preview ') as temporary:
            directory = Path(temporary)
            for artifact in self.fixture_build.iterdir():
                shutil.copy2(artifact, directory / artifact.name)
            scenario = directory / 'scenario.json'
            # These are external API responses under an authenticated host credential,
            # not broker responses or enrollment decisions. Preview may remain offline.
            scenario.write_text(json.dumps([
                {'body': {'id': 123, 'full_name': 'example/project'}},
                {'body': {'total_count': 0, 'variables': []}},
            ]), encoding='utf-8')
            log = directory / 'requests.jsonl'
            state = directory / 'private-state'
            environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'],
                               REVOKE_GH_SCENARIO=str(scenario), REVOKE_GH_LOG=str(log),
                               REVOKE_GH_PYTHON=sys.executable,
                               REVOKE_GH_FIXTURE=str(Path(__file__).with_name('gh_fixture.py').resolve()),
                               GH_TOKEN='HOST-CREDENTIAL-MUST-STAY-ON-HOST', PYTHONUTF8='1')
            expected_gh = directory / ('gh.exe' if os.name == 'nt' else 'gh')
            self.assertEqual(Path(shutil.which('gh', path=environment['PATH']) or '').resolve(),
                             expected_gh.resolve(), 'Fixture must take precedence over host gh')
            result = subprocess.run([
                'pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'install.ps1'),
                '-Action', 'Enroll', '-NonInteractive', '-Repository', 'example/project',
                '-BrokerOrigin', 'https://broker.example.test',
                '-ApprovalVariable', 'TEAM_CONTROLLER_APPROVAL',
                '-EnrollmentProfile', 'polling', '-StateDirectory', str(state),
            ], cwd=directory, env=environment, input='', capture_output=True, text=True,
                encoding='utf-8', timeout=30)
            output = result.stdout + result.stderr
            self.assertEqual(result.returncode, 0, output)
            self.assertFalse(state.exists(), 'Preview must not allocate private enrollment state')
            self.assertNotIn(environment['GH_TOKEN'], output)
            events = [json.loads(line) for line in log.read_text(encoding='utf-8').splitlines()] \
                if log.exists() else []
            for event in events:
                argv = event['argv']
                self.assertEqual(argv[0], 'api', event)
                self.assertEqual(argv[argv.index('--method') + 1], 'GET', event)
                self.assertNotIn('/environments', argv[-1], event)
            self.assertIn('TEAM_CONTROLLER_APPROVAL', output)
            self.assertRegex(output, r'(?i)agent[- ]variable',
                             'Preview must identify the repository Agent-variable approval coordinate')
            self.assertNotIn('GitHub Environment', output)


if __name__ == '__main__':
    unittest.main()
