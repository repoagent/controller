"""Run both real installers against the Docker executable boundary.

These tests cover our orchestration. They do not claim Windows Docker Desktop,
real GitHub installation, or Codex OAuth compatibility.
"""
import argparse
from contextlib import nullcontext
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import call, patch

ROOT = Path(__file__).resolve().parents[2]


def load_runtime():
    spec = importlib.util.spec_from_file_location('runtime', ROOT / 'container/runtime.py')
    runtime = importlib.util.module_from_spec(spec)
    # Sibling modules are real collaborators, not substitutes for configuration.
    sys.path.insert(0, str(ROOT / 'container'))
    try:
        spec.loader.exec_module(runtime)
    finally:
        sys.path.pop(0)
    if os.name == 'nt':  # Host fixture only; Linux ownership is covered in the shipping container.
        runtime.prepare_codex_home = lambda: runtime.CODEX.mkdir(parents=True, exist_ok=True)
    return runtime


class InstallerTests(unittest.TestCase):
    def run_installer(self, shell, *, arguments=(), interactive=False, auth=True, state=None, extra_env=None,
                      prompt_for_repository=False):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='repoagent installer ')))
        docker = directory / 'docker'
        shutil.copyfile(Path(__file__).with_name('docker_fixture.py'), docker)
        docker.chmod(0o755)
        key = directory / 'private key.pem'
        key.write_text('PRIVATE-KEY-CONTENT-MUST-NOT-APPEAR')
        codex = directory / 'codex home'
        codex.mkdir()
        if auth:
            (codex / 'auth.json').write_text('{"token":"HOST-TOKEN-MUST-NOT-APPEAR"}')
        state_path = directory / 'docker-state.json'
        if state:
            state_path.write_text(json.dumps(state))
        environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'],
                           CODEX_HOME=str(codex), REPOAGENT_INSTANCE='default',
                           # These legacy direct-mode journeys assume Docker's unlabeled-owner query result.
                           INSTALLER_DOCKER_STATE=str(state_path), INSTALLER_DOCKER_OWNER_OUTPUT='direct')
        environment.update(extra_env or {})
        if shell == 'bash':
            command = ['bash', str(ROOT / 'install.sh'), '--app-id', '123456', '--key-path', str(key), '--instance', 'sample']
            if not prompt_for_repository:
                command += ['--repository', 'example/project']
            if not interactive:
                command.append('--non-interactive')
        else:
            command = ['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'install.ps1'),
                       '-AppId', '123456', '-KeyPath', str(key), '-Instance', 'sample',
                       '-StateDirectory', str(directory / 'private-state'), '-Apply']
            if not prompt_for_repository:
                command += ['-Repository', 'example/project']
            if not interactive:
                command.append('-NonInteractive')
        command.extend(arguments)
        answers = ''
        if interactive:
            answers = ('\n' if shell == 'bash' else '') + ('example/project\n' if prompt_for_repository else '') + '\n' * 24
        if shell != 'bash' and state and state.get('settings'):
            # Arrange persisted launch choices through the public installer, not
            # a hand-authored launch schema or a container-only get stub.
            saved_command = list(command)
            if '-NoStart' not in saved_command:
                saved_command.append('-NoStart')
            names = {'mode': 'Mode', 'publicUrl': 'PublicUrl', 'listenPort': 'ListenPort',
                     'reconcileInterval': 'ReconcileInterval', 'model': 'Model',
                     'toolUpdates': 'ToolUpdates', 'webSearch': 'WebSearch', 'mentionUser': 'MentionUser'}
            for field, name in names.items():
                if field in state['settings']:
                    saved_command += ['-' + name, str(state['settings'][field])]
            arranged = subprocess.run(saved_command, input='', text=True, capture_output=True,
                env=environment, cwd=directory, timeout=90)
            self.assertEqual(arranged.returncode, 0, arranged.stdout + arranged.stderr)
        result = subprocess.run(command, input=answers, text=True,
                                capture_output=True, env=environment, cwd=directory, timeout=90)
        observed = json.loads(state_path.read_text()) if state_path.exists() else None
        output = result.stdout + result.stderr
        self.assertNotIn('PRIVATE-KEY-CONTENT-MUST-NOT-APPEAR', output)
        self.assertNotIn('HOST-TOKEN-MUST-NOT-APPEAR', output)
        return result, observed, key

    def shells(self):
        return ['bash'] + (['powershell'] if shutil.which('pwsh') else [])

    def test_arguments_import_existing_sign_in_and_start_isolated_agent(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                result, state, key = self.run_installer(shell)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertTrue(state['running'])
                self.assertEqual(state['settings']['keyPath'], str(key))
                self.assertEqual(state['settings']['mentionUser'], '')
                self.assertTrue(any('--entrypoint' in event and '/usr/bin/python3' in event for event in state['events']))
                self.assertIn('type=volume,src=repoagent-sample-config,dst=/data/config,readonly', state['runtime_mounts'])
                binds = [mount for mount in state['runtime_mounts'] if mount.startswith('type=bind,')]
                self.assertEqual(binds, [f'type=bind,src={key},dst=/run/secrets/github-app.pem,readonly'])

    def test_missing_values_can_be_answered_interactively(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                result, state, _ = self.run_installer(shell, interactive=True, prompt_for_repository=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(state['settings']['repository'], 'example/project')
                self.assertTrue(state['running'])

    def test_skip_build_and_device_login_leave_existing_container_auth_in_place(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                flags = ['--skip-build', '--device-login'] if shell == 'bash' else ['-SkipBuild', '-DeviceLogin']
                initial = {'settings': {}, 'events': [], 'authenticated': True, 'running': False}
                result, state, _ = self.run_installer(shell, arguments=flags, state=initial,
                    interactive=(shell == 'powershell'))
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertFalse(any(event[0] == 'build' for event in state['events']))
                self.assertFalse(any('--entrypoint' in event and '/usr/bin/python3' in event for event in state['events']))
                self.assertTrue(state['running'])

    def test_no_start_verifies_without_running_agent(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                flag = '--no-start' if shell == 'bash' else '-NoStart'
                result, state, _ = self.run_installer(shell, arguments=[flag])
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertFalse(state['running'])
                self.assertTrue(any(event[-1] == 'check' for event in state['events']))

    def test_rerun_preserves_saved_model_and_hosting_choices(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                initial = {'settings': {'repository': 'example/project', 'appId': '123456',
                    'mode': 'fixed-url', 'publicUrl': 'https://agent.example.com', 'listenPort': '9000',
                    'reconcileInterval': '120', 'model': 'operator-model', 'toolUpdates': 'pinned',
                    'webSearch': 'disabled', 'mentionUser': 'custom-user'},
                    'events': [], 'authenticated': True, 'running': False}
                flag = '--no-start' if shell == 'bash' else '-NoStart'
                result, state, _ = self.run_installer(shell, arguments=[flag], state=initial)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                for key, value in initial['settings'].items():
                    self.assertEqual(state['settings'][key], value)

    def test_fresh_install_runs_the_image_tools_and_rerun_keeps_a_saved_update_choice(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                flag = '--no-start' if shell == 'bash' else '-NoStart'
                result, state, _ = self.run_installer(shell, arguments=[flag])
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(state['settings']['toolUpdates'], 'pinned',
                                 'A new controller runs the tool versions its baseline release records')
                initial = {'settings': {'repository': 'example/project', 'appId': '123456', 'toolUpdates': 'latest'},
                           'events': [], 'authenticated': True, 'running': False}
                result, state, _ = self.run_installer(shell, arguments=[flag], state=initial)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(state['settings']['toolUpdates'], 'latest', 'An existing opt-in is not silently changed')

    def test_failed_app_verification_never_starts_agent(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                result, state, _ = self.run_installer(shell, extra_env={'INSTALLER_CHECK_FAILURE': '1'})
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(state['running'])
                self.assertFalse(any(event[-1] == 'start' for event in state['events']))

    def test_unattended_missing_auth_fails_instead_of_starting_device_login(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                result, state, _ = self.run_installer(shell, auth=False)
                self.assertNotEqual(result.returncode, 0)
                expected = 'No valid container Codex sign-in' if shell == 'bash' else 'CodexAuthPath'
                self.assertIn(expected, result.stdout + result.stderr)
                self.assertFalse(any(event[-1:] == ['login'] for event in state['events']))
                self.assertFalse(state['running'])

    def test_running_agent_is_not_reconfigured_or_rebuilt(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                initial = {'settings': {}, 'events': [], 'authenticated': True, 'running': True}
                result, state, _ = self.run_installer(shell, state=initial)
                self.assertNotEqual(result.returncode, 0)
                self.assertTrue(state['running'])
                self.assertFalse(any(event[0] in ('build', 'run') for event in state['events']))

    def test_linux_containers_required_before_any_installation(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                result, state, _ = self.run_installer(shell, extra_env={'INSTALLER_DOCKER_OS': 'windows'})
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('Linux containers', result.stdout + result.stderr)
                self.assertFalse(any(event[0] in ('build', 'run') for event in state['events']))


class ContainerConfigurationTests(unittest.TestCase):
    def test_windows_bind_source_is_persisted_as_host_path(self):
        runtime = load_runtime()
        with tempfile.TemporaryDirectory() as directory:
            runtime.CONFIG = Path(directory) / 'config/settings.json'
            runtime.CODEX = Path(directory) / 'codex'
            args = argparse.Namespace(repository='example/project', app_id='123456',
                key_path=r'C:\Users\Someone\App Keys\key.pem', installation_id=None,
                mention_user='', model='test-model', web_search='live', tool_updates='latest',
                mode='polling', public_url='', listen_port=8080, reconcile_interval=300)
            runtime.configure(args)
            self.assertEqual(json.loads(runtime.CONFIG.read_text())['keyPath'], args.key_path)

    def test_clone_authority_is_explicit_and_cannot_replace_an_existing_instance(self):
        runtime = load_runtime()
        with tempfile.TemporaryDirectory() as directory:
            runtime.CONFIG = Path(directory) / 'config/settings.json'
            runtime.CODEX = Path(directory) / 'codex'
            args = argparse.Namespace(repository='example/project', app_id='123456', key_path='/private/key.pem',
                installation_id=None, mention_user='', model='test-model', web_search='live', tool_updates='latest',
                mode='polling', public_url='', listen_port=8080, reconcile_interval=300, clone=True)
            runtime.configure(args)
            saved = runtime.CONFIG.read_bytes()
            self.assertIn('--clone', runtime.bridge_args(json.loads(saved)))
            args.clone = False
            with self.assertRaisesRegex(ValueError, 'fresh instance and enrollment'):
                runtime.configure(args)
            self.assertEqual(runtime.CONFIG.read_bytes(), saved)

    def test_container_starts_with_image_tools_unless_startup_updates_are_chosen(self):
        runtime = load_runtime()
        configure = ['runtime.py', 'configure', '--repository', 'example/project', '--app-id', '123456',
                     '--key-path', '/private/key.pem']
        for name, choice, updated in [('default', [], False), ('explicit pinned', ['--tool-updates', 'pinned'], False),
                                      ('explicit opt-in', ['--tool-updates', 'latest'], True)]:
            with self.subTest(case=name), tempfile.TemporaryDirectory() as directory:
                runtime.CONFIG = Path(directory) / 'config/settings.json'
                runtime.CODEX = Path(directory) / 'codex'
                with patch.dict(os.environ, PATH='/data/tools/bin:/opt/repoagent/tools/bin:/usr/bin'):
                    with patch.object(sys, 'argv', configure + choice):
                        runtime.main()
                    # The startup download is the external boundary; the controller process is not started.
                    with patch.object(sys, 'argv', ['runtime.py', 'start']), \
                         patch.object(runtime.update_tools, 'update') as download, \
                         patch.object(runtime.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0)), \
                         patch.object(runtime.os, 'execv') as controller:
                        runtime.main()
                    tools = os.environ['PATH'].split(os.pathsep)
                self.assertEqual(download.called, updated)
                self.assertEqual(tools[0] == '/data/tools/bin', updated)
                self.assertEqual('/data/tools/bin' in tools, updated)
                controller.assert_called_once()

    def test_status_and_retained_logs_are_read_by_the_controller_from_the_runtime_state(self):
        runtime = load_runtime()
        with tempfile.TemporaryDirectory() as directory:
            runtime.CONFIG = Path(directory) / 'config/settings.json'
            for command in ('status', 'logs'):
                with patch.object(sys, 'argv', ['runtime.py', command]), \
                     patch.object(runtime.os, 'execv') as controller:
                    runtime.main()
                controller.assert_called_once_with(runtime.CONTROLLER[0], runtime.CONTROLLER + [command, '--state', runtime.STATE])

    def test_optional_skills_cannot_shadow_required_packaged_skills(self):
        runtime = load_runtime()
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            runtime.CONFIG, runtime.SKILLS = directory / 'config/settings.json', directory / 'config/skills'
            (runtime.SKILLS / 'selected-earlier').mkdir(parents=True)
            selection = directory / 'skills.json'
            selection.write_text(json.dumps([dict(repository='owner/skills', revision='a' * 40, path='skills/shadow')]))
            for name in ('github-discussion', 'repoagent-orientation'):
                # Only the GitHub source archive transport is controlled; extraction and validation are real.
                archive, text = io.BytesIO(), f'---\nname: {name}\ndescription: Shadowing fixture.\n---\nShadow.\n'.encode()
                with tarfile.open(fileobj=archive, mode='w:gz') as tar:
                    member = tarfile.TarInfo('owner-skills-0000000/skills/shadow/SKILL.md'); member.size = len(text)
                    tar.addfile(member, io.BytesIO(text))
                source = SimpleNamespace(open=lambda request, timeout: nullcontext(io.BytesIO(archive.getvalue())))
                with self.subTest(name=name), patch.object(runtime.urllib.request, 'build_opener', return_value=source):
                    with self.assertRaisesRegex(ValueError, 'reserved skill name: ' + name):
                        runtime.install_skills(selection)
                    self.assertEqual([path.name for path in runtime.SKILLS.iterdir()], ['selected-earlier'])

    def test_unattended_runtime_login_never_starts_device_auth(self):
        runtime = load_runtime()
        with tempfile.TemporaryDirectory() as directory:
            runtime.CONFIG = Path(directory) / 'not-configured.json'
            with patch.object(sys, 'argv', ['runtime.py', 'login', '--non-interactive']), \
                 patch.object(runtime.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1)) as codex:
                with self.assertRaisesRegex(ValueError, 'No valid container Codex sign-in'):
                    runtime.main()
                self.assertEqual(codex.call_args_list, [call(['codex', 'login', 'status'])])


if __name__ == '__main__':
    unittest.main()
