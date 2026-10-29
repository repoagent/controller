"""Mandatory Broker-owned safety check against real Bicep output; no cloud calls."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
BICEP = os.environ.get('BICEP_BIN', str(Path.home() / '.azure/bin/bicep'))


class RestoreFoundationCompilationTests(unittest.TestCase):
    def test_planned_app_name_reaches_cleanup_job_within_provider_limit(self):
        run_id = '20261029-9f3a76'
        planned = subprocess.run([sys.executable, str(ROOT / 'scripts/delivery/auth-restore-plan.py'),
                                  '--run-id', run_id], capture_output=True, text=True, timeout=30)
        self.assertEqual(planned.returncode, 0, planned.stderr)
        resources = json.loads(planned.stdout)['resources']
        compiled = subprocess.run([BICEP, 'build', str(ROOT / 'infra/auth/restore-foundation.bicep'),
                                   '--stdout'], capture_output=True, text=True, timeout=30)
        self.assertEqual(compiled.returncode, 0, compiled.stderr)
        template = json.loads(compiled.stdout)
        # Read the real compiled format operand, not a second copy of the prefix.
        app_format = re.fullmatch(r"\[format\('([^']+)', parameters\('runId'\)\)\]",
                                  template['variables']['appName'])
        self.assertIsNotNone(app_format)
        app_name = app_format[1].format(run_id)
        self.assertEqual(app_name, resources['appName'])
        modules = {r['name']: r['properties'] for r in template['resources']
                   if r['type'] == 'Microsoft.Resources/deployments'}
        foundation = modules['restore-foundation']
        self.assertEqual(foundation['parameters']['appName']['value'], "[variables('appName')]")
        cleanup_identity = modules['restore-cleanup-identity']
        self.assertEqual(cleanup_identity['parameters']['appName']['value'], "[variables('appName')]")
        public_cleanup = next(r['properties'] for r in foundation['template']['resources']
                              if r['type'] == 'Microsoft.Resources/deployments')
        self.assertEqual(public_cleanup['parameters']['appName']['value'], "[parameters('appName')]")
        job = next(r for r in public_cleanup['template']['resources'] if r['type'] == 'Microsoft.App/jobs')
        identity = next(r for r in cleanup_identity['template']['resources']
                        if r['type'] == 'Microsoft.ManagedIdentity/userAssignedIdentities')
        self.assertEqual(job['name'], "[format('{0}-cleanup', parameters('appName'))]")
        self.assertEqual(identity['name'], job['name'])
        job_name = '{0}-cleanup'.format(app_name)
        self.assertLessEqual(len(job_name), 32)
        self.assertRegex(job_name, r'^[a-z][a-z0-9-]*[a-z0-9]$')
        self.assertTrue(resources['cleanupIdentityId'].endswith('/' + job_name))

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
