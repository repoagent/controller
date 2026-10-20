"""Public CLI orchestration with executable provider doubles, never live credentials.

The doubles capture final argv and supply provider-shaped raw JSON. The fixed
Azure query is evaluated against raw fixture resources with the installed JMESPath
library (or Azure CLI's bundled copy); no cloud readiness is established.
"""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name('deploy-auth.py')
REPOSITORY = 'ghcr.io/repoagent/controller-auth'
IMAGE = REPOSITORY + '@sha256:' + 'a' * 64
SUBSCRIPTION = '33333333-3333-3333-3333-333333333333'
TARGET = ['--subscription', SUBSCRIPTION, '--resource-group', 'auth-rg', '--name', 'auth-service']
DOMAIN = 'gentle-field-123.swedencentral.azurecontainerapps.io'
ORIGIN = 'https://auth-service.' + DOMAIN
ENVIRONMENT_ID = f'/subscriptions/{SUBSCRIPTION}/resourceGroups/auth-rg/providers/Microsoft.App/managedEnvironments/auth-service-environment'
ENV_SHOW = ['az', 'resource', 'show', '--ids', ENVIRONMENT_ID, '--api-version', '2025-01-01', '--subscription', SUBSCRIPTION, '-o', 'json']
# Provider request contract, intentionally independent of production helpers.
APP_QUERY = ('{id:id,role:tags."repoagent-role",mode:properties.configuration.activeRevisionsMode,'
             'containers:properties.template.containers[].{name:name,image:image},'
             'environment:properties.environmentId,location:location,ingress:properties.configuration.ingress,'
             'origins:properties.template.containers[].env[?name==`Broker__BrokerOrigin`].value[],'
             'latest:properties.latestRevisionName,ready:properties.latestReadyRevisionName}')
INSPECT = ['skopeo', 'inspect', '--no-creds', '--config', '--override-os', 'linux',
           '--override-arch', 'amd64', 'docker://' + IMAGE]
SHOW = ['az', 'containerapp', 'show', *TARGET, '--query', APP_QUERY, '-o', 'json']
UPDATE = ['az', 'containerapp', 'update', *TARGET, '--container-name', 'auth',
          '--image', IMAGE, '--output', 'none']

# A transport transcript, not an Azure emulator: no project validation decisions
# are implemented here. The real JMESPath library evaluates query projections.
# Unexpected provider calls fail closed.
PROVIDER_DOUBLE = '''import json, os, pathlib, sys
command = [pathlib.Path(sys.argv[0]).name, *sys.argv[1:]]
ledger = pathlib.Path(os.environ['TEST_LEDGER'])
previous = ledger.read_text().splitlines() if ledger.exists() else []
with ledger.open('a') as stream:
    stream.write(json.dumps(command) + '\\n')
transcript = json.loads(pathlib.Path(os.environ['TEST_TRANSCRIPT']).read_text())
index = len(previous)
if index >= len(transcript):
    sys.exit('Unexpected provider command: ' + json.dumps(command))
step = transcript[index]
responses = [step, *step.get('alternatives', [])]
match = next((entry for entry in responses if command == entry['argv']), None)
if match is None:
    sys.exit('Unexpected provider command: ' + json.dumps(command))
response = match['response']
if '--query' in command:
    sys.path.insert(0, os.environ['TEST_JMESPATH_DIRECTORY'])
    import jmespath
    response = jmespath.search(command[command.index('--query') + 1], response)
print(json.dumps(response))
'''


def existing_app():
    """Azure show response after the requested projection, with one existing app."""
    return dict(id=f'/subscriptions/{SUBSCRIPTION}/resourceGroups/auth-rg/providers/Microsoft.App/containerApps/auth-service',
                role='auth-service', mode='Single', location='swedencentral', environment=ENVIRONMENT_ID,
                ingress=dict(external=True, fqdn=ORIGIN.removeprefix('https://')), origins=[ORIGIN], containers=[dict(name='auth', image='ghcr.io/example/auth:previous')],
                latest='auth-service--previous', ready='auth-service--previous')


def auth_image():
    return dict(config=dict(Labels={'io.repoagent.role': 'auth-service'}, User='10001:10001'),
                os='linux', architecture='amd64')


def jmespath_directory():
    try:
        import jmespath
        return Path(jmespath.__file__).resolve().parents[1]
    except ModuleNotFoundError:
        candidates = list(Path('/opt/az/lib').glob('python*/site-packages/jmespath/__init__.py'))
        if not candidates:
            raise RuntimeError('Install jmespath or Azure CLI to evaluate the real application query')
        return candidates[0].parents[1]


