"""Actual Docker lifecycle integration; controlled start never calls a provider."""
import hashlib
import json
import os
import shutil
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
    def setUp(self):
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
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='lifecycle97-')))
        parent = docker('image', 'inspect', '--format', '{{.Id}}', PARENT).stdout.strip()
        self.assertRegex(parent, r'^sha256:[a-f0-9]{64}$')
        self.assertTrue(json.loads(docker('image', 'inspect', parent).stdout)[0]['RepoTags'], 'Parent must retain an independent existing tag')
        original = docker('run', '--rm', '--network', 'none', '--entrypoint', '/usr/bin/sha256sum',
                          parent, '/opt/repoagent/runtime.py').stdout.split()[0]
        self.assertEqual(original, hashlib.sha256((ROOT / 'container/runtime.py').read_bytes()).hexdigest())
        (directory / 'runtime.py').write_text("import runpy,signal,sys\nif sys.argv[1:] == ['start']:\n print(' ready repository=fixture controlled-runtime',flush=True)\n signal.pause()\nelse:\n runpy.run_path('/opt/repoagent/runtime-original.py',run_name='__main__')\n")
        docker('tag', parent, parent_alias)
        (directory / 'Dockerfile').write_text('FROM ' + parent_alias + '\nUSER 0\nRUN mv /opt/repoagent/runtime.py /opt/repoagent/runtime-original.py\nCOPY --chmod=0644 runtime.py /opt/repoagent/runtime.py\nUSER 1000\n')
        docker('build', '--network', 'none', '--pull=false', '--iidfile', str(directory / 'image-id'), str(directory))
        image = (directory / 'image-id').read_text().strip()
        source = directory / 'enrollment.json'
        source.write_text(json.dumps(frozen_enrollment()))
        source.chmod(0o600)
        gh = directory / 'gh'
        shutil.copyfile(Path(__file__).with_name('gh_fixture.py'), gh)
        gh.chmod(0o755)
        environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'], REPOAGENT_INSTANCE=instance, REPOAGENT_IMAGE=image)
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
        docker('volume', 'create', '--label', 'io.repoagent.instance=' + instance, unrelated)
        sentinel = 'type=volume,src=' + unrelated + ',dst=/sentinel'
        docker('run', '--rm', '--network', 'none', '--user', '0', '--mount', sentinel, '--entrypoint', '/bin/sh', image,
               '-c', 'printf retained > /sentinel/retained')
        self.docker, self.public, self.directory, self.environment = docker, public, directory, environment
        self.name, self.image, self.instance, self.parent = name, image, instance, parent
        self.marker, self.mount, self.sentinel, self.unrelated = marker, mount, sentinel, unrelated

    def launch_state(self, pending='none'):
        settings = json.loads(self.files("from pathlib import Path; print(Path('/data/config/settings.json').read_text())"))
        launch = dict(binding=self.marker['binding'], settings=settings)
        return dict(schemaVersion=1, instance=self.instance, volumes={part: self.name + '-' + part for part in STORAGE},
                    storageOriginImage=self.image, currentImage=self.image, currentLaunch=launch,
                    priorImage=self.image, priorLaunch=launch, targetImage=self.image, targetLaunch=launch,
                    pending=pending, operationId=uuid.uuid4().hex, revision=1)

    def write_launch_state(self, record):
        self.files("from pathlib import Path; p=Path('/data/enrollment/launch-state.json'); p.write_text(" + repr(json.dumps(record)) + "); p.chmod(0o600)")

    def test_interrupted_update_refuses_normal_start(self):
        self.write_launch_state(self.launch_state('update'))
        started = self.public('start')
        self.assertNotEqual(started.returncode, 0, 'Pending update launched native work: ' + started.stdout + started.stderr)
        self.assertIn('pending', (started.stdout + started.stderr).lower())
        self.assertNotEqual(self.docker('container', 'inspect', self.name, success=False).returncode, 0)

    def test_public_start_refuses_durable_disable(self):
        docker, public, mount, marker = self.docker, self.public, self.mount, self.marker
        image, name, parent, instance, sentinel = self.image, self.name, self.parent, self.instance, self.sentinel
        seed = "import pathlib,sys; p=pathlib.Path('/data/enrollment/disabled.json'); p.write_text(sys.stdin.read()); p.chmod(0o600)"
        docker('run', '--rm', '-i', '--network', 'none', '--user', '1000', '--mount', mount,
               '--entrypoint', '/usr/bin/python3', image, '-E', '-c', seed, data=json.dumps(marker))
        self.assertEqual(json.loads(docker('run', '--rm', '--network', 'none', '--mount', mount + ',readonly',
                                          '--entrypoint', '/bin/cat', image, '/data/enrollment/disabled.json').stdout), marker)
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

    def files(self, code):
        mounts = [argument for storage in STORAGE for argument in ('--mount', 'type=volume,src=' + self.name + '-' + storage + ',dst=/data/' + storage)]
        mounts += ['--mount', 'type=volume,src=' + self.unrelated + ',dst=/data/unrelated,readonly']
        return self.docker('run', '--rm', '--network', 'none', '--user', '1000', *mounts,
                           '--entrypoint', '/usr/bin/python3', self.image, '-E', '-c', code).stdout

    def uninstall(self, state=None, apply=True):
        arguments = ['-Action', 'Uninstall', '-Instance', self.instance, '-StateDirectory', str(state or self.directory / 'host'),
                     '-Repository', 'owner/repository', '-ExpectedRepositoryId', '345', '-DeviceId', 'device-1',
                     '-AppId', '123', '-InstallationId', '234', '-BrokerOrigin', BROKER, '-EnrollmentProfile', 'polling',
                     '-ApprovalVariable', 'APPROVED_CONTROLLER', '-ExpectedValueSha256', self.marker['binding']['expectedValueSha256'],
                     '-NonInteractive'] + (['-Apply'] if apply else [])
        result = subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'install.ps1'), *arguments],
                                env=self.environment, text=True, capture_output=True, timeout=90)
        self.assertNotIn(frozen_enrollment()['deviceSecret'], result.stdout + result.stderr)
        outcomes = [json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')]
        self.assertEqual(len(outcomes), 1, result.stdout + result.stderr)
        return result, outcomes[0]

    def test_remote_denial_still_disables_clears_and_preserves_retry(self):
        self.deny_remote()
        host = self.directory / 'host'
        host.mkdir(mode=0o700)
        for filename, data in [('enrollment.json', json.dumps(frozen_enrollment())), ('recovery.json', '{corrupt')]:
            (host / filename).write_text(data)
            (host / filename).chmod(0o600)
        targets = ['/data/enrollment/enrollment.json', '/data/credentials/codex/auth.json',
                   '/data/state/bridge.json.github-app/installation-token.json', '/data/state/bridge.json.github-app/installation-token.json.tmp']
        retained = ['/data/config/settings.json', '/data/state/bridge.json', '/data/workspace/retained',
                    '/data/history/retained', '/data/tools/retained', '/data/credentials/codex/config.toml', '/data/unrelated/retained']
        self.files("from pathlib import Path; paths=" + repr(targets[1:] + retained[1:5]) +
                   "; [(p.parent.mkdir(parents=True,exist_ok=True),p.write_text('retained-or-synthetic')) for p in map(Path,paths)]")
        before = json.loads(self.files("import json,hashlib; from pathlib import Path; print(json.dumps({p:hashlib.sha256(Path(p).read_bytes()).hexdigest() for p in " + repr(retained) + "}))"))
        started = self.public('start')
        self.assertEqual(started.returncode, 0, started.stdout + started.stderr)
        result, outcome = self.uninstall()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(outcome['hostRecovery'], 'protected')
        self.assertEqual((host / 'recovery.json').read_text(), '{corrupt')
        self.assertEqual(json.loads((host / 'enrollment.json').read_text()), frozen_enrollment())
        self.assertEqual((outcome['localLaunch'], outcome['localRuntime'], outcome['localCredentials']), ('disabled', 'removed', 'cleared'))
        self.assertEqual((outcome['result'], outcome['revoke']['brokerClosure'], outcome['selfRevoke']), ('partial', 'unconfirmed', 'not-attempted'))
        self.assertFalse(any(json.loads(self.files("import json; from pathlib import Path; print(json.dumps([Path(p).exists() for p in " + repr(targets) + "]))"))))
        after = json.loads(self.files("import json,hashlib; from pathlib import Path; print(json.dumps({p:hashlib.sha256(Path(p).read_bytes()).hexdigest() for p in " + repr(retained) + "}))"))
        self.assertEqual(before, after)
        self.assertEqual(json.loads(self.files("from pathlib import Path; print(Path('/data/enrollment/disabled.json').read_text())")), self.marker)
        methods = [json.loads(line)['argv'][4] for line in self.log.read_text().splitlines()]
        self.assertEqual((methods, json.loads(self.log.read_text().splitlines()[-1])['argv'][-1]), (['GET', 'GET', 'DELETE'], 'repos/owner/repository/agents/variables/APPROVED_CONTROLLER'))
        for arguments in [('start',), ('restart',), ('import-codex-auth', str(self.directory / 'enrollment.json'))]:
            refused = self.public(*arguments)
            self.assertNotEqual(refused.returncode, 0)
            self.assertIn('disabled', refused.stdout + refused.stderr)
        self.assertEqual(json.loads((self.directory / 'enrollment.json').read_text()), frozen_enrollment())
        refused = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'install.ps1'), '-Action', 'Install', '-Apply',
                                  '-Instance', self.instance, '-StateDirectory', str(self.directory / 'fresh-host')], env=self.environment, capture_output=True, text=True, timeout=30)
        self.assertIn('disabled', refused.stdout + refused.stderr)
        stopped = self.public('stop')
        self.assertEqual(stopped.returncode, 0, stopped.stdout + stopped.stderr)
        retried, retry = self.uninstall(state=self.directory / 'alternate-host')
        self.assertEqual((retry['localLaunch'], retry['localCredentials']), ('disabled', 'cleared'))

    def deny_remote(self):
        value = 'synthetic-approval'
        self.marker['binding']['expectedValueSha256'] = hashlib.sha256(value.encode()).hexdigest()
        scenario, self.log = self.directory / 'scenario.json', self.directory / 'gh.jsonl'
        scenario.write_text(json.dumps([{'body': {'id': 345, 'full_name': 'owner/repository'}},
                                       {'body': {'name': 'APPROVED_CONTROLLER', 'value': value}}, {'status': 403}]))
        self.environment.update(REVOKE_GH_SCENARIO=str(scenario), REVOKE_GH_LOG=str(self.log), GH_TOKEN='SYNTHETIC-ONLY')

    def test_unsafe_clear_parents_and_hardlinks_preserve_external_bytes(self):
        self.deny_remote()
        self.files("from pathlib import Path; import os; c=Path('/data/credentials/codex'); (c/'outside').write_text('protected'); os.link(c/'outside',c/'auth.json'); s=Path('/data/state'); (s/'outside').mkdir(); (s/'outside/installation-token.json').write_text('protected'); (s/'bridge.json.github-app').symlink_to(s/'outside',target_is_directory=True)")
        result, outcome = self.uninstall()
        self.assertEqual((outcome['localLaunch'], outcome['localCredentials']), ('disabled', 'partial'))
        self.assertEqual(self.files("from pathlib import Path; print(Path('/data/credentials/codex/outside').read_text()+Path('/data/state/outside/installation-token.json').read_text())").strip(), 'protectedprotected')
        self.assertEqual(self.files("from pathlib import Path; print(Path('/data/enrollment/enrollment.json').exists())").strip(), 'False')
        self.files("from pathlib import Path; Path('/data/enrollment/disabled.json').write_text('{corrupt')")
        refused = self.public('start')
        self.assertNotEqual(refused.returncode, 0)

    def test_disable_write_failure_stops_but_retains_container_and_credentials(self):
        self.assertEqual(self.public('start').returncode, 0)
        self.files("from pathlib import Path; Path('/data/enrollment').chmod(0o555)")
        result, outcome = self.uninstall()
        self.assertEqual((outcome['localLaunch'], outcome['localCredentials']), ('unconfirmed', 'retained'))
        resource = json.loads(self.docker('container', 'inspect', self.name).stdout)[0]
        self.assertFalse(resource['State']['Running'])
        self.assertEqual(self.files("from pathlib import Path; print(Path('/data/enrollment/enrollment.json').exists())").strip(), 'True')

    def test_stop_and_remove_transport_failures_remain_independent_partial_results(self):
        self.deny_remote()
        self.assertEqual(self.public('start').returncode, 0)
        transport = self.directory / 'docker'
        transport.write_text('#!/bin/sh\nif [ "$1" = "$FAIL_COMMAND" ]; then exit 73; fi\nexec ' + shutil.which('docker') + ' "$@"\n')
        transport.chmod(0o755)
        for failure, runtime, credentials in [('stop', 'possibly-running', 'retained'), ('rm', 'unconfirmed', 'cleared')]:
            with self.subTest(failure=failure):
                self.environment['FAIL_COMMAND'] = failure
                result, outcome = self.uninstall()
                self.assertEqual((outcome['localLaunch'], outcome['localRuntime'], outcome['localCredentials']), ('disabled', runtime, credentials))
                resource = json.loads(self.docker('container', 'inspect', self.name).stdout)[0]
                self.assertEqual(resource['State']['Running'], failure == 'stop')

    def test_acknowledged_guided_host_copy_clears_without_changing_journal(self):
        self.deny_remote()
        host = self.directory / 'host'
        host.mkdir(mode=0o700)
        recovery = dict(version=2, setupVersion=2, approvalKind='repository-agent-variable', secret=None,
                        completionId='c' * 32, lastWorkAt=None, acknowledged=True, ackAttempted=True,
                        request=dict(idempotencyId='d' * 32, repositoryId=345, repository='owner/repository',
                                     variable='APPROVED_CONTROLLER', profile='polling'))
        records = {'recovery.json': recovery, 'enrollment.json': frozen_enrollment(),
                   'launch.json': dict(instance=self.instance, runtimeImageId=self.image, repository='owner/repository')}
        for filename, value in records.items():
            (host / filename).write_text(json.dumps(value))
            (host / filename).chmod(0o600)
        _, outcome = self.uninstall()
        self.assertEqual(outcome['hostRecovery'], 'cleared')
        self.assertFalse((host / 'enrollment.json').exists())
        self.assertEqual(json.loads((host / 'recovery.json').read_text()), recovery)
        self.assertEqual(json.loads((host / 'launch.json').read_text()), records['launch.json'])
