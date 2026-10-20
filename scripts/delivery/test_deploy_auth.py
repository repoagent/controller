"""Public CLI orchestration with executable provider doubles, never live credentials.

The doubles capture final argv and supply provider-shaped projected JSON. The fixed
Azure query below checks our adapter's outbound translation; it does not claim
that Azure evaluates that query or that a real revision is ready.
"""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name('deploy-auth.py')
IMAGE = 'ghcr.io/example/auth@sha256:' + 'a' * 64
SUBSCRIPTION = '33333333-3333-3333-3333-333333333333'
TARGET = ['--subscription', SUBSCRIPTION, '--resource-group', 'auth-rg', '--name', 'auth-service']
# Provider request contract, intentionally independent of production helpers.
APP_QUERY = ('{id:id,role:tags."repoagent-role",mode:properties.configuration.activeRevisionsMode,'
             'containers:properties.template.containers[].{name:name,image:image},'
             'latest:properties.latestRevisionName,ready:properties.latestReadyRevisionName}')
INSPECT = ['skopeo', 'inspect', '--override-os', 'linux', '--override-arch', 'amd64', 'docker://' + IMAGE]
SHOW = ['az', 'containerapp', 'show', *TARGET, '--query', APP_QUERY, '-o', 'json']
UPDATE = ['az', 'containerapp', 'update', *TARGET, '--container-name', 'auth',
          '--image', IMAGE, '--output', 'none']

# A transport transcript, not an Azure emulator: no project validation decisions
# or query evaluator are implemented here. Unexpected provider calls fail closed.
PROVIDER_DOUBLE = '''import json, os, pathlib, sys
command = [pathlib.Path(sys.argv[0]).name, *sys.argv[1:]]
ledger = pathlib.Path(os.environ['TEST_LEDGER'])
previous = ledger.read_text().splitlines() if ledger.exists() else []
with ledger.open('a') as stream:
    stream.write(json.dumps(command) + '\\n')
transcript = json.loads(pathlib.Path(os.environ['TEST_TRANSCRIPT']).read_text())
index = len(previous)
if index >= len(transcript) or command != transcript[index]['argv']:
    sys.exit('Unexpected provider command: ' + json.dumps(command))
print(json.dumps(transcript[index]['response']))
'''


def existing_app():
    """Azure show response after the requested projection, with one existing app."""
    return dict(id=f'/subscriptions/{SUBSCRIPTION}/resourceGroups/auth-rg/providers/Microsoft.App/containerApps/auth-service',
                role='auth-service', mode='Single', containers=[dict(name='auth', image='ghcr.io/example/auth:previous')],
                latest='auth-service--previous', ready='auth-service--previous')


def auth_image():
    return dict(Labels={'io.repoagent.role': 'auth-service'}, Os='linux', Architecture='amd64')


