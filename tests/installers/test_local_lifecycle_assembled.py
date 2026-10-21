"""Actual Docker lifecycle integration; controlled start never calls a provider."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import uuid

from test_private_runtime import BROKER, STORAGE, frozen_enrollment

ROOT = Path(__file__).resolve().parents[2]
PARENT = os.environ.get('REPOAGENT_LOCAL_LIFECYCLE_IMAGE', '')


@unittest.skipUnless(PARENT and os.name == 'posix', 'Opt-in: local immutable Linux controller image required')
class LocalLifecycleAssembledTests(unittest.TestCase):
    def test_public_start_refuses_durable_disable(self):
        self.assertRegex(PARENT, r'^sha256:[a-f0-9]{64}$')
        instance = 'test97-' + uuid.uuid4().hex[:12]
        name, unrelated = 'repoagent-' + instance, 'test97-unrelated-' + uuid.uuid4().hex[:12]
        image, parent_alias = None, 'repoagent-test97-parent:' + uuid.uuid4().hex
        def docker(*args, data=None, success=True):
            result = subprocess.run(['docker', *args], input=data, text=True, capture_output=True, timeout=90)
            if success:
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            return result
        def cleanup():
            inspected = docker('container', 'inspect', name, success=False)
            if inspected.returncode == 0:
                resource = json.loads(inspected.stdout)[0]
                self.assertEqual(resource['Config']['Labels']['io.repoagent.instance'], instance)
                self.assertEqual(resource['Image'], image)
                docker('stop', '--time', '5', resource['Id'])
                docker('rm', resource['Id'])
            for volume in [name + '-' + storage for storage in STORAGE] + [unrelated]:
                found = docker('volume', 'inspect', volume, success=False)
                if found.returncode == 0:
                    self.assertEqual(json.loads(found.stdout)[0]['Labels']['io.repoagent.instance'], instance)
                    docker('volume', 'rm', volume)
            if image:
                docker('image', 'rm', image)
            tagged = docker('image', 'inspect', '--format', '{{.Id}}', parent_alias, success=False)
            if tagged.returncode == 0:
                self.assertEqual(tagged.stdout.strip(), PARENT)
                docker('image', 'rm', parent_alias)
            print('LIFECYCLE_CLEANUP ' + json.dumps({'instance': instance, 'volumes': 8, 'image': image}), flush=True)
        self.addCleanup(cleanup)
        with tempfile.TemporaryDirectory(prefix='lifecycle97-') as temporary:
            directory = Path(temporary)
            parent = docker('image', 'inspect', '--format', '{{.Id}}', PARENT).stdout.strip()
            self.assertRegex(parent, r'^sha256:[a-f0-9]{64}$')
            original = docker('run', '--rm', '--network', 'none', '--entrypoint', '/usr/bin/sha256sum',
                              parent, '/opt/repoagent/runtime.py').stdout.split()[0]
            self.assertEqual(original, hashlib.sha256((ROOT / 'container/runtime.py').read_bytes()).hexdigest())
            (directory / 'runtime.py').write_text("import runpy,signal,sys\nif sys.argv[1:] == ['start']:\n print(' ready repository=fixture controlled-runtime',flush=True)\n signal.pause()\nelse:\n runpy.run_path('/opt/repoagent/runtime-original.py',run_name='__main__')\n")
            docker('tag', parent, parent_alias)
            (directory / 'Dockerfile').write_text('FROM ' + parent_alias + '\nUSER 0\nRUN mv /opt/repoagent/runtime.py /opt/repoagent/runtime-original.py\nCOPY runtime.py /opt/repoagent/runtime.py\nUSER 1000\n')
            docker('build', '--network', 'none', '--pull=false', '--iidfile', str(directory / 'image-id'), str(directory))
            image = (directory / 'image-id').read_text().strip()
            source = directory / 'enrollment.json'
            source.write_text(json.dumps(frozen_enrollment()))
            source.chmod(0o600)
            environment = dict(os.environ, REPOAGENT_INSTANCE=instance, REPOAGENT_IMAGE=image)
            def public(*arguments):
                return subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'repoagent.ps1'),
                                       *arguments], env=environment, text=True, capture_output=True, timeout=90)
            configured = public('configure', '--repository', 'owner/repository', '--app-id', '123',
                                '--broker-url', BROKER, '--enrollment-file', str(source))
            self.assertEqual(configured.returncode, 0, configured.stdout + configured.stderr)
            for storage in STORAGE:
                labels = json.loads(docker('volume', 'inspect', '--format', '{{json .Labels}}', name + '-' + storage).stdout)
                self.assertEqual(labels, {'io.repoagent.owner': 'private-runtime-v1', 'io.repoagent.instance': instance,
                                         'io.repoagent.storage': storage, 'io.repoagent.image': image})
            marker = {'schemaVersion': 1, 'state': 'disabled', 'instance': instance, 'image': image,
                      'binding': {'repository': 'owner/repository', 'repositoryId': 345, 'deviceId': 'device-1',
                                  'appId': 123, 'installationId': 234, 'brokerOrigin': BROKER, 'profile': 'polling',
                                  'approvalKind': 'repository-agent-variable', 'approvalVariable': 'APPROVED_CONTROLLER',
                                  'expectedValueSha256': 'b' * 64, 'githubEnvironment': None, 'environmentId': None}}
            mount = 'type=volume,src=' + name + '-enrollment,dst=/data/enrollment'
            seed = "import pathlib,sys; p=pathlib.Path('/data/enrollment/disabled.json'); p.write_text(sys.stdin.read()); p.chmod(0o600)"
            docker('run', '--rm', '-i', '--network', 'none', '--user', '1000', '--mount', mount,
                   '--entrypoint', '/usr/bin/python3', image, '-E', '-c', seed, data=json.dumps(marker))
            self.assertEqual(json.loads(docker('run', '--rm', '--network', 'none', '--mount', mount + ',readonly',
                                              '--entrypoint', '/bin/cat', image, '/data/enrollment/disabled.json').stdout), marker)
            docker('volume', 'create', '--label', 'io.repoagent.instance=' + instance, unrelated)
            sentinel = 'type=volume,src=' + unrelated + ',dst=/sentinel'
            docker('run', '--rm', '--network', 'none', '--user', '0', '--mount', sentinel, '--entrypoint', '/bin/sh', image,
                   '-c', 'printf retained > /sentinel/retained')
            started = public('start')
            inspected = docker('container', 'inspect', name, success=False)
            running = inspected.returncode == 0 and json.loads(inspected.stdout)[0]['State']['Running']
            self.assertEqual(docker('run', '--rm', '--network', 'none', '--mount', sentinel + ',readonly',
                                    '--entrypoint', '/bin/cat', image, '/sentinel/retained').stdout, 'retained')
            print('LIFECYCLE_START ' + json.dumps({'parent': parent, 'image': image, 'instance': instance,
                  'returncode': started.returncode, 'running': running, 'output': started.stdout + started.stderr}), flush=True)
            self.assertNotEqual(started.returncode, 0, 'Disabled selected instance launched; running=' + str(running))
            self.assertFalse(running, 'Disabled selected runtime must remain stopped')
            self.assertRegex(started.stdout + started.stderr, '(?i)disabled')
