"""Existing deployment CLI, real Bicep, raw Azure and remote-main boundaries."""
import copy
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import textwrap
import time
import unittest

import test_deploy as deployment_tests
from test_deploy import (AUTH, SCOPE, APP_ID, ENVIRONMENT_ID,
                         CLIENT_ID, ORIGIN, STORAGE, SUBSCRIPTION)

sys.path.insert(0, str(AUTH.parents[1] / 'scripts/delivery'))
from test_deploy_auth import jmespath_directory

IMAGE = 'ghcr.io/repoagent/controller-auth@sha256:' + 'a' * 64
REVISION = 'auth-test--accepted'
CANARY = 'private-provider-body-issue84'
GATES = ('AUTH_PROVIDER_READY', 'AUTH_STORAGE_READY', 'AUTH_CLEANUP_READY',
         'AUTH_RUNTIME_ACCESS_READY', 'AUTH_DEPLOYMENT_READY')
WORKFLOW = AUTH.parents[1] / '.github/workflows/ci-auth-service.yml'
ROUTE = textwrap.dedent(WORKFLOW.read_text().rsplit('        run: |\n', 1)[1])

GIT = '''import json, os, pathlib, subprocess, sys
if sys.argv[1:2] != ['ls-remote']:
    sys.exit(subprocess.run([os.environ['REAL_GIT'], *sys.argv[1:]]).returncode)
path = pathlib.Path(os.environ['GIT_OBSERVATIONS'])
values = json.loads(path.read_text())
value = values.pop(0) if len(values) > 1 else values[0]
path.write_text(json.dumps(values))
print(value + '\\trefs/heads/main')
'''

SKOPEO = '''import json, os
with open(os.environ['IMAGE_JOURNAL'], 'a') as stream:
    stream.write('anonymous-config\\n')
print(json.dumps(dict(config=dict(Labels={'io.repoagent.role':'auth-service',
    'org.opencontainers.image.revision':os.environ.get('IMAGE_SOURCE', os.environ['AUTH_EXPECTED_SOURCE_SHA'])},
    User='10001'), os='linux', architecture='amd64')))
'''