class DeploymentCliTests(unittest.TestCase):
    def invoke(self, verb, transcript, environment=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ['az', 'skopeo']:
                executable = root / name
                executable.write_text(f'#!{sys.executable}\n' + PROVIDER_DOUBLE)
                executable.chmod(0o700)
            fixture = root / 'transcript.json'
            fixture.write_text(json.dumps([dict(argv=argv, response=response) for argv, response in transcript]))
            ledger = root / 'calls.jsonl'
            env = dict(PATH=str(root), HOME=str(root), LANG='C.UTF-8',
                       TEST_TRANSCRIPT=str(fixture), TEST_LEDGER=str(ledger),
                       AZURE_CLIENT_ID='11111111-1111-1111-1111-111111111111',
                       AZURE_TENANT_ID='22222222-2222-2222-2222-222222222222',
                       AZURE_SUBSCRIPTION_ID=SUBSCRIPTION,
                       AUTH_RESOURCE_GROUP='auth-rg', AUTH_APP_NAME='auth-service',
                       AUTH_IMAGE_REPOSITORY='ghcr.io/example/auth', AUTH_IMAGE=IMAGE,
                       AUTH_DEPLOYMENT_READY='true')
            env.update(environment or {})
            result = subprocess.run([sys.executable, '-B', str(SCRIPT.resolve()), verb],
                                    cwd=root, env=env, capture_output=True, text=True, timeout=15)
            calls = [json.loads(line) for line in ledger.read_text().splitlines()] if ledger.exists() else []
            return result, calls

    def test_unconfigured_or_unsafe_input_never_reaches_a_provider(self):
        for key, value, error in [
                ('AUTH_DEPLOYMENT_READY', '', 'AUTH_DEPLOYMENT_READY must be true after service and infrastructure acceptance'),
                ('AZURE_SUBSCRIPTION_ID', '', 'AZURE_SUBSCRIPTION_ID is missing or invalid'),
                ('AUTH_IMAGE', 'ghcr.io/example/auth:latest', 'AUTH_IMAGE is missing or invalid'),
                ('AUTH_IMAGE', 'ghcr.io/example/controller@sha256:' + 'a' * 64, 'AUTH_IMAGE is missing or invalid'),
                ('AUTH_APP_NAME', '$(touch /tmp/injected)', 'AUTH_APP_NAME is missing or invalid'),
                ('AUTH_IMAGE', IMAGE + ';echo bad', 'AUTH_IMAGE is missing or invalid')]:
            for verb in ['preflight', 'deploy']:
                with self.subTest(key=key, value=value, verb=verb):
                    result, calls = self.invoke(verb, [], {key: value})

                    self.assertEqual(result.returncode, 1)
                    self.assertEqual(result.stderr.strip(), error)
                    self.assertEqual(calls, [])

    def test_controller_or_unlabelled_image_never_reaches_azure(self):
        for labels in [{}, {'io.repoagent.role': 'controller'}]:
            for verb in ['preflight', 'deploy']:
                with self.subTest(labels=labels, verb=verb):
                    result, calls = self.invoke(verb, [(INSPECT, dict(auth_image(), Labels=labels))])

                    self.assertEqual(result.returncode, 1)
                    self.assertEqual(result.stderr.strip(), 'Image must declare io.repoagent.role=auth-service')
                    self.assertEqual(calls, [INSPECT])

    def test_preflight_inspects_the_selected_immutable_auth_image(self):
        result, calls = self.invoke('preflight', [(INSPECT, auth_image())])

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [INSPECT])

    def test_wrong_target_or_unsupported_app_shape_never_updates(self):
        for change, error in [
                (dict(id='/subscriptions/other/resourceGroups/other/providers/Microsoft.App/containerApps/other'),
                 'Existing app identity or repoagent-role tag does not match auth-service target'),
                (dict(role='controller'), 'Existing app identity or repoagent-role tag does not match auth-service target'),
                (dict(mode='Multiple'), 'Deployment requires an existing single-container app in Single revision mode'),
                (dict(containers=[dict(name='auth', image='old'), dict(name='sidecar', image='old')]),
                 'Deployment requires an existing single-container app in Single revision mode')]:
            with self.subTest(change=change):
                result, calls = self.invoke('deploy', [(INSPECT, auth_image()), (SHOW, dict(existing_app(), **change))])

                self.assertEqual(result.returncode, 1)
                self.assertEqual(result.stderr.strip(), error)
                self.assertEqual(calls, [INSPECT, SHOW])

    def test_deployment_updates_only_the_selected_container_and_checks_readiness(self):
        ready = dict(existing_app(), containers=[dict(name='auth', image=IMAGE)],
                     latest='auth-service--new', ready='auth-service--new')
        result, calls = self.invoke('deploy', [(INSPECT, auth_image()), (SHOW, existing_app()),
                                              (UPDATE, {}), (SHOW, ready)])

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), 'Intended immutable image is configured and latest revision is ready.')
        self.assertEqual(calls, [INSPECT, SHOW, UPDATE, SHOW])

    def test_not_ready_revision_is_rechecked_before_success(self):
        pending = dict(existing_app(), containers=[dict(name='auth', image=IMAGE)],
                       latest='auth-service--new')
        ready = dict(pending, ready='auth-service--new')
        result, calls = self.invoke('deploy', [(INSPECT, auth_image()), (SHOW, existing_app()),
                                              (UPDATE, {}), (SHOW, pending), (SHOW, ready)])

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), 'Intended immutable image is configured and latest revision is ready.')
        self.assertEqual(calls, [INSPECT, SHOW, UPDATE, SHOW, SHOW])

    def test_resulting_image_mismatch_is_reported_after_update(self):
        # An unrelated/latest image is not a successful deployment, even if ready.
        result, calls = self.invoke('deploy', [(INSPECT, auth_image()), (SHOW, existing_app()),
                                              (UPDATE, {}), (SHOW, existing_app())])

        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stderr.strip(), 'Resulting app image differs from requested immutable image')
        self.assertEqual(calls, [INSPECT, SHOW, UPDATE, SHOW])


if __name__ == '__main__':
    unittest.main()
