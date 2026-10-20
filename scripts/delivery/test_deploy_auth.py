"""Project-owned guards; CLI doubles do not prove Azure or registry readiness."""
import importlib.util
import os
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('deploy_auth', Path(__file__).with_name('deploy-auth.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DeploymentGuards(unittest.TestCase):
    def setUp(self):
        self.environment = dict(AZURE_CLIENT_ID='11111111-1111-1111-1111-111111111111',
            AZURE_TENANT_ID='22222222-2222-2222-2222-222222222222',
            AZURE_SUBSCRIPTION_ID='33333333-3333-3333-3333-333333333333',
            AUTH_RESOURCE_GROUP='auth-rg', AUTH_APP_NAME='auth-service',
            AUTH_IMAGE_REPOSITORY='ghcr.io/example/auth',
            AUTH_IMAGE='ghcr.io/example/auth@sha256:' + 'a' * 64, AUTH_DEPLOYMENT_READY='true')
        self.enterContext(patch.dict(os.environ, self.environment, clear=True))

    def test_missing_setup_mutable_wrong_repository_and_shell_payloads_are_rejected(self):
        for key, value in [('AUTH_DEPLOYMENT_READY', ''), ('AZURE_SUBSCRIPTION_ID', ''),
                ('AUTH_IMAGE', 'ghcr.io/example/auth:latest'),
                ('AUTH_IMAGE', 'ghcr.io/example/controller@sha256:' + 'a' * 64),
                ('AUTH_APP_NAME', '$(touch /tmp/injected)'),
                ('AUTH_IMAGE', self.environment['AUTH_IMAGE'] + ';echo bad')]:
            with self.subTest(key=key, value=value), patch.dict(os.environ, {key: value}):
                with self.assertRaises(ValueError):
                    module.configuration()

    def test_controller_or_unlabelled_image_is_rejected(self):
        for labels in [{}, {'io.repoagent.role': 'controller'}]:
            with patch.object(module, 'read_json', return_value={'Labels': labels, 'Os': 'linux', 'Architecture': 'amd64'}):
                with self.assertRaises(ValueError):
                    module.inspect_image(module.configuration())

    def test_wrong_app_never_updates(self):
        with patch.object(module, 'read_json', return_value={'id': '/wrong', 'role': 'auth-service'}), \
             patch.object(module.subprocess, 'run') as command:
            with self.assertRaises(ValueError):
                module.deploy(module.configuration())
            command.assert_not_called()

    def test_multi_revision_or_multi_container_target_never_updates(self):
        config = module.configuration()
        identity = f"/subscriptions/{config['AZURE_SUBSCRIPTION_ID']}/resourceGroups/auth-rg/providers/Microsoft.App/containerApps/auth-service"
        for mode, containers in [('Multiple', [dict(name='auth', image='old')]), ('Single', [])]:
            before = dict(id=identity, role='auth-service', mode=mode, containers=containers)
            with patch.object(module, 'read_json', return_value=before), \
                 patch.object(module.subprocess, 'run') as command:
                with self.assertRaises(ValueError):
                    module.deploy(config)
                command.assert_not_called()

    def test_ready_revision_with_exact_image_completes_using_explicit_target(self):
        config = module.configuration()
        before = dict(id=f"/subscriptions/{config['AZURE_SUBSCRIPTION_ID']}/resourceGroups/auth-rg/providers/Microsoft.App/containerApps/auth-service",
                      role='auth-service', mode='Single', containers=[dict(name='auth', image='old')])
        after = dict(before, containers=[dict(name='auth', image=config['AUTH_IMAGE'])], latest='revision', ready='revision')
        with patch.object(module, 'read_json', side_effect=[before, after]), \
             patch.object(module.subprocess, 'run') as command:
            command.return_value.returncode = 0
            module.deploy(config)
            arguments = command.call_args.args[0]
            self.assertEqual(arguments[arguments.index('--image') + 1], config['AUTH_IMAGE'])
            self.assertEqual(arguments[arguments.index('--subscription') + 1], config['AZURE_SUBSCRIPTION_ID'])


if __name__ == '__main__':
    unittest.main()
