"""Fixed authority operator CLI over the existing real POSIX PTY boundary."""
import json
import os
from pathlib import Path
import unittest
from scripts.delivery import test_broker_console_version as version_fixture

SCRIPT = Path(__file__).with_name('broker-console-authority.py')
SECRET = 'DO_NOT_EXPORT_SECRET'
COVERAGE = 'referenced-rows-only; gate reread is not a transaction snapshot or accounting reconciliation'


def partial_projection():
    missing = dict(read='not-referenced',complete=True,epochRelation=None,fields=None)
    observation = dict(schema='repoagent.authority-observation/v1',scope='fixed-repository',coverage=COVERAGE,
        observedAt='2026-10-28T21:00:00+00:00',readCount=2,state='partial',gateObservation='unchanged',
        settlementProven=False,allOutstandingAttemptsExamined=False,
        gate=dict(read='present',complete=False,epochRelation='match',fields=dict(observedState='closed',
            ownerReference='absent',verifierReference='absent',sequence=3,outstandingTokenCount=None,pendingCount=0)),
        owner=dict(missing),receipt=dict(missing),runtimeAttempt=dict(missing),verifierAttempt=dict(missing))
    return dict(schema='repoagent.authority-observation/v1',compiledVersion='1.1.0',compiledSource='b'*40,observation=observation)


