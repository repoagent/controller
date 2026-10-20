"""Publication decisions through real PowerShell and the Docker executable boundary.

Windows builds a native executable using the existing GhFixture launcher. Docker
transport alone is scripted; these tests do not prove registry compatibility.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
IMAGE = 'sha256:' + 'a' * 64
DIGEST = 'sha256:' + 'b' * 64
OTHER = 'sha256:' + 'c' * 64
OCI = 'application/vnd.oci.image.manifest.v1+json'
DOCKER = 'application/vnd.docker.distribution.manifest.v2+json'
REPOSITORY = 'ghcr.io/example/controller'
TAG = 'reviewed_2026-10-20.1'
CANARY = 'PROVIDER-CREDENTIAL-MUST-NOT-APPEAR'


def identity(**changes):
    return dict(id=IMAGE, os='linux', architecture='amd64', **changes) if not changes else {
        **{'id': IMAGE, 'os': 'linux', 'architecture': 'amd64'}, **changes}


def publication(media=OCI):
    # Official single-image Buildx .Manifest shape: descriptor, no schemaVersion.
    return [{'body': identity()}, {'body': identity()}, {}, {'body': identity()}, {},
            {'body': {'mediaType': media, 'digest': DIGEST, 'size': 123}},
            {'body': {'schemaVersion': 2, 'mediaType': media, 'config': {'digest': IMAGE}}}]


class ImagePublicationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which('pwsh'):
            if os.name == 'nt':
                raise RuntimeError('Native Windows acceptance requires PowerShell 7')
            raise unittest.SkipTest('PowerShell 7 required')
        cls.native = os.name == 'nt' or os.environ.get('PUBLISH_DOCKER_NATIVE_FIXTURE') == '1'
        if cls.native:
            temporary = tempfile.TemporaryDirectory(prefix='native docker fixture ')
            cls.addClassCleanup(temporary.cleanup)
            build = Path(temporary.name)
            shutil.copytree(Path(__file__).with_name('GhFixture'), build / 'project')
            (build / 'global.json').write_text(json.dumps({'sdk': {
                'version': '10.0.100', 'rollForward': 'latestFeature', 'allowPrerelease': False}}))
            cls.fixture_build = build / 'output'
            result = subprocess.run(['dotnet', 'build', str(build / 'project/GhFixture.csproj'),
                                     '-p:AssemblyName=docker', '--output', str(cls.fixture_build),
                                     '--nologo', '--verbosity', 'quiet', '--ignore-failed-sources'],
                                    cwd=build, text=True, capture_output=True, timeout=120)
            if result.returncode:
                raise RuntimeError(result.stdout + result.stderr)

    def invoke(self, *, scenario=None, destination='GHCR', repository=REPOSITORY,
               tag=TAG, image=IMAGE, apply=False):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory(prefix='publication boundary ')))
        fixture = Path(__file__).with_name('publication_fixture.py')
        if self.native:
            for artifact in self.fixture_build.iterdir():
                shutil.copy2(artifact, directory / artifact.name)
        else:
            shutil.copyfile(fixture, directory / 'docker')
            (directory / 'docker').chmod(0o755)
        script = directory / 'scenario.json'
        script.write_text(json.dumps(publication() if scenario is None else scenario), encoding='utf-8')
        log = directory / 'requests.jsonl'
        environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'],
                           PUBLISH_SCENARIO=str(script), PUBLISH_LOG=str(log),
                           REVOKE_GH_PYTHON=sys.executable, REVOKE_GH_FIXTURE=str(fixture.resolve()),
                           PYTHONUTF8='1')
        self.assertEqual(Path(shutil.which('docker', path=environment['PATH'])).resolve(),
                         (directory / ('docker.exe' if os.name == 'nt' else 'docker')).resolve())
        command = ['pwsh', '-NoLogo', '-NoProfile', '-File', str(ROOT / 'scripts/publish-image.ps1')]
        for name, value in [('ImageId', image), ('Destination', destination),
                            ('Repository', repository), ('Tag', tag)]:
            if value is not None:
                command += ['-' + name, value]
        if apply:
            command.append('-Apply')
        started = time.monotonic()
        result = subprocess.run(command, cwd=directory, env=environment, text=True,
                                capture_output=True, timeout=40)
        elapsed = time.monotonic() - started
        self.assertNotIn(CANARY, result.stdout + result.stderr)
        outcome = json.loads(result.stdout)
        events = [json.loads(line)['argv'] for line in log.read_text().splitlines()] if log.exists() else []
        return result, outcome, events, elapsed

    def assert_unverified(self, result, outcome, *, changed=False):
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(outcome['status'], 'Unverified')
        self.assertIsNone(outcome['immutableReference'])
        self.assertFalse(outcome['remoteVerified'])
        self.assertFalse(outcome['runtimePullAuthorized'])
        self.assertFalse(outcome['azureDeploymentReady'])
        self.assertEqual(outcome['publicationMayHaveChanged'], changed)

    def test_explicit_identity_destination_and_safe_registry_inputs_required_before_docker(self):
        cases = [dict(image=None), dict(destination=None), dict(image=IMAGE + '\n'),
                 dict(destination='ghcr'), dict(repository=None), dict(tag=None),
                 dict(repository='docker.io/example/controller'), dict(repository='ghcr.io/controller'),
                 dict(repository='https://ghcr.io/example/controller'),
                 dict(repository='ghcr.io/Example/controller'), dict(repository=REPOSITORY + '@' + DIGEST),
                 dict(tag='--help'), dict(tag='v1\n'), dict(tag='$(touch INJECTED)'),
                 dict(destination='Local'), dict(image='sha256:short')]
        for case in cases:
            with self.subTest(case=case):
                result, outcome, events, _ = self.invoke(apply=True, **case)
                self.assert_unverified(result, outcome)
                self.assertEqual(events, [])

    def test_registry_plan_only_inspects_local_identity_and_explains_apply(self):
        for destination, repository in [('DockerHub', 'docker.io/my-org/controller'),
                                        ('GHCR', REPOSITORY), ('ACR', 'myregistry.azurecr.io/controller')]:
            with self.subTest(destination=destination):
                result, outcome, events, _ = self.invoke(destination=destination, repository=repository)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(outcome['status'], 'Planned')
                self.assertEqual(outcome['targetReference'], repository + ':' + TAG)
                self.assertIsNone(outcome['immutableReference'])
                self.assertEqual(len(events), 1)
                self.assertEqual(events[0][:3], ['image', 'inspect', '--format'])
                self.assertEqual(events[0][-1], IMAGE)
                self.assertIn('overwritten', ' '.join(outcome['plan']))
                self.assertIn('authority', ' '.join(outcome['plan']))

    def test_local_even_with_apply_never_tags_pushes_or_queries_remote(self):
        result, outcome, events, _ = self.invoke(destination='Local', repository=None, tag=None, apply=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(outcome['status'], 'LocalVerified')
        self.assertEqual(outcome['imageId'], IMAGE)
        self.assertFalse(outcome['azureDeploymentReady'])
        self.assertEqual(len(events), 1)
        self.assertEqual(events[0][:2], ['image', 'inspect'])

    def test_apply_preserves_arguments_and_returns_only_config_verified_immutable_reference(self):
        for media in [OCI, DOCKER]:
            with self.subTest(media=media):
                scenario = publication(media)
                scenario[4] = {'body': CANARY, 'stderr': CANARY}
                result, outcome, events, _ = self.invoke(apply=True, scenario=scenario)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(outcome['status'], 'PublishedVerified')
                self.assertEqual(outcome['imageId'], IMAGE)
                self.assertEqual(outcome['immutableReference'], REPOSITORY + '@' + DIGEST)
                self.assertTrue(outcome['remoteVerified'])
                self.assertFalse(outcome['runtimePullAuthorized'])
                self.assertFalse(outcome['azureDeploymentReady'])
                target = REPOSITORY + ':' + TAG
                self.assertEqual([event[-1] for event in events[:2]], [IMAGE, IMAGE])
                self.assertEqual(events[2], ['image', 'tag', IMAGE, target])
                self.assertEqual(events[3][-1], target)
                self.assertEqual(events[4], ['image', 'push', target])
                self.assertEqual(events[5], ['buildx', 'imagetools', 'inspect', target,
                                             '--format', '{{json .Manifest}}'])
                self.assertEqual(events[6], ['buildx', 'imagetools', 'inspect',
                                             REPOSITORY + '@' + DIGEST, '--raw'])
                self.assertEqual(len(events), 7)

    def test_invalid_local_identity_platform_or_replaced_tag_stops_before_push(self):
        for position, body in [(0, identity(id=[IMAGE])), (0, identity(os='windows')),
                               (0, identity(architecture='arm64')), (0, [identity()]),
                               (1, identity(id=OTHER)), (3, identity(id=OTHER))]:
            with self.subTest(position=position, body=body):
                scenario = publication()
                scenario[position] = {'body': body}
                result, outcome, events, _ = self.invoke(apply=True, scenario=scenario)
                self.assert_unverified(result, outcome)
                self.assertFalse(any(event[:2] == ['image', 'push'] for event in events))

    def test_remote_descriptor_rejects_index_malformed_ambiguous_and_nonscalar_digests(self):
        descriptor = publication()[5]['body']
        cases = ['{malformed', [], {**descriptor, 'digest': [DIGEST]},
                 {**descriptor, 'digest': DIGEST + '\n'}, {**descriptor, 'mediaType': [OCI]},
                 {**descriptor, 'mediaType': 'application/vnd.oci.image.index.v1+json'},
                 {**descriptor, 'manifests': []},
                 '{"mediaType":"' + OCI + '","digest":"' + DIGEST + '","digest":"' + OTHER + '"}']
        for body in cases:
            with self.subTest(body=body):
                scenario = publication()
                scenario[5] = {'body': body}
                result, outcome, events, _ = self.invoke(apply=True, scenario=scenario)
                self.assert_unverified(result, outcome, changed=True)
                self.assertEqual(len(events), 6)

    def test_immutable_manifest_must_be_single_supported_image_with_exact_scalar_config_id(self):
        manifest = publication()[6]['body']
        for body in [{**manifest, 'config': {'digest': OTHER}},
                     {**manifest, 'config': {'digest': [IMAGE]}},
                     {**manifest, 'config': {'digest': IMAGE + '\n'}},
                     {**manifest, 'schemaVersion': '2'}, {**manifest, 'mediaType': 'unknown'},
                     {**manifest, 'manifests': []}, '[{}]', '{} trailing']:
            with self.subTest(body=body):
                scenario = publication()
                scenario[6] = {'body': body}
                result, outcome, events, _ = self.invoke(apply=True, scenario=scenario)
                self.assert_unverified(result, outcome, changed=True)
                self.assertEqual(len(events), 7)

    def test_cli_failures_suppress_provider_output_and_never_retry_or_repair(self):
        for position in range(7):
            with self.subTest(position=position):
                scenario = publication()
                scenario[position] = {'body': CANARY, 'stderr': CANARY, 'exit': 23}
                result, outcome, events, _ = self.invoke(apply=True, scenario=scenario)
                self.assert_unverified(result, outcome, changed=position >= 4)
                self.assertEqual(len(events), position + 1)

    def test_combined_output_limit_terminates_a_still_running_command_without_publishing(self):
        scenario = [{'stdout_bytes': 600000, 'stderr_bytes': 600000, 'delay': 60}]
        result, outcome, events, elapsed = self.invoke(apply=True, scenario=scenario)
        self.assert_unverified(result, outcome)
        self.assertEqual(len(events), 1)
        self.assertLess(elapsed, 15, 'Output budget must terminate without waiting for provider exit')


if __name__ == '__main__':
    unittest.main()
