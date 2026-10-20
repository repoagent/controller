"""Behavior at the public image-build commands and their Docker process boundary."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
DEFAULT = json.loads((ROOT / 'image/default.json').read_text())


class ImageCompositionTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='image composition ')))
        docker = self.directory / 'docker'
        shutil.copyfile(Path(__file__).with_name('docker_fixture.py'), docker)
        docker.chmod(0o755)
        self.state_path = self.directory / 'docker-state.json'
        self.environment = dict(os.environ, PATH=str(self.directory) + os.pathsep + os.environ['PATH'],
                                INSTALLER_DOCKER_STATE=str(self.state_path))

    def invoke(self, shell, *arguments):
        command = (["bash", str(ROOT / 'repoagent'), 'build'] if shell == 'bash' else
                   ['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'), 'build'])
        result = subprocess.run(command + list(arguments), cwd=self.directory, env=self.environment,
                                text=True, capture_output=True, timeout=40)
        state = json.loads(self.state_path.read_text()) if self.state_path.exists() else {'events': []}
        return result, state

    def shells(self):
        self.assertIsNotNone(shutil.which('pwsh'), 'Linux pwsh is required for host parity')
        return ['bash', 'powershell']

    def manifest(self, **changes):
        value = json.loads(json.dumps(DEFAULT))
        for key, replacement in changes.items():
            if isinstance(replacement, dict) and key in value:
                value[key].update(replacement)
            else:
                value[key] = replacement
        path = self.directory / 'operator manifest.json'
        path.write_text(json.dumps(value))
        return path

    def test_invalid_manifests_stop_before_build_or_secret_access(self):
        cases = {
            'unknown profile': {'agent': {'profile': 'other'}},
            'malformed version': {'agent': {'version': 'latest'}},
            'unsupported architecture': {'platform': 'linux/arm64'},
            'unknown field': {'credential': 'CANARY-SECRET'},
            'unknown tool': {'tools': {'curl': '1.0'}},
        }
        for shell in self.shells():
            for name, changes in cases.items():
                with self.subTest(shell=shell, case=name):
                    self.state_path.unlink(missing_ok=True)
                    result, state = self.invoke(shell, '--manifest', str(self.manifest(**changes)))
                    self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertFalse(any(event[0] == 'build' for event in state['events']))
                    self.assertFalse(any('/run/secrets/' in ' '.join(event) for event in state['events']))
                    self.assertNotIn('CANARY-SECRET', result.stdout + result.stderr)

    def test_both_hosts_build_same_allowlisted_context_and_inventory(self):
        observed = []
        for shell in self.shells():
            with self.subTest(shell=shell):
                self.state_path.unlink(missing_ok=True)
                result, state = self.invoke(shell, '--manifest', str(self.manifest()))
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(len(state['builds']), 1)
                build = state['builds'][0]
                files = build['files']
                self.assertIn('image-inventory.json', files)
                self.assertIn('container/Containerfile', files)
                self.assertFalse(any(name.endswith(('.pem', '.key', 'auth.json')) for name in files))
                self.assertFalse(any(name.startswith(('.aws/', '.codex/', '.git/', 'home/')) for name in files))
                self.assertEqual(build['inventory']['platform'], 'linux/amd64')
                self.assertEqual(build['inventory']['agent'], DEFAULT['agent'])
                self.assertTrue(build['inventory']['artifacts']['codex']['sha256'])
                observed.append((files, build['inventory']))
        self.assertEqual(observed[0], observed[1])

    def test_derived_path_and_build_arguments_are_passed_literally(self):
        derived = self.directory / 'my derived ;$(touch INJECTED).Dockerfile'
        derived.write_text('ARG REPOAGENT_BASE\nFROM ${REPOAGENT_BASE}\n')
        marker = self.directory / 'INJECTED'
        value = 'MESSAGE=spaces ; $(touch INJECTED) & \'quotes\''
        for shell in self.shells():
            with self.subTest(shell=shell):
                self.state_path.unlink(missing_ok=True)
                result, state = self.invoke(shell, '--manifest', str(self.manifest()),
                                            '--derived-dockerfile', str(derived), '--build-arg', value)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(len(state['builds']), 2)
                self.assertEqual(state['builds'][1]['derived'], derived.read_text())
                args = state['builds'][1]['arguments']
                self.assertEqual(args[args.index('--build-arg', args.index('--build-arg') + 1) + 1], value)
                self.assertFalse(marker.exists())

    def test_digest_pinned_custom_base_and_minimal_tools_shape_build_input(self):
        digest = 'a' * 64
        for reference in ('registry.example.com/team/ubuntu@sha256:' + digest, 'sha256:' + digest):
            for shell in self.shells():
                with self.subTest(reference=reference, shell=shell):
                    self.state_path.unlink(missing_ok=True)
                    manifest = self.manifest(baseImage={'reference': reference})
                    # The minimal selection omits both optional tool groups.
                    value = json.loads(manifest.read_text())
                    value['tools'] = {}
                    manifest.write_text(json.dumps(value))
                    result, state = self.invoke(shell, '--manifest', str(manifest))
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertEqual(len(state['builds']), 1)
                    build = state['builds'][0]
                    self.assertEqual(build['inventory']['baseImages']['runtime'], reference)
                    self.assertEqual(build['inventory']['tools'], {})
                    if reference.startswith('sha256:'):
                        self.assertIn('FROM repoagent-local-base:', build['dockerfile'])
                    else:
                        self.assertIn('FROM ' + reference + ' AS runtime', build['dockerfile'])
                    self.assertIn('prepare-runtime.sh false', build['dockerfile'])
                    self.assertNotIn('RUN bash /tmp/install-cloudflared.sh', build['dockerfile'])

    def test_floating_custom_base_stops_before_build(self):
        for shell in self.shells():
            with self.subTest(shell=shell):
                self.state_path.unlink(missing_ok=True)
                manifest = self.manifest(baseImage={'reference': 'ubuntu:26.04'})
                result, state = self.invoke(shell, '--manifest', str(manifest))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(any(event[0] == 'build' for event in state['events']))

    def test_local_base_identity_mismatch_stops_before_build(self):
        reference = 'sha256:' + 'a' * 64
        for shell in self.shells():
            for override in ('INSTALLER_DOCKER_LOCAL_ID_RESPONSE', 'INSTALLER_DOCKER_TAG_ID_RESPONSE'):
                with self.subTest(shell=shell, mismatch=override):
                    self.state_path.unlink(missing_ok=True)
                    self.environment[override] = 'sha256:' + 'b' * 64
                    try:
                        result, state = self.invoke(shell, '--manifest', str(self.manifest(baseImage={'reference': reference})))
                    finally:
                        self.environment.pop(override)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertFalse(any(event[0] == 'build' for event in state['events']))

    def test_symlinked_source_file_is_rejected(self):
        spec = importlib.util.spec_from_file_location('compose_image', ROOT / 'scripts/compose-image.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        source = self.directory / 'source'
        for relative in module.SOURCE_FILES:
            destination = source / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, destination)
        original = source / module.SOURCE_FILES[0]
        replacement = self.directory / 'outside credential'
        replacement.write_text('CANARY-SECRET')
        original.unlink()
        original.symlink_to(replacement)
        result = subprocess.run([sys.executable, str(ROOT / 'scripts/compose-image.py'),
                                 '--source', str(source), '--manifest', str(self.manifest()),
                                 '--output', str(self.directory / 'output')], text=True, capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.directory / 'output').exists())
        self.assertNotIn('CANARY-SECRET', result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
