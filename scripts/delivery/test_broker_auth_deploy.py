"""Run the real deploy shell and attestation helper with external boundary doubles.

These tests protect release selection and readback, not Azure atomicity or running executions.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SHA = 'b' * 40
IMAGE = 'ghcr.io/repoagent/controller-auth@sha256:' + 'a' * 64
OLD = 'ghcr.io/repoagent/controller-auth@sha256:' + 'c' * 64
PROVIDER = '''import json, os, pathlib, sys
name, args = pathlib.Path(sys.argv[0]).name, sys.argv[1:]
with open(os.environ['LEDGER'], 'a') as stream:
    stream.write(json.dumps([name, *args]) + '\\n')
applied = pathlib.Path(os.environ['APPLIED'])
if name == 'git':
    print(os.environ['GITHUB_SHA'] + (' refs/heads/main' if args[0] == 'ls-remote' else ''))
elif name == 'skopeo':
    print(json.dumps(dict(os='linux', architecture='amd64', config=dict(User='10001:10001',
        Labels={'io.repoagent.role':'auth-service','org.opencontainers.image.revision':os.environ['GITHUB_SHA']}))))
elif name == 'bicep':
    pathlib.Path(args[args.index('--outfile')+1]).write_text('{}')
elif name == 'curl':
    pass
elif name == 'az':
    if args[:3] == ['containerapp', 'job', 'show']:
        if os.environ['TEST_CLEANUP_FAIL'] == 'true':
            sys.exit('Cleanup read unavailable')
        print(os.environ['TEST_CLEANUP_IMAGE'])
    elif args[:2] == ['containerapp', 'show']:
        query=args[args.index('--query')+1]
        if query == 'properties.template.containers[0].image':
            phase = 'AFTER' if applied.exists() else 'BEFORE'
            if os.environ['TEST_API_FAIL'] == phase:
                sys.exit('API read unavailable')
            print(os.environ['TEST_API_' + phase])
        else:
            print('example.test' if query == 'properties.configuration.ingress.fqdn' else 'true')
    elif args[:3] == ['deployment', 'group', 'create']:
        applied.touch()
    elif args[:3] in (['deployment', 'group', 'show'], ['deployment', 'group', 'what-if']) or args[:2] == ['resource','list']:
        print('{}')
    else:
        sys.exit('Unexpected Azure command')
else:
    sys.exit('Unexpected provider')
'''


class BrokerImageDeploymentTests(unittest.TestCase):
    def deploy(self, operation, *, requested=IMAGE, api=IMAGE, after=None,
               cleanup=IMAGE, api_failure='', cleanup_failure=False):
        with tempfile.TemporaryDirectory(prefix='broker-deploy-') as directory:
            root = Path(directory)
            for relative in ['infra/auth/deploy.sh', 'infra/auth/main.bicep', 'infra/auth/cleanup.bicep',
                             'scripts/delivery/auth-pipeline.py']:
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / relative, target)
            binaries = root / 'bin'
            binaries.mkdir()
            for name in ['git', 'skopeo', 'bicep', 'az', 'curl']:
                path = binaries / name
                path.write_text(f'#!{sys.executable}\n' + PROVIDER)
                path.chmod(0o700)
            ledger = root / 'calls.jsonl'
            env = dict(PATH=str(binaries) + os.pathsep + os.defpath, HOME=str(root), LANG='C.UTF-8',
                       LEDGER=str(ledger), APPLIED=str(root / 'applied'), BICEP_BIN=str(binaries / 'bicep'),
                       AZURE_SUBSCRIPTION_ID='46f7e5d6-05f3-435b-8089-6c63a203dbfa',
                       AZURE_RESOURCE_GROUP='rg-repoagent-controller-auth', AZURE_LOCATION='swedencentral',
                       AUTH_APP_NAME='repoagent-auth', AUTH_STORAGE_ACCOUNT_NAME='fixturestorage',
                       AUTH_KEY_VAULT_NAME='fixturevault', AUTH_SIGNING_KEY_URI='https://fixture/keys/sign/version',
                       AUTH_DATA_PROTECTION_KEY_URI='https://fixture/keys/wrap/version', AUTH_DEPLOYMENT_EPOCH='fixture',
                       AUTH_IMAGE_REPOSITORY='ghcr.io/repoagent/controller-auth', AUTH_IMAGE=requested,
                       AUTH_EXPECTED_SOURCE_SHA=SHA, GITHUB_SHA=SHA, GITHUB_REF='refs/heads/main',
                       TEST_API_BEFORE=api, TEST_API_AFTER=api if after is None else after,
                       TEST_CLEANUP_IMAGE=cleanup, TEST_API_FAIL=api_failure,
                       TEST_CLEANUP_FAIL=str(cleanup_failure).lower())
            result = subprocess.run([shutil.which('bash'), str(root / 'infra/auth/deploy.sh'), operation],
                                    cwd=root, env=env, capture_output=True, text=True, timeout=10)
            calls = [json.loads(line) for line in ledger.read_text().splitlines()]
            self.assertNotIn('Unexpected', result.stderr, result.stderr)
            return result, calls

    def assert_no_deployment(self, calls):
        self.assertFalse(any(call[:4] in (['az', 'deployment', 'group', 'create'],
                                         ['az', 'deployment', 'group', 'what-if']) for call in calls), calls)

    def test_cleanup_only_change_to_another_release_is_refused_before_deployment(self):
        for operation in ['cleanup-plan', 'cleanup-apply']:
            with self.subTest(operation=operation):
                result, calls = self.deploy(operation, api=OLD)
                self.assertNotEqual(result.returncode, 0, 'Cleanup accepted a different release from the API')
                self.assertIn('Cleanup must use the current broker API image', result.stderr)
                self.assert_no_deployment(calls)

    def test_cleanup_bootstrap_or_repair_can_use_the_current_api_release(self):
        for operation, action in [('cleanup-plan', 'what-if'), ('cleanup-apply', 'create')]:
            with self.subTest(operation=operation):
                result, calls = self.deploy(operation)
                self.assertEqual(result.returncode, 0, result.stderr)
                deployment = next(call for call in calls if call[:4] == ['az', 'deployment', 'group', action])
                self.assertIn('image=' + IMAGE, deployment)

    def test_missing_invalid_or_unavailable_api_evidence_prevents_cleanup_deployment(self):
        for operation in ['cleanup-plan', 'cleanup-apply']:
            for api, failure in [('', ''), ('null', ''), ('not-an-image', ''), (IMAGE, 'BEFORE')]:
                with self.subTest(operation=operation, api=api, failure=failure):
                    result, calls = self.deploy(operation, api=api, api_failure=failure)
                    self.assertNotEqual(result.returncode, 0, 'Cleanup proceeded without valid API evidence')
                    self.assert_no_deployment(calls)

    def test_matching_invalid_image_is_still_refused_by_real_attestation(self):
        for operation in ['cleanup-plan', 'cleanup-apply']:
            with self.subTest(operation=operation):
                result, calls = self.deploy(operation, api='not-an-image', requested='not-an-image')
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('immutable SHA256 digest', result.stderr)
                self.assert_no_deployment(calls)

    def test_cleanup_apply_refuses_api_or_job_drift_after_the_write(self):
        for after, cleanup in [(IMAGE, OLD), (OLD, IMAGE), ('', IMAGE), (IMAGE, '')]:
            with self.subTest(after=after, cleanup=cleanup):
                result, calls = self.deploy('cleanup-apply', after=after, cleanup=cleanup)
                self.assertNotEqual(result.returncode, 0, 'Cleanup deployment succeeded without verifying both images')
                self.assertIn('Broker image mismatch', result.stderr)
                self.assertIn(['az', 'deployment', 'group', 'create'], [call[:4] for call in calls])

    def test_cleanup_apply_cannot_succeed_when_postapply_readback_is_unavailable(self):
        for failure in [dict(api_failure='AFTER'), dict(cleanup_failure=True)]:
            with self.subTest(failure=failure):
                result, calls = self.deploy('cleanup-apply', **failure)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(['az', 'deployment', 'group', 'create'], [call[:4] for call in calls])

    def test_paired_upgrade_and_rollback_can_move_both_images_to_the_requested_release(self):
        for before, requested in [(OLD, IMAGE), (IMAGE, OLD)]:
            with self.subTest(before=before, requested=requested):
                result, calls = self.deploy('apply', requested=requested, api=before,
                                            after=requested, cleanup=requested)
                self.assertEqual(result.returncode, 0, result.stderr)
                deployment = next(call for call in calls if call[:4] == ['az', 'deployment', 'group', 'create'])
                self.assertIn('image=' + requested, deployment)
                self.assertEqual(sum(call[0] == 'curl' for call in calls), 2)
                self.assertIn('API and cleanup use ' + requested, result.stdout)

    def test_paired_deployment_requires_both_images_before_reporting_healthy(self):
        for api, cleanup, success in [(IMAGE, IMAGE, True), (OLD, IMAGE, False), (IMAGE, OLD, False), (OLD, OLD, False)]:
            with self.subTest(api=api, cleanup=cleanup):
                result, calls = self.deploy('apply', api=api, cleanup=cleanup)
                self.assertEqual(result.returncode == 0, success, result.stderr)
                self.assertEqual(sum(call[0] == 'curl' for call in calls), 2 if success else 0)
                self.assertEqual('API and cleanup use' in result.stdout, success)


if __name__ == '__main__':
    unittest.main()
