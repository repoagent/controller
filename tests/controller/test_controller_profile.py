"""Owned persistence policy and secret continuity; real PowerShell, no Azure model."""
import json
from pathlib import Path
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
MODULE = ROOT / 'scripts/azure/controller-profile.psm1'
PRIVATE = 'vers=3.1.1,uid=1000,gid=1000,dir_mode=0700,file_mode=0600,serverino,mfsymlinks'

# These are the policy inputs, not a fake Azure service or replica inventory.
SMB = r'''
$tags=@{nativePersistence='conversation-rollouts'; purpose='controller-setup'}
$private='vers=3.1.1,uid=1000,gid=1000,dir_mode=0700,file_mode=0600,serverino,mfsymlinks'
$paths=@('/data','/workspace','/data/tools','/data/state/bridge.json.github-app/bin','/data/state/bridge.json.github-app/clone-bin')
$mounts=@(foreach($path in $paths) {
    $sub=if($path -eq '/workspace'){'runtime/workspace'}else{'runtime'+$path}
    @{mountPath=$path;subPath=$sub;volumeName=$(if($path -eq '/data'){'private'}else{'executable'})}
})
$template=@{
    containers=@(@{env=@(@{name='CODEX_SQLITE_HOME';value='/tmp/repoagent-codex-sqlite'});args=@('code','serve');volumeMounts=$mounts})
    initContainers=@(@{args=@('code','initialize');volumeMounts=@(@{mountPath='/mnt/controller';volumeName='private'})})
    volumes=@(@{name='private';storageType='AzureFile';storageName='controller-state';mountOptions=$private},
              @{name='executable';storageType='AzureFile';storageName='controller-state';mountOptions=$private.Replace('file_mode=0600','file_mode=0700')})
}
'''


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 required')
class ControllerProfileTests(unittest.TestCase):
    def invoke(self, body, *, fixture=SMB):
        literal = "'" + str(MODULE).replace("'", "''") + "'"
        return subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-Command',
            "$ErrorActionPreference='Stop'; Import-Module " + literal +
            ' -DisableNameChecking; ' + fixture + body], capture_output=True, text=True, timeout=15)

    def test_claimed_conversation_profile_keeps_private_executable_views_and_local_sqlite(self):
        result = self.invoke('Assert-ControllerStorageProfile $tags $template @(); "ACCEPTED"')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('ACCEPTED', result.stdout)
        for change in (
            "$template.volumes[0].mountOptions=$private.Replace('0600','0700');",
            "$template.volumes[1].mountOptions+=',nobrl';",
            "$template.containers[0].env[0].value='/data/sqlite';",
            "$template.containers[0].volumeMounts=$mounts[0..3];",
            "$tags.Remove('nativePersistence');",
            "$tags.nativePersistence='unknown';",
        ):
            with self.subTest(change=change):
                refused = self.invoke(change + 'Assert-ControllerStorageProfile $tags $template @()')
                self.assertNotEqual(refused.returncode, 0, 'Unsafe persistence policy accepted')

    def test_active_profile_requires_the_retained_native_enrollment_mount(self):
        active = r'''
$tags.purpose='controller-active';$template.initContainers[0].args[1]='initialize-active';$template.containers[0].args[1]='activate'
$template.containers[0].volumeMounts+=@{mountPath='/run/enrollment';volumeName='enrollment'}
$template.volumes+=@{name='enrollment';storageType='Secret';secrets=@(@{secretRef='controller-enrollment';path='enrollment.json'})}
$declared=@(@{name='controller-enrollment'})
'''
        result = self.invoke(active + 'Assert-ControllerStorageProfile $tags $template $declared')
        self.assertEqual(result.returncode, 0, result.stderr)
        for change in ("$declared=@();", "$template.volumes[2].secrets[0].path='other.json';",
                       "$template.containers[0].args[1]='serve';"):
            with self.subTest(change=change):
                result = self.invoke(active + change + 'Assert-ControllerStorageProfile $tags $template $declared')
                self.assertNotEqual(result.returncode, 0)

    def test_initializer_uses_the_same_private_share_root(self):
        for change in (
            "$template.volumes+=@{name='foreign';storageType='AzureFile';storageName='other';mountOptions=$private}; $template.initContainers[0].volumeMounts[0].volumeName='foreign';",
            "$template.initContainers[0].volumeMounts[0].volumeName='executable';",
            "$template.initContainers[0].volumeMounts[0].mountPath='/other';",
            "$template.initContainers[0].volumeMounts[0].subPath='other';",
        ):
            with self.subTest(change=change):
                result = self.invoke(change + 'Assert-ControllerStorageProfile $tags $template @()')
                self.assertNotEqual(result.returncode, 0, 'Initializer can target another persistent root')

    def test_legacy_nfs_still_requires_versioned_managed_identity_secret_references(self):
        fixture = r'''
$tags=@{};$template=@{containers=@(@{volumeMounts=@(@{mountPath='/data';subPath='runtime/data';volumeName='state'},
@{mountPath='/workspace';subPath='runtime/workspace';volumeName='state'})});volumes=@(@{name='state';storageType='NfsAzureFile'})}
$secret=@{name='controller-enrollment';keyVaultUrl='https://example.vault.azure.net/secrets/controller-enrollment/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';identity='System'}
'''
        accepted = self.invoke('Assert-ControllerStorageProfile $tags $template @($secret)', fixture=fixture)
        self.assertEqual(accepted.returncode, 0, accepted.stderr)
        for change in ("$secret.Remove('identity');", "$secret.keyVaultUrl='https://example.vault.azure.net/secrets/controller-enrollment';",
                       "$secret=@{name='controller-enrollment'};"):
            with self.subTest(change=change):
                refused = self.invoke(change + 'Assert-ControllerStorageProfile $tags $template @($secret)', fixture=fixture)
                self.assertNotEqual(refused.returncode, 0)

    def test_native_value_changes_affect_fingerprint_without_disclosing_values(self):
        result = self.invoke(r'''
$declared=@(@{name='controller-enrollment'})
$a=Get-NativeEnrollmentFingerprint $declared @(@{name='controller-enrollment';value='PRIVATE_CANARY_A'})
$same=Get-NativeEnrollmentFingerprint $declared @(@{name='controller-enrollment';value='PRIVATE_CANARY_A'})
$b=Get-NativeEnrollmentFingerprint $declared @(@{name='controller-enrollment';value='PRIVATE_CANARY_B'})
@{same=($a -ceq $same);changed=($a -cne $b);fingerprint=$a}|ConvertTo-Json -Compress
''', fixture='')
        self.assertEqual(result.returncode, 0, result.stderr)
        value = json.loads(result.stdout)
        self.assertTrue(value['same'])
        self.assertTrue(value['changed'])
        self.assertRegex(value['fingerprint'], r'^[0-9a-f]{64}$')
        self.assertNotIn('PRIVATE_CANARY', result.stdout + result.stderr)
        for inventory in ("@()", "@(@{name='other';value='PRIVATE_CANARY_A'})",
                          "@(@{name='controller-enrollment';value=''})",
                          "@(@{name='controller-enrollment';value='PRIVATE_CANARY_A';identity='System'})",
                          "@(@{name='controller-enrollment';value='PRIVATE_CANARY_A'},@{name='controller-enrollment';value='PRIVATE_CANARY_B'})"):
            with self.subTest(inventory=inventory):
                refused = self.invoke("Get-NativeEnrollmentFingerprint @(@{name='controller-enrollment'}) " + inventory, fixture='')
                self.assertNotEqual(refused.returncode, 0)
                self.assertNotIn('PRIVATE_CANARY', refused.stdout + refused.stderr)


if __name__ == '__main__':
    unittest.main()