class ActivationProcessTests(unittest.TestCase):
    setUpBase = deployment_tests.DeploymentProcessTests.setUp

    def setUp(self):
        self.setUpBase()
        sha = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=AUTH, capture_output=True, text=True, check=True).stdout.strip()
        self.env.update(AUTH_EXPECTED_SOURCE_SHA=sha, GITHUB_SHA=sha, GITHUB_REF='refs/heads/main',
                        AUTH_IMAGE=IMAGE, AUTH_IMAGE_REPOSITORY='ghcr.io/repoagent/controller-auth',
                        AUTH_RESOURCE_GROUP=self.env['AZURE_RESOURCE_GROUP'],
                        AZURE_CLIENT_ID=CLIENT_ID, AZURE_TENANT_ID=CLIENT_ID,
                        REAL_GIT=shutil.which('git'), GIT_OBSERVATIONS=str(self.directory / 'git.json'),
                        IMAGE_JOURNAL=str(self.directory / 'image.jsonl'),
                        TEST_JMESPATH_DIRECTORY=str(jmespath_directory()))
        self.main = [sha]
        for gate in GATES:
            self.env.pop(gate, None)
        for name, source in (('git', GIT), ('skopeo', SKOPEO)):
            executable = self.directory / name
            executable.write_text(f'#!{sys.executable}\n' + source)
            executable.chmod(0o700)
        for name in ('docker', 'gh'):
            executable = self.directory / name
            executable.write_text('#!/bin/sh\nexit 97\n')
            executable.chmod(0o700)
        self.app = self.staging_app()
        self.scenario['resources'][APP_ID] = self.app
        self.scenario['appId'] = APP_ID
        self.scenario['responses'] = {
            'containerapp revision show': {'body': {'name': REVISION, 'properties': {'template': self.app['properties']['template']}}},
            'containerapp revision deactivate': {'raw': ''},
            'containerapp revision activate': {'raw': ''},
            'containerapp revision list': {'body': [{'name': REVISION, 'properties': {'active': False}}]},
            'containerapp replica list': {'body': []},
            'containerapp exec': {'health': {}},
            'containerapp update': {'body': {}},
        }

    def staging_app(self):
        app = copy.deepcopy(self.scenario['resources'][APP_ID])
        identity = SCOPE + '/providers/Microsoft.ManagedIdentity/userAssignedIdentities/auth-test-runtime'
        app['identity'] = dict(type='UserAssigned', userAssignedIdentities={identity: {}})
        app['tags']['repoagent-role'] = 'auth-service'
        values = {'Broker__BrokerOrigin': ORIGIN, 'AZURE_CLIENT_ID': CLIENT_ID,
                  'Broker__ManagedIdentityClientId': CLIENT_ID,
                  'Auth__SigningKeyUri': self.env['AUTH_SIGNING_KEY_URI'],
                  'Auth__DataProtectionKeyUri': self.env['AUTH_DATA_PROTECTION_KEY_URI'],
                  'Broker__DeploymentEpoch': self.env['AUTH_DEPLOYMENT_EPOCH'],
                  'ASPNETCORE_HTTP_PORTS': '8080', 'Auth__DataProtectionApplicationName': 'repoagent-auth-auth-test',
                  'Auth__DataProtectionBlobUri': f'https://{STORAGE}.blob.core.windows.net/data-protection/keyring.xml',
                  'Broker__TableEndpoint': f'https://{STORAGE}.table.core.windows.net/', 'Broker__TableName': 'brokerstate',
                  'Broker__RequestLimitPerMinute': '120', 'Broker__ConcurrentRequestLimit': '8',
                  'Broker__RequestTimeoutSeconds': '15'}
        app['properties'].update(workloadProfileName='Consumption',
            configuration=dict(activeRevisionsMode='Single', maxInactiveRevisions=3),
            latestRevisionName=REVISION, latestReadyRevisionName=REVISION,
            template=dict(containers=[dict(name='auth-service', image=IMAGE, resources=dict(cpu=0.25, memory='0.5Gi'),
                           env=[dict(name=k, value=v) for k, v in values.items()])],
                          scale=dict(minReplicas=1, maxReplicas=1, rules=[])))
        return app

    def invoke(self, mode):
        Path(self.env['GIT_OBSERVATIONS']).write_text(json.dumps(self.main))
        Path(self.env['AZ_DOUBLE_SCENARIO']).write_text(json.dumps(self.scenario))
        journal = Path(self.env['AZ_DOUBLE_JOURNAL'])
        journal.unlink(missing_ok=True)
        result = subprocess.run(['bash', '-e', '-c', ROUTE], cwd=AUTH.parents[1],
                                env={**self.env, 'AUTH_OPERATION': mode}, stdin=subprocess.DEVNULL,
                                capture_output=True, text=True, timeout=75)
        self.last_result = result
        self.calls = [json.loads(line) for line in journal.read_text().splitlines()] if journal.exists() else []
        self.assertNotIn(CANARY, result.stdout + result.stderr + json.dumps(self.calls))
        self.assertFalse(any('delete' in call['args'] for call in self.calls))
        for call in self.calls:
            args = call['args']
            self.assertEqual(args[args.index('--subscription') + 1], SUBSCRIPTION)
        return result

    def creates(self):
        return [call for call in self.calls if call['args'][:3] == ['deployment', 'group', 'create']]

    def test_foundation_workflow_route_needs_no_image_or_hosted_flags_and_never_creates_app(self):
        self.env = {key: value for key, value in self.env.items() if not key.startswith('AUTH_IMAGE')}
        for mode in ('foundation-plan', 'foundation-apply'):
            with self.subTest(mode=mode):
                result = self.invoke(mode)
                self.assertEqual(result.returncode, 0, result.stderr)
                for call in self.calls:
                    if 'parameters' in call:
                        self.assertFalse(call['parameters']['parameters']['deployApp']['value'])
                self.assertFalse(Path(self.env['IMAGE_JOURNAL']).exists())

    def test_actual_workflow_shell_refuses_unknown_and_injected_operations(self):
        for operation in ('apply', 'deploy', 'stage-apply; touch injected', '$(touch injected)', ''):
            with self.subTest(operation=operation):
                result = self.invoke(operation)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.calls, [])
                self.assertFalse((AUTH.parents[1] / 'injected').exists())

    def test_checkout_or_remote_main_mismatch_prevents_any_azure_calls(self):
        for key, value in (('AUTH_EXPECTED_SOURCE_SHA', 'abc'), ('GITHUB_SHA', 'c' * 40),
                           ('GITHUB_REF', 'refs/heads/foreign')):
            original = self.env[key]
            with self.subTest(key=key):
                self.env[key] = value
                result = self.invoke('foundation-apply')
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.calls, [])
            self.env[key] = original

    def test_foreign_or_malformed_staged_artifact_refuses_before_create(self):
        baseline = copy.deepcopy(self.app)
        for field in ('identity', 'environment', 'image', 'epoch', 'scale', 'key', 'extraContainer'):
            with self.subTest(field=field):
                self.app = copy.deepcopy(baseline)
                props = self.app['properties']
                container = props['template']['containers'][0]
                if field == 'identity':
                    self.app['identity']['type'] = 'SystemAssigned'
                elif field == 'environment':
                    props['environmentId'] += '-foreign'
                elif field == 'image':
                    container['image'] = IMAGE[:-1] + 'b'
                elif field in ('epoch', 'key'):
                    name = 'Broker__DeploymentEpoch' if field == 'epoch' else 'Auth__SigningKeyUri'
                    next(entry for entry in container['env'] if entry['name'] == name)['value'] = 'foreign'
                elif field == 'scale':
                    props['template']['scale']['maxReplicas'] = 2
                else:
                    props['template']['containers'].append(copy.deepcopy(container))
                self.scenario['resources'][APP_ID] = self.app
                result = self.invoke('stage-apply')
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.creates(), [])

    def test_stage_plan_and_first_repeated_apply_without_hosted_flags(self):
        result = self.invoke('stage-plan')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.creates(), [])
        for absent in (True, False):
            with self.subTest(absent=absent):
                self.scenario['resources'].pop(APP_ID, None)
                if not absent:
                    self.scenario['resources'][APP_ID] = self.app
                self.scenario['postCreateResources'] = {APP_ID: self.app}
                result = self.invoke('stage-apply')
                self.assertEqual(result.returncode, 0, result.stderr)
                output = json.loads(result.stdout)
                self.assertTrue(output['isolated'])
                self.assertFalse(output['releaseAcceptance'])
                self.assertEqual((output['source'], output['image'], output['revision']),
                                 (self.env['GITHUB_SHA'], IMAGE, REVISION))
                self.assertEqual(output['runtimeConfiguration']['Broker__BrokerOrigin'], ORIGIN)
                self.assertEqual(len(self.creates()), 1)
                parameters = self.creates()[0]['parameters']['parameters']
                self.assertTrue(parameters['staged']['value'])
                self.assertEqual(parameters['image']['value'], IMAGE)

    def test_stage_apply_requires_fresh_app_readback_after_first_or_repeated_creation(self):
        baseline = copy.deepcopy(self.scenario)
        for first in (False, True):
            for missing in (False, True):
                with self.subTest(first=first, missing=missing):
                    self.scenario = copy.deepcopy(baseline)
                    if first:
                        self.scenario['resources'].pop(APP_ID)
                    self.scenario['postCreateResources'] = {APP_ID: None if missing else self.app}
                    result = self.invoke('stage-apply')
                    self.assertEqual(len(self.creates()), 1)
                    create = next(i for i, call in enumerate(self.calls)
                                  if call['args'][:3] == ['deployment', 'group', 'create'])
                    reads = [(i, call['responseCode']) for i, call in enumerate(self.calls)
                             if call['args'][:2] == ['resource', 'show'] and APP_ID in call['args']]
                    self.assertEqual([code for i, code in reads if i < create], [3, 3] if first else [0, 0])
                    self.assertEqual([code for i, code in reads if i > create], [3 if missing else 0])
                    if missing:
                        self.assertNotEqual(result.returncode, 0, result.stdout)
                        self.assertEqual(result.stdout, '')
                        self.assertIn('unconfirmed', result.stderr)
                        self.assertNotIn('AttributeError', result.stderr)
                        self.assertFalse(any(call['args'][:3] == ['containerapp', 'revision', 'show']
                                             for call in self.calls[create + 1:]))
                        self.assertFalse(any(call['args'][:2] == ['containerapp', 'update'] for call in self.calls))
                    else:
                        self.assertEqual(result.returncode, 0, result.stderr)
                        output = json.loads(result.stdout)
                        self.assertTrue(output['applied'])
                        self.assertTrue(output['isolated'])
                        self.assertEqual(output['revision'], REVISION)

    def test_foundation_apply_requires_every_manifest_resource_freshly_present(self):
        baseline = copy.deepcopy(self.scenario)
        resources = [resource_id for resource_id in baseline['resources'] if resource_id != APP_ID]
        identity = SCOPE + '/providers/Microsoft.ManagedIdentity/userAssignedIdentities/auth-test-runtime'
        self.assertIn(identity, resources)
        for missing in (None, identity, *(resource_id for resource_id in resources if resource_id != identity)):
            with self.subTest(missing=missing):
                self.scenario = copy.deepcopy(baseline)
                self.scenario['postCreateResources'] = {} if missing is None else {missing: None}
                result = self.invoke('foundation-apply')
                self.assertEqual(len(self.creates()), 1)
                create = next(i for i, call in enumerate(self.calls)
                              if call['args'][:3] == ['deployment', 'group', 'create'])
                identity_reads = [call['responseCode'] for call in self.calls[:create]
                                  if call['args'][:2] == ['resource', 'show'] and identity in call['args']]
                self.assertEqual(identity_reads, [0, 0], 'Valid identity was observed before apply')
                if missing is not None:
                    self.assertTrue(any(call['args'][:2] == ['resource', 'show'] and missing in call['args']
                                        and call['responseCode'] == 3 for call in self.calls[create + 1:]))
                    self.assertNotEqual(result.returncode, 0, result.stdout)
                    self.assertEqual(result.stdout, '')
                    self.assertIn('unconfirmed', result.stderr)
                else:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertTrue(json.loads(result.stdout)['applied'])
                    after = [call for call in self.calls[create + 1:] if call['args'][:2] == ['resource', 'show']]
                    self.assertEqual({call['args'][call['args'].index('--ids') + 1] for call in after}, set(resources))
                    self.assertTrue(all(call['responseCode'] == 0 for call in after))

    def test_stage_readback_accepts_provider_null_dapr_and_null_rules_independently(self):
        baseline = copy.deepcopy(self.scenario)
        for field in ('dapr', 'rules'):
            with self.subTest(field=field):
                self.scenario = copy.deepcopy(baseline)
                app = self.scenario['resources'].pop(APP_ID)
                if field == 'dapr':
                    app['properties']['configuration']['dapr'] = None
                else:
                    app['properties']['template']['scale']['rules'] = None
                self.scenario['postCreateResources'] = {APP_ID: app}
                result = self.invoke('stage-apply')
                self.assertEqual(len(self.creates()), 1, 'Valid first staging must reach creation')
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertTrue(json.loads(result.stdout)['isolated'])

    def test_nullable_provider_configuration_remains_eligible_for_stop_and_resume(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        baseline = copy.deepcopy(self.scenario)
        for field in ('dapr', 'rules'):
            for mode in ('stage-stop', 'stage-resume'):
                with self.subTest(field=field, mode=mode):
                    self.scenario = copy.deepcopy(baseline)
                    app = self.scenario['resources'][APP_ID]
                    if field == 'dapr':
                        app['properties']['configuration']['dapr'] = None
                    else:
                        app['properties']['template']['scale']['rules'] = None
                        self.scenario['responses']['containerapp revision show']['body']['properties']['template']['scale']['rules'] = None
                    if mode == 'stage-resume':
                        self.scenario['responses']['containerapp revision list']['body'][0]['properties']['active'] = True
                        self.scenario['responses']['containerapp replica list']['body'] = [{'name': 'new-replica'}]
                    result = self.invoke(mode)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    action = 'deactivate' if mode == 'stage-stop' else 'activate'
                    mutations = [call['args'] for call in self.calls
                                 if call['args'][:3] in (['containerapp', 'revision', 'activate'],
                                                        ['containerapp', 'revision', 'deactivate'])]
                    self.assertEqual(len(mutations), 1)
                    self.assertEqual(mutations[0][:3], ['containerapp', 'revision', action])
                    self.assertEqual(mutations[0][mutations[0].index('--revision') + 1], REVISION)
                    lists = [call['args'] for call in self.calls if call['args'][:3] == ['containerapp', 'revision', 'list']]
                    self.assertTrue(lists)
                    self.assertTrue(all('--all' in args for args in lists))
                    output = json.loads(result.stdout)
                    if mode == 'stage-stop':
                        self.assertTrue(output['shutdownConfirmed'])
                        replicas = [call['args'] for call in self.calls if call['args'][:3] == ['containerapp', 'replica', 'list']]
                        self.assertEqual([args[args.index('--revision') + 1] for args in replicas], [REVISION])
                    else:
                        self.assertEqual(output['replica'], 'new-replica')
                        self.assert_exec_processes_released()

    def test_staging_optional_configuration_acceptance_is_narrow_and_never_mutates_invalid(self):
        baseline = copy.deepcopy(self.scenario)
        cases = [('dapr', value, valid) for value, valid in (
            ({}, True), ({'enabled': False}, True), ({'enabled': True}, False),
            (False, False), (0, False), ('', False), ([], False),
            ({'enabled': 0}, False), ({'enabled': None}, False), ({'enabled': 'false'}, False))]
        cases += [('rules', value, valid) for value, valid in (
            ([], True), ([{}], False), ({}, False), (False, False), ('', False))]
        for field, value, valid in cases:
            with self.subTest(field=field, value=value):
                self.scenario = copy.deepcopy(baseline)
                props = self.scenario['resources'][APP_ID]['properties']
                target = props['configuration'] if field == 'dapr' else props['template']['scale']
                target[field] = value
                result = self.invoke('stage-plan' if valid else 'stage-apply')
                self.assertEqual(self.creates(), [])
                self.assertFalse(any(call['args'][:3] in (
                    ['containerapp', 'revision', 'activate'], ['containerapp', 'revision', 'deactivate']) for call in self.calls))
                self.assertEqual(result.returncode == 0, valid, result.stderr)

    def test_public_or_internal_ingress_refuses_before_create(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        for external in (True, False):
            for mode in ('stage-apply', 'stage-stop', 'stage-resume', 'promote-staged'):
                with self.subTest(external=external, mode=mode):
                    self.env.update({key: 'true' for key in GATES})
                    self.app['properties']['configuration']['ingress'] = dict(external=external, targetPort=8080)
                    result = self.invoke(mode)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn('Refusing public or foreign staging', result.stderr)
                    self.assertEqual(self.creates(), [])
                    self.assertFalse(any(call['args'][:3] in (
                        ['containerapp', 'revision', 'activate'], ['containerapp', 'revision', 'deactivate']) for call in self.calls))

    def test_stage_refuses_second_inspection_shape_drift(self):
        drift = copy.deepcopy(self.app)
        drift['properties']['configuration']['ingress'] = dict(external=False)
        self.scenario['postPlanResources'] = {APP_ID: drift}
        result = self.invoke('stage-apply')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Refusing public or foreign staging', result.stderr)
        self.assertEqual(self.creates(), [])

    def test_runtime_refuses_wrong_source_and_moving_main_before_write(self):
        self.env['IMAGE_SOURCE'] = 'c' * 40
        result = self.invoke('stage-apply')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.calls, [])
        del self.env['IMAGE_SOURCE']
        self.main = [self.env['GITHUB_SHA'], 'c' * 40]
        result = self.invoke('stage-apply')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('current main', result.stderr)
        self.assertEqual(self.creates(), [])

    def test_promotion_requires_every_release_flag(self):
        for gate in GATES:
            with self.subTest(gate=gate):
                self.env.update({key: 'true' for key in GATES})
                del self.env[gate]
                result = self.invoke('promote-staged')
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(gate, result.stderr)
                self.assertEqual(self.creates(), [])

    def test_same_digest_promotion_uses_guarded_apply_and_public_rollout(self):
        self.env.update({key: 'true' for key in GATES})
        public = copy.deepcopy(self.app)
        public['properties']['configuration']['ingress'] = dict(external=True, fqdn=ORIGIN.removeprefix('https://'))
        public['properties']['template']['scale']['minReplicas'] = 0
        self.scenario['postCreateResources'] = {APP_ID: public}
        result = self.invoke('promote-staged')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(self.creates()), 1)
        parameters = self.creates()[0]['parameters']['parameters']
        self.assertFalse(parameters['staged']['value'])
        self.assertEqual(parameters['image']['value'], IMAGE)
        updates = [call for call in self.calls if call['args'][:2] == ['containerapp', 'update']]
        self.assertEqual(len(updates), 1)
        self.assertEqual(updates[0]['args'][updates[0]['args'].index('--image') + 1], IMAGE)

    def test_main_moves_after_public_apply_but_before_rollout_update(self):
        self.env.update({key: 'true' for key in GATES})
        public = copy.deepcopy(self.app)
        public['properties']['configuration']['ingress'] = dict(external=True, fqdn=ORIGIN.removeprefix('https://'))
        self.scenario['postCreateResources'] = {APP_ID: public}
        self.main = [self.env['GITHUB_SHA'], self.env['GITHUB_SHA'], 'c' * 40]
        result = self.invoke('promote-staged')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(self.creates()), 1)
        self.assertFalse(any(call['args'][:2] == ['containerapp', 'update'] for call in self.calls))

    def test_stop_observes_every_revision_actual_replica_list(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        self.scenario['responses']['containerapp revision list']['body'].append(
            dict(name='auth-test--older', properties=dict(active=False)))
        result = self.invoke('stage-stop')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(json.loads(result.stdout)['shutdownConfirmed'])
        observations = [call['args'] for call in self.calls if call['args'][:3] == ['containerapp', 'replica', 'list']]
        self.assertEqual({args[args.index('--revision') + 1] for args in observations}, {REVISION, 'auth-test--older'})
        mutation = [call['args'] for call in self.calls if call['args'][:3] == ['containerapp', 'revision', 'deactivate']]
        self.assertEqual(len(mutation), 1)
        self.assertEqual(mutation[0][mutation[0].index('--revision') + 1], REVISION)

    def test_stop_waits_for_other_active_revisions_and_actual_remaining_replicas(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        selected = dict(name=REVISION, properties=dict(active=False))
        other = dict(name='auth-test--older', properties=dict(active=True))
        inactive = dict(name='auth-test--older', properties=dict(active=False))
        self.scenario['responses']['containerapp revision list'] = [
            {'body': [selected, other]}, {'body': [selected, inactive]}, {'body': [selected, inactive]}]
        self.scenario['responses']['containerapp replica list'] = [
            {'body': []}, {'body': [{'name': 'older-live'}]},
            {'body': []}, {'body': [{'name': 'older-terminating'}]},
            {'body': []}, {'body': []}]
        result = self.invoke('stage-stop')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(json.loads(result.stdout)['shutdownConfirmed'])
        observations = [call for call in self.calls if call['args'][:3] == ['containerapp', 'replica', 'list']]
        self.assertEqual(len(observations), 6, 'Success must follow actual empty observations for both revisions')

    def test_stop_timeout_never_claims_zero_when_another_revision_has_replicas(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        self.scenario['responses']['containerapp revision list']['body'].append(
            dict(name='auth-test--older', properties=dict(active=True)))
        self.scenario['responses']['containerapp replica list']['body'] = [{'name': 'remaining'}]
        started = time.monotonic()
        result = self.invoke('stage-stop')
        self.assertNotEqual(result.returncode, 0)
        self.assertLess(time.monotonic() - started, 74)
        self.assertIn('unconfirmed', result.stderr)
        self.assertNotIn('"shutdownConfirmed": true', result.stdout)

    def test_stop_rejects_wrong_selected_revision_or_artifact_without_deactivation(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        baseline = copy.deepcopy(self.scenario['responses']['containerapp revision show']['body'])
        for field in ('foreignName', 'wrongName', 'image'):
            with self.subTest(field=field):
                selected = copy.deepcopy(baseline)
                if field == 'image':
                    selected['properties']['template']['containers'][0]['image'] = IMAGE[:-1] + 'b'
                else:
                    selected['name'] = 'foreign--revision' if field == 'foreignName' else 'auth-test--other'
                self.scenario['responses']['containerapp revision show']['body'] = selected
                result = self.invoke('stage-stop')
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(any(call['args'][:3] == ['containerapp', 'revision', 'deactivate'] for call in self.calls))

    def test_malformed_failed_or_ambiguous_stop_observation_never_confirms(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        baseline = copy.deepcopy(self.scenario)
        for command, response in (
                ('containerapp revision list', {'body': {}}),
                ('containerapp revision list', {'body': []}),
                ('containerapp revision list', {'body': [{'name': REVISION, 'properties': {'active': 'false'}}]}),
                ('containerapp replica list', {'body': [None]}),
                ('containerapp replica list', {'error': CANARY}),
                ('containerapp replica list', {'raw': '{malformed'})):
            with self.subTest(command=command, response=response):
                self.scenario = copy.deepcopy(baseline)
                self.scenario['responses'][command] = response
                result = self.invoke('stage-stop')
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn('"shutdownConfirmed": true', result.stdout)

    def test_resume_selects_exact_new_replica_and_fails_closed_without_local_tools(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        self.scenario['responses']['containerapp revision list']['body'][0]['properties']['active'] = True
        self.scenario['responses']['containerapp replica list']['body'] = [{'name': 'new-replica'}]
        result = self.invoke('stage-resume')
        self.assertEqual(result.returncode, 0, result.stderr)
        output = json.loads(result.stdout)
        self.assertEqual(output['replica'], 'new-replica')
        command = next(call['args'] for call in self.calls if call['args'][:2] == ['containerapp', 'exec'])
        execution = next(call for call in self.calls if call['args'][:2] == ['containerapp', 'exec'])
        self.assertTrue(execution['stdinIsTty'])
        self.assertEqual(execution['terminalSetup'], 'ok')
        self.assertTrue(execution['terminalRestored'])
        for flag, value in (('--revision', REVISION), ('--replica', 'new-replica'), ('--container', 'auth-service')):
            self.assertEqual(command[command.index(flag) + 1], value)
        self.scenario['responses']['containerapp exec'] = {'error': CANARY}
        result = self.invoke('stage-resume')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Local health/tools unavailable', result.stderr)

    def assert_exec_processes_released(self):
        execution = next(call for call in self.calls if call['args'][:2] == ['containerapp', 'exec'])
        self.assertTrue(execution['stdinIsTty'])
        self.assertEqual(execution['terminalSetup'], 'ok')
        self.assertTrue(execution['terminalRestored'])
        self.assertFalse(execution['stdoutIsTty'])
        self.assertFalse(execution['stderrIsTty'])
        self.assertIsNotNone(execution['terminalName'])
        for pid in (execution['pid'], execution['parentPid']):
            with self.assertRaises(ProcessLookupError, msg='No owned CLI/provider process may survive completion'):
                os.kill(pid, 0)

    def test_resume_executes_live_and_ready_and_refuses_failed_probes_or_missing_tools(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        self.scenario['responses']['containerapp revision list']['body'][0]['properties']['active'] = True
        self.scenario['responses']['containerapp replica list']['body'] = [{'name': 'new-replica'}]
        live, ready = ('http://127.0.0.1:8080/health/' + path for path in ('live', 'ready'))
        cases = [('curl', ['curl'], {}), ('wget', ['wget'], {}),
                 ('live-fails', ['curl'], {live: 22}), ('ready-fails', ['curl'], {ready: 22}),
                 ('no-tool', [], {})]
        self.health_observations = []
        for label, tools, outcomes in cases:
            with self.subTest(case=label):
                self.scenario['responses']['containerapp exec'] = {'health': {'tools': tools, 'outcomes': outcomes}}
                result = self.invoke('stage-resume')
                healthy = bool(tools) and not outcomes
                execution = next(call for call in self.calls if call['args'][:2] == ['containerapp', 'exec'])
                requests = execution['healthRequests']
                self.health_observations.append(dict(case=label, returncode=result.returncode, requests=requests))
                self.assertEqual(result.returncode == 0, healthy, result.stderr)
                if healthy:
                    self.assertTrue(json.loads(result.stdout)['resumed'])
                    self.assertCountEqual(requests, [dict(tool=tools[0], url=url) for url in (live, ready)])
                elif outcomes:
                    self.assertTrue(set(outcomes) <= {request['url'] for request in requests})
                else:
                    self.assertEqual(requests, [])
                self.assert_exec_processes_released()

    def test_resume_captures_provider_output_and_releases_terminal_on_success_and_refusal(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        self.scenario['responses']['containerapp revision list']['body'][0]['properties']['active'] = True
        self.scenario['responses']['containerapp replica list']['body'] = [{'name': 'new-replica'}]
        for healthy in (True, False):
            with self.subTest(healthy=healthy):
                # More than a pipe buffer on both streams exposes output forwarding
                # and wait-before-drain deadlocks, without any actual credential.
                # These are transport controls, separate from actual probe execution.
                self.scenario['responses']['containerapp exec'] = {
                    'raw': CANARY + 'x' * 131072 + ('\nAUTH_LOCAL_HEALTH_OK' if healthy else ''),
                    'stderr': CANARY + 'y' * 131072}
                result = self.invoke('stage-resume')
                self.assertEqual(result.returncode == 0, healthy, result.stderr)
                if healthy:
                    self.assertTrue(json.loads(result.stdout)['resumed'])
                else:
                    self.assertIn('Local health/tools unavailable', result.stderr)
                    self.assertNotIn('"resumed": true', result.stdout)
                self.assert_exec_processes_released()

    def test_resume_timeout_kills_child_and_suppresses_partial_provider_output(self):
        self.env['AUTH_STAGE_REVISION'] = REVISION
        self.scenario['responses']['containerapp revision list']['body'][0]['properties']['active'] = True
        self.scenario['responses']['containerapp replica list']['body'] = [{'name': 'new-replica'}]
        self.scenario['responses']['containerapp exec'] = {'raw': CANARY, 'stderr': CANARY, 'delay': 65}
        started = time.monotonic()
        result = self.invoke('stage-resume')

        self.assertNotEqual(result.returncode, 0)
        self.assertLess(time.monotonic() - started, 74)
        self.assertIn('TimeoutExpired', result.stderr)
        self.assertEqual(result.stdout, '')
        self.assert_exec_processes_released()


class ActivationWorkflowConfigurationTests(unittest.TestCase):
    def test_activation_uses_existing_branch_oidc_scope_and_excludes_normal_jobs(self):
        workflow = WORKFLOW.read_text()
        activation = workflow.split('\n  activation:\n', 1)[1]
        self.assertIn("github.ref == 'refs/heads/main' && github.event_name == 'workflow_dispatch'", activation)
        self.assertIn('contents: read\n      id-token: write', activation)
        self.assertNotIn('environment:', activation)
        self.assertNotIn('packages: write', activation)
        self.assertIn('azure/login@a641126d1b8aa4d1fa005f4f92df94a3a4c4c906', activation)
        self.assertIn('persist-credentials: false', activation)
        for key, expression in (('AUTH_OPERATION', 'inputs.operation'),
                                ('AUTH_EXPECTED_SOURCE_SHA', 'inputs.expected-source-sha'),
                                ('AUTH_IMAGE', 'inputs.image'), ('AUTH_STAGE_REVISION', 'inputs.revision')):
            self.assertIn(key + ': ${{ ' + expression + ' }}', activation)
        self.assertNotIn('${{', ROUTE, 'Untrusted workflow inputs must reach shell through env only')
        for job in ('publish', 'deploy'):
            block = re.search(r'(?ms)^  ' + job + r':\n(.*?)(?=^  \S|\Z)', workflow)[1]
            self.assertIn("inputs.operation == 'deploy'", block)
            self.assertIn("github.event_name == 'push'", block)
        self.assertIn("if: github.event_name != 'workflow_dispatch' || inputs.operation == 'deploy'", workflow)
        self.assertIn('cancel-in-progress: false', workflow)
        self.assertIn("github.ref || 'delivery'", workflow)


if __name__ == '__main__':
    unittest.main()
