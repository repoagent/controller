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


def native_config(root, home, discovery=False, environment=None):
    """Observe the actual native layer; callers must acquire trust through production."""
    executable = (shutil.which('codex', path=environment['PATH']) if environment is not None else
                  os.environ.get('CHECKOUT_NATIVE', os.environ.get('CHECKOUT_REAL_NATIVE', shutil.which('codex'))))
    environment = dict(PATH=os.environ['PATH'], LANG='C.UTF-8') if environment is None else dict(environment)
    environment['CODEX_HOME'] = str(home)
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
            result = request(2, 'config/read', dict(cwd=str(root), includeLayers=True))
            if discovery:
                result['fixtureSkills'] = request(3, 'skills/list', dict(cwds=[str(root)], forceReload=True))
                result['fixtureThread'] = request(4, 'thread/start', dict(cwd=str(root)))
            return result
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
    test.assertFalse(layers[0].get('disabledReason'), dict(project=layers[0], effective=observation['config']['model_reasoning_effort']))
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


@unittest.skipUnless(os.environ.get('CHECKOUT_SHIPPING_IMAGE'), 'Explicit image built from current shipping context required')
class CheckoutShippingContainer(unittest.TestCase):
    def test_shipping_aot_clone_and_restart_use_normal_tls_and_production_trust(self):
        from test_managed_git import GitProvider
        import tomllib
        image = os.environ['CHECKOUT_SHIPPING_IMAGE']
        with tempfile.TemporaryDirectory(prefix='issue64 shipping container ') as directory:
            directory = Path(directory)
            provider = GitProvider(directory, listen_host='0.0.0.0')
            self.addCleanup(provider.close)
            enrollment = directory / 'enrollment.json'
            enrollment.write_text(json.dumps(provider.enrollment())); enrollment.chmod(0o600)
            home, workspace = directory / 'home', directory / 'workspace'
            home.mkdir(mode=0o700); workspace.mkdir()
            config = home / 'config.toml'
            config.write_text('model_reasoning_effort = "medium"\n'); config.chmod(0o600)
            docker = ['docker', '--host=unix:///var/run/docker.sock', 'run', '--rm', '--add-host', 'host.docker.internal:host-gateway',
                      '--mount', f'type=bind,src={directory},dst={directory}', '--entrypoint', '/opt/repoagent/controller/RepoAgentController']
            proxy = 'http://host.docker.internal:' + str(provider.server.server_address[1])
            environment = dict(HTTPS_PROXY=proxy, HTTP_PROXY=proxy, ALL_PROXY='', NO_PROXY='',
                               https_proxy=proxy, http_proxy=proxy, all_proxy='', no_proxy='',
                               SSL_CERT_FILE=str(provider.cert), GIT_SSL_CAINFO=str(provider.cert))
            for name, value in environment.items():
                docker += ['--env', name + '=' + value]
            docker += ['--env', 'CODEX_HOME=' + str(home), image]
            command = docker + ['--repo', 'owner/project', '--app-id', '123', '--broker-url', 'https://broker.fixture',
                                '--enrollment-file', str(enrollment), '--state', str(directory / 'state.json'),
                                '--workspace', str(workspace), '--clone', '--once']
            for attempt in range(2):
                result = subprocess.run(command, capture_output=True, text=True, timeout=60)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr + ' requests=' + repr(provider.requests))
                self.assertIn('checkout-ready', result.stdout)
                self.assertEqual((workspace / 'repository/README').read_text(), 'approved repository\n')
            parsed = tomllib.loads(config.read_text())
            self.assertEqual(parsed['projects'], {str(workspace / 'repository'): {'trust_level': 'trusted'}})
            self.assertTrue(provider.git_tokens)
            self.assertEqual(set(provider.git_tokens), {'lease-A'})
            git_requests = [request for request in provider.requests if request[1] == 'github.com']
            print('SHIPPING image=' + image + ' normal-TLS git requests=' + str(len(git_requests)), flush=True)


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--observe-production':
        import tomllib
        root, home = map(Path, sys.argv[2:])
        observation = native_config(root, home, discovery=True)
        assert_trusted_project(unittest.TestCase(), observation, root)
        config = tomllib.loads((home / 'config.toml').read_text())
        assert config['projects'] == {'/unrelated-fixture': {'trust_level': 'untrusted'}, str(root): {'trust_level': 'trusted'}}
        assert str(root / 'AGENTS.md') in json.dumps(observation['fixtureThread']['instructionSources'])
        assert str(root / '.agents/skills/issue64-context/SKILL.md') in json.dumps(observation['fixtureSkills'])
        assert str(root.parents[1] / 'sibling/.agents/skills/issue64-sibling/SKILL.md') not in json.dumps(observation['fixtureSkills'])
        assert observation['fixtureThread']['cwd'] == str(root)
        assert observation['fixtureThread']['thread']['cwd'] == str(root)
        print(json.dumps(observation))
    else:
        unittest.main()
