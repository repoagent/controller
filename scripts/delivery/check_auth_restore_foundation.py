"""Mandatory Broker-owned safety check against real Bicep output; no cloud calls."""
import json
import os
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
BICEP = os.environ.get('BICEP_BIN', str(Path.home() / '.azure/bin/bicep'))


class RestoreFoundationCompilationTests(unittest.TestCase):
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
