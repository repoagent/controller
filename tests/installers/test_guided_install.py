"""Guided public installer orchestration; external Docker/GitHub/broker doubles.

These process tests retain real PowerShell, enrollment client and private files.
Actual broker/Table/DataProtection/container/native and Windows acceptance are
separate gates; a process-boundary Docker fixture cannot establish readiness there.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest

import test_host_enrollment as host
import test_private_runtime as runtime

ROOT = Path(__file__).resolve().parents[2]
IMAGE = 'sha256:' + 'a' * 64


def snapshot(directory):
    return {str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in directory.rglob('*') if path.is_file()}


def mutating_docker_commands(events):
    # A run creates a container even for queries such as `get` or `versions`.
    return [event for event in events if event[0] in
            ('run', 'build', 'pull', 'tag', 'start', 'stop', 'rm', 'create') or
            event[:2] in (['volume', 'create'], ['volume', 'rm'], ['container', 'start'])]


class GuidedInstallTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Existing fixture setup builds only native executable transport shims.
        host.HostEnrollmentTests.setUpClass.__func__(cls)
        gh_build = cls.fixture_build
        runtime.PrivateRuntimeTests.setUpClass.__func__(cls)
        cls.docker_build = cls.fixture_build
        cls.fixture_build = gh_build

    def setUp(self):
        self.journey = host.EnrollmentJourney(self, variable='APPROVED_CONTROLLER')
        self.owned = self.journey.directory / 'operator'
        self.owned.mkdir(mode=0o700)
        self.journey.state = self.owned / 'guided-red'
        self.auth = self.owned / 'auth.json'
        self.auth.write_text(json.dumps({'OPENAI_API_KEY': 'GUIDED-FAKE-CODEX-CREDENTIAL'}))
        self.auth.chmod(0o600)
        self.docker_state = self.journey.directory / 'docker-state.json'
        if self.native:
            for artifact in self.docker_build.iterdir():
                shutil.copy2(artifact, self.journey.directory / artifact.name)
        else:
            docker = self.journey.directory / 'docker'
            shutil.copyfile(Path(__file__).with_name('docker_fixture.py'), docker)
            docker.chmod(0o755)
        self.environment = dict(self.journey.environment,
            REPOAGENT_IMAGE=IMAGE, CODEX_HOME=str(self.owned / 'unused-codex-home'),
            INSTALLER_DOCKER_STATE=str(self.docker_state),
            INSTALLER_DOCKER_REAL_CONFIGURE=str(ROOT / 'container'),
            INSTALLER_DOCKER_PYTHON=sys.executable,
            INSTALLER_DOCKER_FIXTURE=str(Path(__file__).with_name('docker_fixture.py').resolve()))
        self.assertEqual(Path(shutil.which('docker', path=self.environment['PATH'])).resolve(),
                         self.journey.directory / ('docker.exe' if os.name == 'nt' else 'docker'))

    def install(self, *, apply):
        command = ['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'install.ps1'),
            '-Action', 'Install', '-Repository', 'example/project',
            '-BrokerOrigin', self.journey.origin, '-Instance', 'guided-red',
            '-StateDirectory', str(self.journey.state), '-EnrollmentProfile', 'polling',
            '-Mode', 'polling', '-CodexAuthPath', str(self.auth), '-NonInteractive']
        if apply:
            command += ['-SkipBuild', '-Apply']
        result = subprocess.run(command, cwd=self.journey.directory, env=self.environment,
            input='', capture_output=True, text=True, encoding='utf-8', timeout=90)
        state = json.loads(self.docker_state.read_text()) if self.docker_state.exists() else {}
        output = result.stdout + result.stderr
        for secret in ('GUIDED-FAKE-CODEX-CREDENTIAL', self.environment['GH_TOKEN'], 's' * 43):
            self.assertNotIn(secret, output)
        self.assertEqual(self.journey.errors, [], 'Broker fixture failed')
        print('GUIDED_PROCESS ' + json.dumps({'apply': apply, 'command': command,
            'returncode': result.returncode, 'output': output,
            'dockerEvents': state.get('events', []),
            'running': state.get('running', False),
            'brokerRequests': [event['path'] for event in self.journey.events],
            'enrollmentExists': (self.journey.state / 'enrollment.json').exists()}), flush=True)
        return result, state

    def test_apply_complete_broker_inputs_enroll_and_start_selected_runtime(self):
        result, state = self.install(apply=True)
        self.assertTrue(state.get('running', False),
                        'Broker installation did not reach runtime readiness:\n' + result.stdout + result.stderr)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((self.journey.state / 'enrollment.json').is_file())
        self.assertIn('repoagent-guided-red', state.get('containers', {}))

    def test_preview_complete_broker_inputs_leave_host_and_docker_unchanged(self):
        before = snapshot(self.owned)
        result, state = self.install(apply=False)
        self.assertEqual(snapshot(self.owned), before, 'Preview changed selected host state')
        self.assertEqual(mutating_docker_commands(state.get('events', [])), [],
                         'Preview performed Docker mutations before explicit Apply')
        self.assertEqual(self.journey.events, [], 'Preview allocated broker enrollment')
        self.assertFalse(self.journey.log.exists(), 'Preview invoked host GitHub')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
