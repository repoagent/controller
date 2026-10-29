#!/usr/bin/env python3
"""Print an offline, isolated foundation plan; never contact Azure or use credentials."""
import argparse
from datetime import datetime
import json
from pathlib import Path
import re


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-id', required=True, help='UTC date and six lowercase hex characters: YYYYMMDD-abcdef')
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9]{8}-[0-9a-f]{6}', args.run_id):
        parser.error('run-id must be YYYYMMDD followed by a hyphen and six lowercase hex characters')
    try:
        datetime.strptime(args.run_id[:8], '%Y%m%d')
    except ValueError:
        parser.error('run-id must begin with a valid calendar date')
    subscription = '46f7e5d6-05f3-435b-8089-6c63a203dbfa'
    group = 'rg-repoagent-controller-auth'
    scope = f'/subscriptions/{subscription}/resourceGroups/{group}'
    app = 'repoagent-r230-' + args.run_id
    storage = 'ra230' + args.run_id.replace('-', '')
    wrapping = 'restore230-' + args.run_id + '-wrap'
    vault = scope + '/providers/Microsoft.KeyVault/vaults/repoagent-auth-kv'
    account = scope + '/providers/Microsoft.Storage/storageAccounts/' + storage
    identities = scope + '/providers/Microsoft.ManagedIdentity/userAssignedIdentities/'
    print(json.dumps({
        'operation': 'offline-plan', 'cloudAccessed': False, 'runtimeDeployed': False,
        'rbacApplied': False, 'tableState': 'not-initialized',
        'subscriptionId': subscription, 'resourceGroup': group, 'location': 'swedencentral',
        'template': str(Path(__file__).resolve().parents[2] / 'infra/auth/restore-foundation.bicep'),
        'parameters': {'runId': {'value': args.run_id}},
        'resources': {'appName': app, 'storageName': storage, 'vaultName': 'repoagent-auth-kv',
                      'wrappingKeyName': wrapping, 'runtimeIdentityId': identities + app + '-runtime',
                      'cleanupIdentityId': identities + app + '-cleanup',
                      'tableResourceId': account + '/tableServices/default/tables/brokerstate',
                      'keyRingContainerId': account + '/blobServices/default/containers/data-protection',
                      'wrappingKeyResourceId': vault + '/keys/' + wrapping},
        'requiredOperatorChecks': [
            'Verify accepted source and subscription/tenant before any provider request.',
            'Require fresh run-owned names; an existing key returned by ARM is not proof of freshness.',
            'Compile and review Incremental what-if; no shared vault or existing key may be written.',
            'Apply only after review; initialize only the new Table with typed Int64 Add-only quota rows.',
            'Grant runtime read/sign on the exact existing App signing key and read/wrap/unwrap on the new key.',
            'Grant runtime only own key-ring container read/write and own Table; cleanup only own Table.',
            'This foundation creates no runtime, alert route, role assignment, restore or acceptance evidence.',
        ],
    }, indent=2))


if __name__ == '__main__':
    main()
