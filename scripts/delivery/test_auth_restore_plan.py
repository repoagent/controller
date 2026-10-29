"""Protect the offline rehearsal plan using real CLI and Bicep compilation.

No Azure service, credential, deployment or provider imitation is involved.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest


ROOT = Path(__file__).resolve().parents[2]
BICEP = os.environ.get('BICEP_BIN', str(Path.home() / '.azure/bin/bicep'))


class RestorePlanTests(unittest.TestCase):
    def plan(self, run_id):
        return subprocess.run([sys.executable, str(ROOT / 'scripts/delivery/auth-restore-plan.py'),
                               '--run-id', run_id], capture_output=True, text=True, timeout=30)

    def test_run_owned_plan_cannot_select_production_data_or_write_cloud(self):
        result = self.plan('20261029-a1b2c3')
        self.assertEqual(result.returncode, 0, result.stderr)
        plan = json.loads(result.stdout)
        self.assertEqual(plan['operation'], 'offline-plan')
        self.assertFalse(plan['cloudAccessed'])
        self.assertEqual(plan['parameters']['runId']['value'], '20261029-a1b2c3')
        self.assertEqual(plan['resources']['appName'], 'repoagent-r230-20261029-a1b2c3')
        self.assertEqual(plan['resources']['storageName'], 'ra23020261029a1b2c3')
        self.assertEqual(plan['resources']['wrappingKeyName'], 'restore230-20261029-a1b2c3-wrap')
        self.assertEqual(plan['resources']['vaultName'], 'repoagent-auth-kv')
        self.assertEqual(plan['tableState'], 'not-initialized')
        self.assertFalse(plan['runtimeDeployed'])
        self.assertFalse(plan['rbacApplied'])

    def test_unsafe_or_ambiguous_run_names_are_refused(self):
        for run_id in ('repoagent-auth', '../production', '20261029-ABCDEF', '20261029-a1b2c3;id',
                       '20261029-a1b2c3\n', '20261029-12345', '20260230-a1b2c3'):
            with self.subTest(run_id=run_id):
                result = self.plan(run_id)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, '')

    def test_foundation_uses_existing_vault_and_has_no_runtime_or_permissions(self):
        result = subprocess.run([BICEP, 'build', str(ROOT / 'infra/auth/restore-foundation.bicep'),
                                 '--stdout'], capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        template = json.loads(result.stdout)
        modules = {r['name']: r for r in template['resources']
                   if r['type'] == 'Microsoft.Resources/deployments'}
        foundation = modules['restore-foundation']['properties']
        self.assertFalse(foundation['parameters']['provisionVault']['value'])
        self.assertFalse(foundation['parameters']['deployApp']['value'])
        child = foundation['template']
        vault = next(r for r in child['resources'] if r['type'] == 'Microsoft.KeyVault/vaults')
        self.assertEqual(vault['condition'], "[parameters('provisionVault')]")
        self.assertTrue(child['parameters']['provisionVault']['defaultValue'])
        api = next(r for r in child['resources'] if r['type'] == 'Microsoft.App/containerApps')
        self.assertEqual(api.get('condition'), "[parameters('deployApp')]")
        scheduled = next(r for r in child['resources'] if r['type'] == 'Microsoft.Resources/deployments')
        self.assertEqual(scheduled.get('condition'), "[and(parameters('deployApp'), not(parameters('staged')))]")
        cleanup = modules['restore-cleanup-identity']['properties']
        self.assertTrue(cleanup['parameters']['identityOnly']['value'])
        job = next(r for r in cleanup['template']['resources'] if r['type'] == 'Microsoft.App/jobs')
        self.assertEqual(job.get('condition'), "[not(parameters('identityOnly'))]")
        def deployed_resources(document):
            for resource in document['resources']:
                yield resource
                if resource['type'] == 'Microsoft.Resources/deployments':
                    yield from deployed_resources(resource['properties']['template'])
        resources = list(deployed_resources(template))
        self.assertFalse(any(r['type'].startswith('Microsoft.Authorization/') for r in resources))
        keys = [r for r in resources if r['type'] == 'Microsoft.KeyVault/vaults/keys']
        self.assertEqual(len(keys), 1)
        self.assertEqual(keys[0]['properties']['keyOps'], ['wrapKey', 'unwrapKey'])
        self.assertFalse(keys[0]['properties']['attributes']['exportable'])
        self.assertNotIn('sign', keys[0]['properties']['keyOps'])


if __name__ == '__main__':
    unittest.main()
