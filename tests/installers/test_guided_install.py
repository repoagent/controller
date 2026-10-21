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


def readonly_lifecycle_inspection(event):
    """Exact approved Apply-only transport; extra options or mounts remain effects."""
    expected = ['run', '--rm', '-i', '--network', 'none', '--user', '1000',
        '--mount', f'type=bind,src={ROOT / "container/local-lifecycle.py"},dst=/source/local-lifecycle.py,readonly',
        '--mount', 'type=volume,src=repoagent-guided-red-enrollment,dst=/data/enrollment,readonly',
        '--mount', 'type=volume,src=repoagent-guided-red-config,dst=/data/config,readonly',
        '--entrypoint', '/usr/bin/python3', IMAGE, '-E', '/source/local-lifecycle.py', 'inspect']
    return event == expected


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

    def setUp(self, profile='polling'):
        self.journey = host.EnrollmentJourney(self, variable='APPROVED_CONTROLLER', profile=profile)
        self.directory = self.journey.directory
        if os.name == 'nt':
            # #73's checked reader also validates all native ancestors. Shared
            # TEMP may grant other accounts delete rights; use LOCALAPPDATA.
            import tempfile
            self.owned = Path(self.enterContext(tempfile.TemporaryDirectory(
                prefix='guided private ', dir=Path(os.environ['LOCALAPPDATA']).resolve())))
            runtime.PrivateRuntimeTests.set_windows_private_acl(self, self.owned, directory=True)
        else:
            self.owned = self.journey.directory / 'operator'
            self.owned.mkdir(mode=0o700)
        self.journey.state = self.owned / 'guided-red'
        self.auth = self.owned / 'auth.json'
        self.auth.write_text(json.dumps({'OPENAI_API_KEY': 'GUIDED-FAKE-CODEX-CREDENTIAL'}))
        self.auth.chmod(0o600)
        if os.name == 'nt':
            runtime.PrivateRuntimeTests.set_windows_private_acl(self, self.auth)
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
            INSTALLER_DOCKER_LEGACY_ABSENT_MARKER='1',
            INSTALLER_DOCKER_REAL_CONFIGURE=str(ROOT / 'container'),
            INSTALLER_DOCKER_PYTHON=sys.executable,
            INSTALLER_DOCKER_FIXTURE=str(Path(__file__).with_name('docker_fixture.py').resolve()))
        self.assertEqual(Path(shutil.which('docker', path=self.environment['PATH'])).resolve(),
                         (self.journey.directory / ('docker.exe' if os.name == 'nt' else 'docker')).resolve())

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

    def test_fresh_clone_preview_selects_context_profile_and_stays_read_only(self):
        for mode in ('polling', 'fixed-url', 'quick-tunnel'):
            with self.subTest(mode=mode):
                before = snapshot(self.owned)
                options = {'Clone': None, 'Mode': mode}
                if mode == 'fixed-url': options['PublicUrl'] = 'https://listener.example'
                result, _ = self.run_guided(apply=False, interactive=mode == 'polling', overrides=options)
                self.assertNotIn('Clone this repository', result.prompt_output)
                self.assert_read_only(before)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                output = (result.stdout + result.stderr).lower()
                for expected in ('polling-clone' if mode == 'polling' else 'webhook-clone',
                                 'contents read', 'discussions write', '/workspace/repository', 'trust', 'instructions', 'configuration'):
                    self.assertIn(expected, output)
                if mode != 'polling': self.assertRegex(output, r'(hooks|webhooks).*write')
        before = snapshot(self.owned)
        result, _ = self.run_guided(apply=False, overrides={'Clone': None, 'EnrollmentProfile': 'webhook-clone'})
        self.assertNotEqual(result.returncode, 0)
        self.assert_read_only(before)

    def test_fresh_clone_apply_binds_v2_profile_and_saved_resume_retains_authority(self):
        for mode in ('polling', 'fixed-url', 'quick-tunnel'):
            with self.subTest(mode=mode):
                profile = 'polling-clone' if mode == 'polling' else 'webhook-clone'
                self.setUp(profile=profile)
                options = {'Clone': None, 'Mode': mode}
                if mode == 'fixed-url': options['PublicUrl'] = 'https://listener.example'
                result, state = self.run_guided(overrides=options)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                receipt, approval = self.journey.read('enrollment.json'), self.journey.read('approval.json')
                self.assertEqual(receipt['protocolVersion'], 2)
                self.assertEqual(receipt['profile'], profile)
                self.assertEqual(receipt['policyVersion'], 'repository-agent-clone-approval-v2')
                self.assertEqual(receipt['permissions']['contents'], 'read')
                self.assertEqual(receipt['permissions']['discussions'], 'write')
                self.assertEqual(receipt['permissions'].get('repository_hooks'), None if mode == 'polling' else 'write')
                self.assertEqual(approval['setupVersion'], 2)
                data = (self.journey.state / 'enrollment.json').read_bytes()
                self.assertEqual(state['imports'], [{'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()}])
                self.assertTrue(any('--clone' in event and 'configure' in event for event in state['events']))
                self.assertTrue(state['settings']['clone'])
                self.assertEqual(state['settings']['mode'], mode)
                self.assertTrue(state['running'])
                self.assertEqual(self.journey.read('launch.json')['enrollmentProfile'], profile)
                self.journey.private()
                stopped = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'), 'stop'],
                    cwd=self.directory, env=dict(self.environment, REPOAGENT_INSTANCE='guided-red'), capture_output=True, text=True, timeout=45)
                self.assertEqual(stopped.returncode, 0, stopped.stdout + stopped.stderr)
                result, state = self.run_guided(saved=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertTrue(state['settings']['clone'])
                self.assertEqual(self.journey.read('enrollment.json'), receipt)
                self.assertEqual(self.journey.read('approval.json'), approval)
                self.assertEqual(sum(event['path'] == '/v2/enrollments' for event in self.journey.events), 1)
                self.assertEqual(sum('POST' in event['argv'] for event in self.journey.github()), 1)

    def test_saved_ordinary_v1_enrollment_cannot_upgrade_by_clone_flag(self):
        result, state = self.run_guided()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.journey.read('enrollment.json')['protocolVersion'], 1)
        for legacy in (False, True):
            if legacy:
                launch = self.journey.read('launch.json'); del launch['clone']
                (self.journey.state / 'launch.json').write_text(json.dumps(launch))
            before, count, requests = snapshot(self.owned), len(state['events']), len(self.journey.events)
            result, state = self.run_guided(saved=True, overrides={'Clone': None})
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('fresh instance', result.stdout + result.stderr)
            self.assertEqual(snapshot(self.owned), before)
            self.assertEqual(mutating_docker_commands(event for event in state['events'][count:] if not readonly_lifecycle_inspection(event)), [])
            self.assertEqual(len(self.journey.events), requests)

    def test_context_question_defaults_no_only_when_fresh_input_prompting_is_needed(self):
        before = snapshot(self.owned)
        result, _ = self.run_guided(apply=False, interactive=True,
            overrides={'Repository': False}, answers='example/project\n\n')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.prompt_output.count('Clone this repository'), 1, result.prompt_output)
        self.assertNotIn('polling-clone', result.stdout)
        self.assert_read_only(before)
        result, _ = self.run_guided(apply=False, interactive=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn('Clone this repository', result.prompt_output)
        self.assert_read_only(before)

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


    def run_interactive_process(self, command, answers, environment=None):
        if os.name == 'nt':
            # Read-Host writes directly to the Win32 screen buffer, bypassing
            # redirected stdout. The detached worker owns its console and still
            # runs this exact public process with ordinary stdin/stdout pipes.
            capture = subprocess.run([sys.executable, str(Path(__file__).with_name('windows_prompt_capture.py'))],
                cwd=self.journey.directory, env=dict(self.environment, **(environment or {})),
                input=json.dumps({'command': command, 'answers': answers}), capture_output=True,
                text=True, encoding='utf-8', timeout=100, creationflags=subprocess.DETACHED_PROCESS)
            self.assertEqual(capture.returncode, 0, capture.stdout + capture.stderr)
            captured = json.loads(capture.stdout)
            result = subprocess.CompletedProcess(command, captured['returncode'], captured['stdout'], captured['stderr'])
            result.prompt_output = captured['console']
        else:
            result = subprocess.run(command, cwd=self.journey.directory,
                env=dict(self.environment, **(environment or {})), input=answers,
                capture_output=True, text=True, encoding='utf-8', timeout=90)
            result.prompt_output = result.stdout
        return result

    def run_guided(self, *, saved=False, apply=True, overrides=None, interactive=False, answers='', environment=None):
        options = {'Repository': 'example/project', 'BrokerOrigin': self.journey.origin,
                   'CodexAuthPath': str(self.auth), 'SkipBuild': None} if not saved else {}
        options.update({'Instance': 'guided-red', 'StateDirectory': str(self.journey.state)})
        if apply:
            options['Apply'] = None
        if not interactive:
            options['NonInteractive'] = None
        for name, value in (overrides or {}).items():
            if value is False:
                options.pop(name, None)
            else:
                options[name] = value
        command = ['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'install.ps1')]
        for name, value in options.items():
            command.append('-' + name)
            if value is not None:
                command.append(str(value))
        if interactive:
            result = self.run_interactive_process(command, answers, environment)
        else:
            result = subprocess.run(command, cwd=self.journey.directory,
                env=dict(self.environment, **(environment or {})), input=answers,
                capture_output=True, text=True, encoding='utf-8', timeout=90)
            result.prompt_output = result.stdout
        state = json.loads(self.docker_state.read_text()) if self.docker_state.exists() else {}
        output = result.stdout + result.stderr
        secrets = ['GUIDED-FAKE-CODEX-CREDENTIAL', self.environment['GH_TOKEN'], 's' * 43]
        if (self.journey.state / 'recovery.json').exists():
            secrets.append(self.journey.read('recovery.json').get('secret'))
        for secret in filter(None, secrets):
            self.assertNotIn(secret, output + result.prompt_output)
        self.assertEqual(self.journey.errors, [], 'Broker fixture failed')
        print('GUIDED_CONFORMANCE ' + json.dumps({'test': self.id(), 'command': command,
            'returncode': result.returncode, 'output': output,
            'promptOutput': result.prompt_output if interactive else None,
            'running': state.get('running', False), 'dockerEvents': state.get('events', []),
            'brokerRequests': [event['path'] for event in self.journey.events]}), flush=True)
        return result, state

    def test_failed_credential_import_keeps_enrollment_and_minimal_resume_recovers_saved_auth(self):
        result, state = self.run_guided(environment={'INSTALLER_DOCKER_FAIL_OPERATION': 'import-codex-auth'})
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(state.get('running'))
        self.assertFalse(state.get('authenticated'))
        enrollment = self.journey.read('enrollment.json')
        approval = self.journey.read('approval.json')
        launch = self.journey.read('launch.json')
        self.assertEqual(launch['codexAuthPath'], str(self.auth))
        self.assertEqual(launch['runtimeImageId'], IMAGE)
        self.assertIn('repoagent-guided-red-credentials', state['volumes'])
        result, state = self.run_guided(saved=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(state['running'])
        self.assertEqual(self.journey.read('enrollment.json'), enrollment)
        self.assertEqual(self.journey.read('approval.json'), approval)
        self.assertEqual(self.journey.read('launch.json')['runtimeImageId'], IMAGE)
        self.assertEqual(sum(event['path'] == '/v2/enrollments' for event in self.journey.events), 1)
        self.assertEqual(sum('POST' in event['argv'] for event in self.journey.github()), 1)

    def test_check_failure_keeps_working_credentials_and_saved_resume_does_not_replace_them(self):
        result, state = self.run_guided(environment={'INSTALLER_DOCKER_FAIL_OPERATION': 'check'})
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(state['authenticated'])
        self.assertFalse(state['running'])
        enrollment = self.journey.read('enrollment.json')
        approval = self.journey.read('approval.json')
        before = len(state['events'])
        # A working refresh cache must remain usable even when the original
        # selected host file is no longer available.
        self.auth.unlink()
        result, state = self.run_guided(saved=True,
            environment={'REPOAGENT_IMAGE': 'ambient-tag-that-must-not-replace-saved-image'})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(state['running'])
        self.assertFalse(any('--entrypoint' in event and '/usr/bin/python3' in event and not readonly_lifecycle_inspection(event)
                             for event in state['events'][before:]))
        self.assertEqual(state['containers']['repoagent-guided-red']['Image'], IMAGE)
        self.assertEqual(self.journey.read('enrollment.json'), enrollment)
        self.assertEqual(self.journey.read('approval.json'), approval)
        self.assertEqual(sum(event['path'] == '/v2/enrollments' for event in self.journey.events), 1)
        self.assertEqual(sum('POST' in event['argv'] for event in self.journey.github()), 1)


    def assert_read_only(self, before):
        state = json.loads(self.docker_state.read_text()) if self.docker_state.exists() else {}
        self.assertEqual(snapshot(self.owned), before)
        self.assertEqual(mutating_docker_commands(state.get('events', [])), [])
        self.assertEqual(self.journey.events, [])
        self.assertFalse(self.journey.log.exists())

    def test_noninteractive_missing_repository_names_option_before_mutation(self):
        before = snapshot(self.owned)
        result, _ = self.run_guided(overrides={'Repository': False})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('-Repository', result.stdout + result.stderr)
        self.assert_read_only(before)

    def test_interactive_missing_repository_prompts_once_and_complete_values_never_prompt(self):
        before = snapshot(self.owned)
        # An unrelated native prompt must be observed as itself, never as the
        # expected installer text. This also rejects an empty capture oracle.
        control = self.run_interactive_process(['pwsh', '-NoLogo', '-NoProfile', '-Command',
            "Read-Host 'Fixture control prompt'"], 'control-input\n')
        self.assertEqual(control.returncode, 0, control.stdout + control.stderr)
        self.assertEqual(control.prompt_output.count('Fixture control prompt'), 1, control.prompt_output)
        self.assertNotIn('Supply -Repository', control.prompt_output)
        self.assertIn('control-input', control.stdout)
        result, _ = self.run_guided(apply=False, interactive=True,
            overrides={'Repository': False}, answers='example/project\n')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.prompt_output.count('Supply -Repository'), 1, result.prompt_output)
        self.assertIn('Repository: example/project;', result.stdout)
        self.assertNotIn('Supply -BrokerOrigin', result.prompt_output)
        self.assertNotIn('Supply -CodexAuthPath', result.prompt_output)
        self.assert_read_only(before)
        result, _ = self.run_guided(apply=False, interactive=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn('Supply -', result.prompt_output)
        self.assertIn('Repository: example/project;', result.stdout)
        self.assert_read_only(before)

    def test_unsupported_broker_profile_and_mode_stop_before_mutation(self):
        before = snapshot(self.owned)
        for overrides in ({'EnrollmentProfile': 'webhook'}, {'Mode': 'quick-tunnel'},
                          {'Mode': 'fixed-url', 'PublicUrl': 'https://listener.example'}):
            with self.subTest(overrides=overrides):
                result, _ = self.run_guided(overrides=overrides)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('polling', result.stdout + result.stderr)
                self.assert_read_only(before)

    def test_default_state_coordinates_are_separate_for_two_preview_instances(self):
        before = snapshot(self.owned)
        for instance in ('guided-preview-a', 'guided-preview-b'):
            with self.subTest(instance=instance):
                result, _ = self.run_guided(apply=False,
                    overrides={'StateDirectory': False, 'Instance': instance})
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn('.repoagent/instances/' + instance, result.stdout.replace('\\', '/'))
                self.assertIn('repoagent-' + instance + '-enrollment', result.stdout)
                self.assertIn('pending enrollment', result.stdout)
                self.assert_read_only(before)

    def test_invalid_manifest_stops_before_broker_or_docker_mutation(self):
        manifest = self.owned / 'invalid-image.json'
        manifest.write_text('{"schemaVersion":1,"unknown":true}')
        before = snapshot(self.owned)
        result, _ = self.run_guided(overrides={'ImageManifest': str(manifest)})
        self.assertNotEqual(result.returncode, 0)
        self.assert_read_only(before)

    def test_custom_variable_and_options_persist_and_explicit_nonbinding_options_win(self):
        self.journey.variable = 'TEAM_CONTROLLER_APPROVAL'
        marker = json.loads(self.journey.marker)
        marker['variable'] = self.journey.variable
        self.journey.marker = json.dumps(marker, separators=(',', ':'))
        result, state = self.run_guided(overrides={'ApprovalVariable': 'team_controller_approval',
            'Model': 'saved-model', 'ToolUpdates': 'pinned', 'WebSearch': 'cached', 'NoStart': None})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(state['running'])
        self.assertIn('remains stopped', result.stdout)
        enrollment = self.journey.read('enrollment.json')
        approval = self.journey.read('approval.json')
        launch = self.journey.read('launch.json')
        self.assertEqual(launch['approvalVariable'], self.journey.variable)
        self.assertEqual(launch['model'], 'saved-model')
        before = snapshot(self.owned)
        event_count = len(state['events'])
        broker_count = len(self.journey.events)
        result, state = self.run_guided(saved=True, apply=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('model: saved-model', result.stdout)
        self.assertIn(self.journey.variable, result.stdout)
        self.assertIn(enrollment['deviceId'], result.stdout)
        self.assertEqual(snapshot(self.owned), before)
        self.assertEqual(mutating_docker_commands(state['events'][event_count:]), [])
        self.assertEqual(len(self.journey.events), broker_count)
        result, state = self.run_guided(saved=True, overrides={'Model': 'explicit-model'})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(state['running'])
        self.assertEqual(state['settings']['model'], 'explicit-model')
        self.assertEqual(state['settings']['toolUpdates'], 'pinned')
        self.assertEqual(state['settings']['webSearch'], 'cached')
        self.assertEqual(self.journey.read('approval.json'), approval)
        self.assertEqual(self.journey.read('enrollment.json'), enrollment)
        self.assertEqual(sum('POST' in event['argv'] for event in self.journey.github()), 1)

    def test_host_approval_conflict_prevents_runtime_and_preserves_launch_and_receipt(self):
        result, state = self.run_guided(overrides={'NoStart': None})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        before = snapshot(self.owned)
        count = len(state['events'])
        result, state = self.run_guided(saved=True, overrides={'ApprovalVariable': 'DIFFERENT_APPROVAL'})
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(snapshot(self.owned), before)
        self.assertEqual(mutating_docker_commands(event for event in state['events'][count:] if not readonly_lifecycle_inspection(event)), [])
        self.assertFalse(state['running'])
        result, state = self.run_guided(saved=True, overrides={'Instance': 'different-instance'})
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(snapshot(self.owned), before)
        self.assertFalse(state['running'])

    def test_host_discovery_failure_prevents_any_runtime_mutation(self):
        self.journey.model.write_text(json.dumps({'variables': {}, 'listing_status': 403}))
        result, state = self.run_guided()
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(state.get('running', False))
        self.assertEqual(mutating_docker_commands(state.get('events', [])), [])
        self.assertEqual(self.journey.events, [])

    def assert_failure_then_same_identity_resume(self, failure):
        result, state = self.run_guided(environment={'INSTALLER_DOCKER_FAIL_OPERATION': failure})
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(state['running'])
        enrollment = self.journey.read('enrollment.json')
        approval = self.journey.read('approval.json')
        launch = self.journey.read('launch.json')
        self.assertEqual(launch['runtimeImageId'], IMAGE)
        result, state = self.run_guided(saved=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(state['running'])
        self.assertEqual(self.journey.read('enrollment.json'), enrollment)
        self.assertEqual(self.journey.read('approval.json'), approval)
        self.assertEqual(self.journey.read('launch.json')['runtimeImageId'], IMAGE)
        self.assertEqual(sum(event['path'] == '/v2/enrollments' for event in self.journey.events), 1)
        self.assertEqual(sum('POST' in event['argv'] for event in self.journey.github()), 1)

    def test_failed_enrollment_import_resumes_same_identity(self):
        self.assert_failure_then_same_identity_resume('import-enrollment')

    def test_failed_configuration_resumes_same_identity(self):
        self.assert_failure_then_same_identity_resume('configure')

    def test_failed_login_resumes_same_identity(self):
        self.assert_failure_then_same_identity_resume('login')

    def test_failed_start_resumes_same_identity(self):
        self.assert_failure_then_same_identity_resume('start')


    def assert_preview_build_identity(self, result, manifest, derived=None):
        # Expected identities come from fixture coordinates/bytes, independently
        # of the installer's saved plan or formatting choices.
        identities = {'manifest path': str(manifest.resolve()),
                      'controller source': json.loads(manifest.read_text())['controller']['source']}
        if derived:
            identities['derived Dockerfile path'] = str(derived.resolve())
            identities['derived Dockerfile SHA256'] = hashlib.sha256(derived.read_bytes()).hexdigest()
        for field, value in identities.items():
            with self.subTest(preview_identity=field):
                self.assertIn(value, result.stdout + result.stderr)

    def test_own_app_complete_inputs_preview_default_manifest_without_writes(self):
        key = self.owned / 'own-app.pem'
        key.write_text('FAKE-PEM-CONTENT-NEVER-LOG')
        key.chmod(0o600)
        before = snapshot(self.owned)
        result, _ = self.run_guided(apply=False, overrides={'Authentication': 'OwnApp',
            'BrokerOrigin': False, 'AppId': '123456', 'KeyPath': str(key)})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('OwnApp', result.stdout)
        self.assert_read_only(before)
        self.assertIn('Read-only preview.', result.stdout)
        self.assert_preview_build_identity(result, ROOT / 'image/default.json')

    def test_own_app_custom_digest_and_derived_build_arguments_remain_usable_behind_apply(self):
        key = self.owned / 'own-app.pem'
        key.write_text('FAKE-PEM-CONTENT-NEVER-LOG')
        key.chmod(0o600)
        manifest = self.owned / 'custom-image.json'
        value = json.loads((ROOT / 'image/default.json').read_text())
        value['baseImage'] = {'reference': IMAGE}
        manifest.write_text(json.dumps(value))
        derived = self.owned / 'Derived.Dockerfile'
        derived.write_text('ARG REPOAGENT_BASE\nFROM ${REPOAGENT_BASE}\nARG FEATURE\nRUN echo "$FEATURE"\n')
        overrides = {'Authentication': 'OwnApp', 'BrokerOrigin': False, 'AppId': '123456',
            'KeyPath': str(key), 'ImageManifest': str(manifest), 'DerivedDockerfile': str(derived),
            'BuildArg': 'FEATURE=nonsecret-feature', 'SkipBuild': False, 'NoStart': None}
        before = snapshot(self.owned)
        result, _ = self.run_guided(apply=False, overrides=overrides)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(IMAGE, result.stdout)
        self.assertNotIn('nonsecret-feature', result.stdout)
        self.assert_read_only(before)
        self.assertIn('Read-only preview.', result.stdout)
        self.assertNotIn('nonsecret-feature', result.stdout + result.stderr)
        with self.subTest(selection='explicit'):
            self.assert_preview_build_identity(result, manifest, derived)
        result, state = self.run_guided(overrides=overrides)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(state['running'])
        self.assertEqual(state['settings']['appId'], '123456')
        self.assertEqual(state['settings']['keyPath'], str(key))
        self.assertEqual(len(state['builds']), 2)
        self.assertEqual(state['builds'][0]['inventory']['baseImages']['runtime'], IMAGE)
        self.assertIn('FEATURE=nonsecret-feature', state['builds'][1]['arguments'])
        self.assertNotIn('nonsecret-feature', result.stdout + result.stderr)
        launch = self.journey.read('launch.json')
        self.assertEqual(launch['authentication'], 'OwnApp')
        self.assertEqual(launch['runtimeImageId'], IMAGE)
        self.assertEqual(launch['derivedDockerfile'], str(derived))
        self.assertEqual(launch['imageManifestSha256'], hashlib.sha256(manifest.read_bytes()).hexdigest())
        self.assertEqual(launch['derivedDockerfileSha256'], hashlib.sha256(derived.read_bytes()).hexdigest())
        self.assertNotIn('nonsecret-feature', json.dumps(launch))
        self.assertNotIn('FAKE-PEM-CONTENT-NEVER-LOG', json.dumps(launch) + result.stdout + result.stderr)
        self.assertEqual(self.journey.events, [])
        result, state = self.run_guided(saved=True, overrides={'NoStart': None})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(state['builds']), 2, 'Saved immutable image should avoid rebuilding')

        before = snapshot(self.owned)
        event_count = len(state['events'])
        result, state = self.run_guided(saved=True, apply=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('Read-only preview.', result.stdout)
        self.assertEqual(snapshot(self.owned), before)
        self.assertEqual(mutating_docker_commands(state['events'][event_count:]), [])
        self.assertEqual(self.journey.events, [])
        self.assertFalse(self.journey.log.exists())
        self.assertNotIn('nonsecret-feature', result.stdout + result.stderr)
        with self.subTest(selection='saved'):
            self.assert_preview_build_identity(result, manifest, derived)

    def test_own_app_clone_choice_survives_saved_resume(self):
        key = self.owned / 'own-app.pem'
        key.write_text('FAKE-PEM-CONTENT-NEVER-LOG')
        key.chmod(0o600)
        options = {'Authentication': 'OwnApp', 'BrokerOrigin': False, 'AppId': '123456',
                   'KeyPath': str(key), 'NoStart': None, 'Clone': None}
        before = snapshot(self.owned)
        result, _ = self.run_guided(apply=False, overrides=options)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('Contents read', result.stdout)
        self.assert_read_only(before)
        result, state = self.run_guided(overrides=options)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(state['settings']['clone'])
        self.assertEqual(self.journey.read('launch.json')['clone'], 'true')
        result, state = self.run_guided(saved=True, overrides={'NoStart': None})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(state['settings']['clone'])
        self.assertEqual(self.journey.read('launch.json')['clone'], 'true')
        self.assertEqual(self.journey.events, [])
        self.assertFalse(self.journey.log.exists())

    def test_own_app_saved_ordinary_and_legacy_choices_cannot_upgrade_to_clone(self):
        key = self.owned / 'own-app.pem'
        key.write_text('FAKE-PEM-CONTENT-NEVER-LOG')
        key.chmod(0o600)
        result, state = self.run_guided(overrides={'Authentication': 'OwnApp',
            'BrokerOrigin': False, 'AppId': '123456', 'KeyPath': str(key), 'NoStart': None})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(state['settings']['clone'])
        self.assertEqual(self.journey.read('launch.json')['clone'], 'false')
        for legacy in (False, True):
            with self.subTest(legacy=legacy):
                if legacy:
                    # Model the previously shipped schema1 record by removing
                    # only the newly optional nonsecret launch coordinate.
                    launch = self.journey.read('launch.json')
                    del launch['clone']
                    (self.journey.state / 'launch.json').write_text(json.dumps(launch))
                before = snapshot(self.owned)
                event_count = len(state['events'])
                result, state = self.run_guided(saved=True, apply=False)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn('Read-only preview.', result.stdout)
                self.assertNotIn('OwnApp clone mode:', result.stdout)
                self.assertEqual(snapshot(self.owned), before)
                result, state = self.run_guided(saved=True, overrides={'Clone': None})
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('fresh instance', result.stdout + result.stderr)
                self.assertEqual(snapshot(self.owned), before)
                self.assertEqual(mutating_docker_commands(state['events'][event_count:]), [])
                self.assertEqual(self.journey.events, [])
                self.assertFalse(self.journey.log.exists())

    def test_only_enrollment_bytes_and_selected_auth_cross_runtime_boundary(self):
        history = self.owned / 'history-sentinel.jsonl'
        history.write_text('HOST-HISTORY-NEVER-COPY')
        config = self.owned / 'config.toml'
        config.write_text('HOST-CONFIG-NEVER-COPY')
        before = snapshot(self.owned)
        result, state = self.run_guided(overrides={'NoStart': None})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        launch = json.dumps(self.journey.read('launch.json'))
        receipt_bytes = (self.journey.state / 'enrollment.json').read_bytes()
        receipt = self.journey.read('enrollment.json')
        recovery = self.journey.read('recovery.json')
        commands = json.dumps(state['events'])
        for secret in ('GUIDED-FAKE-CODEX-CREDENTIAL', self.environment['GH_TOKEN'], receipt['deviceSecret'],
                       recovery.get('secret'), 'HOST-HISTORY-NEVER-COPY', 'HOST-CONFIG-NEVER-COPY'):
            if secret:
                self.assertNotIn(secret, commands + launch + result.stdout + result.stderr)
        self.assertEqual(state['imports'], [{'size': len(receipt_bytes),
            'sha256': hashlib.sha256(receipt_bytes).hexdigest()}])
        self.assertEqual(before['history-sentinel.jsonl'], snapshot(self.owned)['history-sentinel.jsonl'])
        self.assertEqual(before['config.toml'], snapshot(self.owned)['config.toml'])
        self.assertEqual(before['auth.json'], snapshot(self.owned)['auth.json'])
        bind_mounts = [event[index + 1] for event in state['events'] if not readonly_lifecycle_inspection(event) for index, argument in enumerate(event[:-1])
                       if argument == '--mount' and 'type=bind' in event[index + 1]]
        self.assertEqual(bind_mounts, [f'type=bind,src={self.auth},dst=/run/secrets/codex-auth.json,readonly'])
        self.assertNotIn('--env', commands)
        for filename in ('approval.json', 'recovery.json', 'launch.json', 'history-sentinel.jsonl', 'config.toml'):
            self.assertFalse(any(filename in mount for mount in bind_mounts))
        self.journey.private()


if __name__ == '__main__':
    unittest.main()