def raw_app(projected):
    return dict(id=projected['id'], tags={'repoagent-role': projected['role']}, location=projected['location'],
                properties=dict(environmentId=projected['environment'],
                    configuration=dict(activeRevisionsMode=projected['mode'], ingress=projected['ingress']),
                    template=dict(containers=[dict(**container, env=[dict(name='Broker__BrokerOrigin', value=origin)
                        for origin in projected['origins']]) for container in projected['containers']]),
                    latestRevisionName=projected['latest'], latestReadyRevisionName=projected['ready']))


def owned_environment():
    return dict(id=ENVIRONMENT_ID, type='Microsoft.App/managedEnvironments', location='swedencentral',
                tags=dict(repositoryId='1426154477', ownerId='345029828', managedBy='repoagent-auth', role='auth-environment'),
                properties=dict(defaultDomain=DOMAIN))


class DeploymentCliTests(unittest.TestCase):
    def invoke(self, verb, transcript, environment=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ['az', 'skopeo']:
                executable = root / name
                executable.write_text(f'#!{sys.executable}\n' + PROVIDER_DOUBLE)
                executable.chmod(0o700)
            fixture = root / 'transcript.json'
            steps = []
            for argv, response in transcript:
                entry = dict(argv=argv, response=response)
                if argv[0] == 'skopeo':
                    # Both documented provider response shapes keep old code
                    # runnable: rejection regressions must fail on an accepted
                    # bad image, not an unexpected-command fixture error.
                    config = response.get('config', {})
                    entry['alternatives'] = [dict(
                        argv=['skopeo', 'inspect', '--override-os', 'linux', '--override-arch', 'amd64', argv[-1]],
                        response=dict(Labels=config.get('Labels', {}), Os=response.get('os'),
                                      Architecture=response.get('architecture')))]
                if argv == SHOW:
                    entry['response'] = raw_app(response)
                steps.append(entry)
            fixture.write_text(json.dumps(steps))
            ledger = root / 'calls.jsonl'
            env = dict(PATH=str(root), HOME=str(root), LANG='C.UTF-8',
                       TEST_TRANSCRIPT=str(fixture), TEST_LEDGER=str(ledger),
                       TEST_JMESPATH_DIRECTORY=str(jmespath_directory()),
                       AZURE_CLIENT_ID='11111111-1111-1111-1111-111111111111',
                       AZURE_TENANT_ID='22222222-2222-2222-2222-222222222222',
                       AZURE_SUBSCRIPTION_ID=SUBSCRIPTION,
                       AUTH_RESOURCE_GROUP='auth-rg', AUTH_APP_NAME='auth-service',
                       AUTH_IMAGE_REPOSITORY=REPOSITORY, AUTH_IMAGE=IMAGE,
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
                    result, calls = self.invoke(verb, [(INSPECT, dict(auth_image(), config=dict(Labels=labels, User='10001')))])

                    self.assertEqual(result.returncode, 1)
                    self.assertEqual(result.stderr.strip(), 'Image must declare io.repoagent.role=auth-service')
                    self.assertEqual(calls, [INSPECT])

    def test_preflight_inspects_the_selected_immutable_auth_image(self):
        result, calls = self.invoke('preflight', [(INSPECT, auth_image())])

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [INSPECT])

    def test_manual_commands_reject_root_or_missing_user_before_azure(self):
        ready = dict(existing_app(), containers=[dict(name='auth', image=IMAGE)],
                     latest='auth-service--new', ready='auth-service--new')
        for user in [None, '', '0', '0:10001', 'root']:
            config = dict(Labels={'io.repoagent.role': 'auth-service'})
            if user is not None:
                config['User'] = user
            for verb in ['preflight', 'deploy']:
                with self.subTest(user=user, verb=verb):
                    # A valid Azure path is available to expose accidental update,
                    # while the intended command must stop at image inspection.
                    result, calls = self.invoke(verb, [(INSPECT, dict(auth_image(), config=config)),
                        (SHOW, existing_app()), (ENV_SHOW, owned_environment()), (UPDATE, {}), (SHOW, ready)])

                    self.assertEqual(result.returncode, 1)
                    self.assertIn('explicit nonzero numeric USER', result.stderr)
                    self.assertEqual(calls, [INSPECT])

    def test_manual_commands_reject_a_wrong_configured_repository_before_providers(self):
        wrong_repository = 'ghcr.io/repoagent/controller'
        wrong_image = wrong_repository + '@sha256:' + 'a' * 64
        inspect = [*INSPECT[:-1], 'docker://' + wrong_image]
        update = [wrong_image if value == IMAGE else value for value in UPDATE]
        ready = dict(existing_app(), containers=[dict(name='auth', image=wrong_image)],
                     latest='auth-service--new', ready='auth-service--new')
        for verb in ['preflight', 'deploy']:
            with self.subTest(verb=verb):
                result, calls = self.invoke(verb, [(inspect, auth_image()), (SHOW, existing_app()), (ENV_SHOW, owned_environment()),
                    (update, {}), (SHOW, ready)],
                    {'AUTH_IMAGE_REPOSITORY': wrong_repository, 'AUTH_IMAGE': wrong_image})

                self.assertEqual(result.returncode, 1)
                self.assertIn('must be the dedicated', result.stderr)
                self.assertEqual(calls, [])

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

    def test_unowned_or_malformed_environment_refuses_before_update(self):
        for change in (dict(id=ENVIRONMENT_ID + '-foreign'), dict(type='Microsoft.App/containerApps'),
                       dict(tags={}), dict(location='other-region'), dict(properties=dict(defaultDomain='https://bad.test'))):
            with self.subTest(change=change):
                result, calls = self.invoke('deploy', [(INSPECT, auth_image()), (SHOW, existing_app()),
                    (ENV_SHOW, dict(owned_environment(), **change))])
                self.assertEqual(result.returncode, 1)
                self.assertNotIn(UPDATE, calls)
                self.assertNotIn('Unexpected provider command', result.stderr)

    def test_public_origin_mismatch_refuses_before_update_and_after_rollout(self):
        for change in (dict(ingress=dict(external=True, fqdn='foreign.example.test')),
                       dict(environment=ENVIRONMENT_ID + '-foreign'), dict(origins=['https://foreign.example.test'])):
            with self.subTest(change=change):
                result, calls = self.invoke('deploy', [(INSPECT, auth_image()), (SHOW, dict(existing_app(), **change)), (ENV_SHOW, owned_environment())])
                self.assertEqual(result.returncode, 1)
                self.assertIn('Public ingress or configured broker origin', result.stderr)
                self.assertNotIn(UPDATE, calls)
        after = dict(existing_app(), containers=[dict(name='auth', image=IMAGE)],
                     latest='new', ready='new', ingress=dict(external=True, fqdn='foreign.example.test'))
        result, calls = self.invoke('deploy', [(INSPECT, auth_image()), (SHOW, existing_app()), (ENV_SHOW, owned_environment()), (UPDATE, {}), (SHOW, after)])
        self.assertEqual(result.returncode, 1)
        self.assertIn('Public ingress or configured broker origin', result.stderr)
        self.assertIn(UPDATE, calls)

    def test_deployment_updates_only_the_selected_container_and_checks_readiness(self):
        ready = dict(existing_app(), containers=[dict(name='auth', image=IMAGE)],
                     latest='auth-service--new', ready='auth-service--new')
        result, calls = self.invoke('deploy', [(INSPECT, auth_image()), (SHOW, existing_app()), (ENV_SHOW, owned_environment()),
                                              (UPDATE, {}), (SHOW, ready)])

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), 'Broker origin: ' + ORIGIN + '\nIntended immutable image is configured and latest revision is ready.')
        self.assertEqual(calls, [INSPECT, SHOW, ENV_SHOW, UPDATE, SHOW])

    def test_not_ready_revision_is_rechecked_before_success(self):
        pending = dict(existing_app(), containers=[dict(name='auth', image=IMAGE)],
                       latest='auth-service--new')
        ready = dict(pending, ready='auth-service--new')
        result, calls = self.invoke('deploy', [(INSPECT, auth_image()), (SHOW, existing_app()), (ENV_SHOW, owned_environment()),
                                              (UPDATE, {}), (SHOW, pending), (SHOW, ready)])

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), 'Broker origin: ' + ORIGIN + '\nIntended immutable image is configured and latest revision is ready.')
        self.assertEqual(calls, [INSPECT, SHOW, ENV_SHOW, UPDATE, SHOW, SHOW])

    def test_resulting_image_mismatch_is_reported_after_update(self):
        # An unrelated/latest image is not a successful deployment, even if ready.
        result, calls = self.invoke('deploy', [(INSPECT, auth_image()), (SHOW, existing_app()), (ENV_SHOW, owned_environment()),
                                              (UPDATE, {}), (SHOW, existing_app())])

        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stderr.strip(), 'Resulting app image differs from requested immutable image')
        self.assertEqual(calls, [INSPECT, SHOW, ENV_SHOW, UPDATE, SHOW])


if __name__ == '__main__':
    unittest.main()
