"""Shipping source closure and genuine native config observations, without model turns."""
import json
import os
from pathlib import Path
import select
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]


def native_config(root, home):
    """Observe the actual native layer; callers must acquire trust through production."""
    executable = os.environ.get('CHECKOUT_NATIVE', shutil.which('codex'))
    environment = dict(PATH=os.environ['PATH'], CODEX_HOME=str(home), LANG='C.UTF-8')
    with tempfile.TemporaryFile() as errors:
        process = subprocess.Popen([executable, 'app-server'], cwd=root, env=environment,
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
        buffer = b''

        def request(identifier, method, params):
            nonlocal buffer
            process.stdin.write((json.dumps(dict(id=identifier, method=method, params=params)) + '\n').encode())
            process.stdin.flush()
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                if b'\n' not in buffer:
                    if not select.select([process.stdout], [], [], max(0, deadline - time.monotonic()))[0]:
                        break
                    part = os.read(process.stdout.fileno(), 65536)
                    if not part:
                        raise RuntimeError('Native closed before response')
                    buffer += part
                while b'\n' in buffer:
                    line, buffer = buffer.split(b'\n', 1)
                    response = json.loads(line)
                    if response.get('id') == identifier:
                        if 'error' in response:
                            raise RuntimeError(response)
                        return response['result']
            raise TimeoutError(method)

        try:
            initialized = request(1, 'initialize', dict(clientInfo=dict(name='checkout_contract', version='1'),
                                                       capabilities=dict(experimentalApi=True)))
            assert initialized['codexHome'] == str(home)
            process.stdin.write(b'{"method":"initialized"}\n')
            process.stdin.flush()
            return request(2, 'config/read', dict(cwd=str(root), includeLayers=True))
        finally:
            process.stdin.close()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=3)
            process.stdout.close()


def assert_trusted_project(test, observation, root):
    """Retained green oracle for the later production bootstrap journey."""
    layers = [layer for layer in observation['layers'] if layer['name']['type'] == 'project'
              and Path(layer['name']['dotCodexFolder']) == root / '.codex']
    test.assertEqual(len(layers), 1, observation)
    test.assertFalse(layers[0].get('disabledReason'), layers[0])
    test.assertEqual(observation['config']['model_reasoning_effort'], 'low', observation)


class CheckoutShippingInputs(unittest.TestCase):
    def test_default_image_contains_all_evaluated_controller_compile_inputs(self):
        result = subprocess.run(['dotnet', 'msbuild', str(ROOT / 'src/RepoAgentController'), '-getItem:Compile'],
                                text=True, capture_output=True, check=True, timeout=60)
        items = json.loads(result.stdout)['Items']['Compile']
        expected = {str(Path(item['FullPath']).relative_to(ROOT)) for item in items}
        inventory = {line.strip() for line in (ROOT / 'image/source-files.txt').read_text().splitlines()
                     if line.strip() and not line.startswith('#')}
        self.assertFalse(expected - inventory, 'Shipping context omits compile inputs: ' + repr(sorted(expected - inventory)))

    def test_actual_default_composed_context_builds_without_injected_sources(self):
        with tempfile.TemporaryDirectory(prefix='checkout shipping ') as directory:
            context = Path(directory) / 'context'
            composition = subprocess.run([sys.executable, str(ROOT / 'scripts/compose-image.py'), '--source', str(ROOT),
                                         '--manifest', str(ROOT / 'image/default.json'), '--output', str(context)],
                                        capture_output=True, text=True, timeout=60)
            self.assertEqual(composition.returncode, 0, composition.stdout + composition.stderr)
            config = Path(directory) / 'nuget.config'
            config.write_text('<configuration><packageSources><clear/><add key="public" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
            environment = dict(os.environ, DOTNET_CLI_HOME=str(Path(directory) / 'cli-home'),
                               DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
            result = subprocess.run(['dotnet', 'build', str(context / 'src/RepoAgentController'), '-c', 'Release',
                                     '--nologo', '--configfile', str(config), '-p:TreatWarningsAsErrors=true', '-p:NuGetAudit=false'],
                                    env=environment, capture_output=True, text=True, timeout=120)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