@unittest.skipIf(os.name == 'nt', 'Authority console uses Linux PTY transport')
class BrokerAuthorityProbeTests(unittest.TestCase):
    def invoke(self, change=None, **environment):
        def setup(fixtures):
            identity = version_fixture.GROUP+'/providers/Microsoft.ManagedIdentity/userAssignedIdentities/repoagent-auth-runtime'
            fixtures[version_fixture.APP]['identity'] = dict(type='UserAssigned',userAssignedIdentities={
                identity:dict(clientId='8016ef66-b1b7-4118-8de4-07814ebc9896')})
            released = fixtures.pop('repos/repoagent/controller/releases/tags/broker-v1.0.0')
            released['tag_name'] = 'broker-v1.1.0'
            fixtures['repos/repoagent/controller/releases/tags/broker-v1.1.0'] = released
            fixtures['repos/repoagent/controller/commits/broker-v1.1.0'] = fixtures.pop('repos/repoagent/controller/commits/broker-v1.0.0')
            asset = fixtures['repos/repoagent/controller/releases/assets/7']
            asset.update(tag='broker-v1.1.0',version='1.1.0',imageTag='ghcr.io/repoagent/controller-auth:broker-v1.1.0')
            fixtures['authorityPayload'] = partial_projection()
            if change: change(fixtures)
        values = dict(AUTH_RELEASE_TAG='broker-v1.1.0')
        values.update(environment)
        return version_fixture.BrokerConsoleVersionTests.invoke(self, setup, script=SCRIPT,
            report_name='authority-probe.json', **values)

    def test_valid_partial_projection_survives_a_nonzero_cli_exit_without_becoming_settlement_proof(self):
        result, report, calls, output = self.invoke(lambda fixtures: fixtures.update(consoleExitCode=1))
        self.assertEqual(result.returncode, 1)
        self.assertIsNotNone(report)
        self.assertTrue(report['projectionValidated'])
        self.assertEqual(report['state'], 'partial')
        self.assertIsNone(report['projection']['observation']['gate']['fields']['outstandingTokenCount'])
        self.assertFalse(report['settlementProven'])
        self.assertFalse(report['allOutstandingAttemptsExamined'])
        self.assertFalse(report['remoteExitStatusEstablished'])
        self.assertNotIn(SECRET, json.dumps(report)+result.stdout+result.stderr)
        self.assertEqual(sorted(path.name for path in output.iterdir()), ['authority-probe.json'])
        console = next(call for call in calls if call[:3] == ['az','containerapp','exec'])
        self.assertEqual(console[console.index('--command')+1], 'dotnet /app/RepoAgent.Auth.dll --inspect-retained-authority')

    def test_legacy_or_unknown_major_release_is_refused_before_any_console_or_provider_call(self):
        for tag in ('broker-v1.0.0','broker-v1.0.99','broker-v2.0.0'):
            with self.subTest(tag=tag):
                def mapping(fixtures):
                    release = fixtures.pop('repos/repoagent/controller/releases/tags/broker-v1.1.0')
                    release['tag_name'] = tag
                    fixtures['repos/repoagent/controller/releases/tags/'+tag] = release
                    fixtures['repos/repoagent/controller/commits/'+tag] = dict(sha='b'*40)
                    fixtures['repos/repoagent/controller/releases/assets/7'].update(tag=tag,
                        version=tag.removeprefix('broker-v'),imageTag='ghcr.io/repoagent/controller-auth:'+tag)
                    fixtures['authorityPayload']['compiledVersion'] = tag.removeprefix('broker-v')
                result, report, calls, _ = self.invoke(mapping,AUTH_RELEASE_TAG=tag)
                self.assertNotEqual(result.returncode, 0)
                self.assertIsNone(report)
                self.assertEqual(calls, [])

    def test_valid_unavailable_observation_is_retained_without_inventing_missing_rows_or_zero_counts(self):
        def unavailable(fixtures):
            value = fixtures['authorityPayload']['observation']
            value.update(state='unavailable',gateObservation='not-checked',readCount=1)
            for name in ('gate','owner','receipt','runtimeAttempt','verifierAttempt'):
                value[name] = dict(read='unavailable',complete=False,epochRelation=None,fields=None)
        result, report, _, _ = self.invoke(unavailable)
        self.assertEqual(result.returncode, 1)
        self.assertTrue(report['projectionValidated'])
        self.assertEqual(report['state'], 'unavailable')
        self.assertIsNone(report['projection']['observation']['gate']['fields'])

    def test_valid_observed_projection_succeeds_without_turning_observation_into_readiness(self):
        def observed(fixtures):
            value = fixtures['authorityPayload']['observation']
            value['state'] = 'observed'
            value['gate']['complete'] = True
            value['gate']['fields']['outstandingTokenCount'] = 0
        result, report, _, _ = self.invoke(observed)
        self.assertEqual(result.returncode,0,result.stderr)
        self.assertEqual(report['state'],'observed')
        self.assertTrue(report['projectionValidated'])
        self.assertFalse(report['settlementProven'])
        self.assertFalse(report['allOutstandingAttemptsExamined'])

    def test_wrong_source_context_is_refused_before_any_provider_call(self):
        for environment in (dict(GITHUB_SHA='c'*40),dict(GITHUB_REF='refs/heads/other'),dict(AZURE_SUBSCRIPTION_ID='other')):
            with self.subTest(environment=environment):
                result, report, calls, _ = self.invoke(**environment)
                self.assertNotEqual(result.returncode,0)
                self.assertIsNone(report)
                self.assertEqual(calls,[])

    def test_a_projection_followed_by_a_console_hang_is_not_exported_as_completed_evidence(self):
        result, report, _, _ = self.invoke(lambda fixtures: fixtures.update(consoleMode='trailing-hang'))
        self.assertEqual(result.returncode,1)
        self.assertEqual(report['reason'],'console-timeout')
        self.assertFalse(report['projectionValidated'])
        self.assertNotIn('projection',report)

    def test_nullable_flags_and_unknown_fields_stay_distinct_from_false_and_healthy(self):
        def owner(fixtures):
            value = fixtures['authorityPayload']['observation']
            value['gate']['fields']['ownerReference'] = 'valid'
            value['readCount'] = 4
            value['owner'] = dict(read='present',complete=False,epochRelation='match',fields=dict(
                observedState='unknown',acknowledged=None,payloadRedacted=False,outstandingTokenCount=None,
                runtimeReference='absent',observationReferencePresent=False,
                **{name:'absent' for name in ('expiresAt','lastActivityAt','firstTerminalAt','runtimeExpiresAt',
                    'runtimeObservationReservedAt','runtimeObservationJwtIssuedAt','runtimeObservationJwtExpiresAt')}))
            value['receipt'] = dict(read='missing',complete=False,epochRelation=None,fields=None)
        result, report, _, _ = self.invoke(owner)
        self.assertEqual(result.returncode, 1)
        fields = report['projection']['observation']['owner']['fields']
        self.assertIsNone(fields['acknowledged'])
        self.assertIs(fields['payloadRedacted'],False)
        self.assertEqual(fields['observedState'],'unknown')
        self.assertEqual(report['state'],'partial')

    def test_deep_unknown_fields_wrong_values_or_false_proof_coercions_export_no_projection(self):
        cases = [
            (('secret',),SECRET), (('compiledSource',),'c'*40), (('compiledVersion',),'1.1.1'),
            (('observation','secret'),SECRET), (('observation','coverage'),SECRET),
            (('observation','settlementProven'),True), (('observation','settlementProven'),0),
            (('observation','allOutstandingAttemptsExamined'),True), (('observation','readCount'),7),
            (('observation','readCount'),True), (('observation','observedAt'),'2026-10-28T22:00:00+01:00'),
            (('observation','gate','fields','secret'),SECRET), (('observation','gate','fields','pendingCount'),-1),
            (('observation','gate','fields','pendingCount'),True), (('observation','gate','fields','pendingCount'),1.5),
            (('observation','gate','fields','observedState'),SECRET), (('observation','gate','epochRelation'),SECRET),
            (('observation','owner','fields'),dict(controllerSecretHash=SECRET)),
            (('observation','gate','complete'),True)]
        for path,value in cases:
            with self.subTest(path=path,value=value):
                def mutate(fixtures):
                    target = fixtures['authorityPayload']
                    for key in path[:-1]: target = target[key]
                    target[path[-1]] = value
                result, report, _, _ = self.invoke(mutate)
                self.assertEqual(result.returncode, 1)
                self.assertFalse(report['projectionValidated'])
                self.assertNotIn('projection',report)
                self.assertNotIn(SECRET,json.dumps(report)+result.stdout+result.stderr)

    def test_missing_or_wrong_runtime_identity_prevents_console(self):
        for mode in ('missing','client','resource','system'):
            with self.subTest(mode=mode):
                def wrong(fixtures):
                    app = fixtures[version_fixture.APP]
                    identity = app['identity']
                    if mode=='missing': app.pop('identity')
                    elif mode=='system': identity['type']='SystemAssigned'
                    else:
                        key = next(iter(identity['userAssignedIdentities']))
                        if mode=='client': identity['userAssignedIdentities'][key]['clientId']='f79a35bd-ed2f-4c9f-be3e-f0c336009562'
                        else: identity['userAssignedIdentities'][key+'-other'] = identity['userAssignedIdentities'].pop(key)
                result, report, calls, _ = self.invoke(wrong)
                self.assertEqual(result.returncode, 1)
                self.assertFalse(report['projectionValidated'])
                self.assertFalse(any(call[:3] == ['az','containerapp','exec'] for call in calls))

    def test_ambiguous_duplicate_oversized_or_drifting_capture_exports_no_projection(self):
        for mode in ('ambiguous','private-json','overflow','drift','duplicate','projection-limit','nested'):
            with self.subTest(mode=mode):
                def raw(fixtures):
                    if mode=='duplicate':
                        fixtures['authorityRaw'] = json.dumps(fixtures['authorityPayload']).replace(
                            '"compiledSource":', '"compiledSource":"'+SECRET+'","compiledSource":',1)
                    elif mode=='projection-limit': fixtures['authorityRaw'] = json.dumps(fixtures['authorityPayload'])+' '*16384
                    elif mode=='nested': fixtures['authorityRaw'] = '['*1100+'0'+']'*1100
                    else: fixtures['consoleMode'] = mode
                result, report, _, _ = self.invoke(raw)
                self.assertEqual(result.returncode, 1)
                self.assertFalse(report['projectionValidated'])
                self.assertNotIn('projection',report)
                self.assertNotIn(SECRET,json.dumps(report)+result.stdout+result.stderr)
