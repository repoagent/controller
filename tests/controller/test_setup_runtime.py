"""Owned setup behavior on a real filesystem and HTTP listener; no Azure double."""
import importlib.util
import fcntl
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
import urllib.error
import urllib.request

SOURCE = Path(__file__).resolve().parents[2] / 'infra/controller/setup-runtime.py'
spec = importlib.util.spec_from_file_location('setup_runtime', SOURCE)
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)
BINDING = {'version': 1, 'repository': 'owner/repo', 'repositoryId': 123,
           'appId': 456, 'installationId': 789, 'brokerOrigin': 'https://auth.example.org'}


class SetupTests(unittest.TestCase):
    def setUp(self):
        self.share = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.root = self.share / 'runtime'

    def test_fresh_setup_and_reuse_preserve_private_native_data(self):
        setup.initialize(self.share, BINDING)
        # The root-squashed platform must traverse this parent to mount subpaths;
        # it cannot list it or read any private child.
        self.assertEqual(self.root.stat().st_mode & 0o777, 0o711)
        for directory in ('data', 'data/enrollment', 'data/credentials/codex', 'data/history',
                          'data/state', 'data/config/skills', 'data/tools', 'data/control',
                          'data/participant', 'workspace'):
            self.assertEqual((self.root / directory).stat().st_mode & 0o777, 0o700)
        binding = self.root / 'binding.json'
        self.assertEqual(json.loads(binding.read_text()), BINDING)
        self.assertEqual(binding.stat().st_mode & 0o777, 0o600)
        self.assertEqual(os.readlink(self.root / 'data/credentials/codex/sessions'), '/data/history')
        history = self.root / 'data/history/session.jsonl'
        history.write_bytes(b'private native history\n')
        before = binding.read_bytes()
        setup.initialize(self.share, BINDING)
        self.assertEqual(binding.read_bytes(), before)
        self.assertEqual(history.read_bytes(), b'private native history\n')

    def test_different_binding_cannot_repurpose_existing_runtime(self):
        setup.initialize(self.share, BINDING)
        before = (self.root / 'binding.json').read_bytes()
        for field in ('repositoryId', 'appId', 'installationId', 'brokerOrigin'):
            with self.subTest(field=field):
                changed = dict(BINDING, **{field: 999 if field.endswith('Id') else 'https://other.example.org'})
                with self.assertRaisesRegex(ValueError, 'binding'):
                    setup.initialize(self.share, changed)
                self.assertEqual((self.root / 'binding.json').read_bytes(), before)

    def test_unbound_data_is_never_adopted_or_deleted(self):
        self.root.mkdir(mode=0o700)
        retained = self.root / 'retained'
        retained.write_bytes(b'do not replace')
        with self.assertRaisesRegex(ValueError, 'unbound'):
            setup.initialize(self.share, BINDING)
        self.assertEqual(retained.read_bytes(), b'do not replace')
        self.assertFalse((self.root / 'binding.json').exists())

    def test_unsafe_paths_are_rejected_without_changing_targets(self):
        setup.initialize(self.share, BINDING)
        outside = self.share / 'outside'
        outside.mkdir(mode=0o700)
        credentials = self.root / 'data/enrollment'
        credentials.rmdir()
        credentials.symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'private directory'):
            setup.initialize(self.share, BINDING)
        self.assertEqual(list(outside.iterdir()), [])
        self.assertTrue(credentials.is_symlink())

    def test_existing_enrollment_prevents_setup_redeployment(self):
        setup.initialize(self.share, BINDING)
        enrollment = self.root / 'data/enrollment/enrollment.json'
        enrollment.write_bytes(b'synthetic enrollment')
        enrollment.chmod(0o600)
        with self.assertRaisesRegex(ValueError, 'enrolled'):
            setup.initialize(self.share, BINDING)
        self.assertEqual(enrollment.read_bytes(), b'synthetic enrollment')

    def test_initializer_cannot_overlap_another_initializer(self):
        with (self.share / 'runtime.lock').open('w') as owner:
            os.fchmod(owner.fileno(), 0o600)
            fcntl.flock(owner, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaises(BlockingIOError):
                setup.initialize(self.share, BINDING)
            self.assertFalse(self.root.exists())

    def test_broad_permissions_are_not_silently_adopted_or_repaired(self):
        setup.initialize(self.share, BINDING)
        directory = self.root / 'data/credentials'
        directory.chmod(0o755)
        with self.assertRaisesRegex(ValueError, 'private directory'):
            setup.initialize(self.share, BINDING)
        self.assertEqual(directory.stat().st_mode & 0o777, 0o755)

    def test_setup_health_does_not_serve_files_or_claim_controller_readiness(self):
        with setup.http.server.HTTPServer(('127.0.0.1', 0), setup.SetupHealth) as server:
            worker = threading.Thread(target=server.serve_forever)
            worker.start()
            try:
                origin = f'http://127.0.0.1:{server.server_port}'
                with urllib.request.urlopen(origin + '/health/setup', timeout=3) as response:
                    self.assertEqual(json.load(response), {'mode': 'setup-required', 'controllerStarted': False})
                for method, path, status in [
                    ('GET', '/health/ready', 503), ('HEAD', '/health/ready', 503),
                    ('GET', '/data/enrollment/enrollment.json', 404),
                    ('POST', '/data/enrollment/enrollment.json', 404),
                    ('OPTIONS', '/data/enrollment/enrollment.json', 404),
                    ('CUSTOM', '/data/enrollment/enrollment.json', 404),
                    ('GET', '/../../binding.json', 404), ('GET', '/', 404),
                ]:
                    with self.subTest(method=method, path=path):
                        request = urllib.request.Request(origin + path, method=method)
                        with self.assertRaises(urllib.error.HTTPError) as caught:
                            urllib.request.urlopen(request, timeout=3)
                        with caught.exception as response:
                            self.assertEqual(response.code, status)
                            if method == 'HEAD':
                                self.assertEqual(response.read(), b'')
            finally:
                server.shutdown()
                worker.join(timeout=3)
                self.assertFalse(worker.is_alive())


if __name__ == '__main__':
    unittest.main()
