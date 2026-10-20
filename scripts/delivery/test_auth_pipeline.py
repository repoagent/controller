"""Real delivery CLI/shell processes with private source fixtures and provider doubles.

Fixtures satisfy only the source/infra entrypoint contract. They are not a working
service, Docker engine, registry, Azure deployment or hosted Actions acceptance.
"""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name('auth-pipeline.py')
WORKFLOW = SCRIPT.parents[2] / '.github/workflows/ci-auth-service.yml'
REPOSITORY = 'ghcr.io/repoagent/controller-auth'
SHA = 'b' * 40
TAG = REPOSITORY + ':sha-' + SHA
IMAGE = REPOSITORY + '@sha256:' + 'a' * 64
CANARY = 'fake-token-$(touch token-was-executed);keep-private'
SOURCE_FILES = ('service/auth/global.json', 'service/auth/verify.sh', 'service/auth/Dockerfile',
                'infra/auth/validate.sh', 'infra/auth/deploy.sh', 'infra/auth/main.bicep')
CHECK = ['infra/auth/deploy.sh', 'check']
METADATA = ['gh', 'api', 'users/repoagent/packages/container/controller-auth']
BUILD = ['docker', 'build', '--platform', 'linux/amd64', '--label',
         'org.opencontainers.image.revision=' + SHA, '--tag', TAG, 'service/auth']
INSPECT = ['docker', 'image', 'inspect', TAG]
PUSH = ['docker', 'push', TAG]
ATTEST = ['skopeo', 'inspect', '--no-creds', '--config', '--override-os', 'linux',
          '--override-arch', 'amd64', 'docker://' + IMAGE]

# An external transcript sink: it implements no image or readiness decisions.
DOUBLE = '''import json, os, pathlib, sys
name = pathlib.Path(sys.argv[0]).name
argv = ['infra/auth/deploy.sh', *sys.argv[1:]] if name == 'infra-double.py' else [name, *sys.argv[1:]]
ledger = pathlib.Path(os.environ['TEST_LEDGER'])
previous = ledger.read_text().splitlines() if ledger.exists() else []
entry = dict(argv=argv, stdin=sys.stdin.read() if argv[1:2] == ['login'] else '',
             gh_token=os.environ.get('GH_TOKEN', ''))
with ledger.open('a') as stream:
    stream.write(json.dumps(entry) + '\\n')
transcript = json.loads(pathlib.Path(os.environ['TEST_TRANSCRIPT']).read_text())
index = len(previous)
if index >= len(transcript) or argv != transcript[index]['argv']:
    sys.exit('Unexpected provider command: ' + json.dumps(argv))
step = transcript[index]
if step.get('error'):
    sys.exit(step['error'])
if step.get('response') is not None:
    print(json.dumps(step['response']))
'''


def local_image(**changes):
    return dict(dict(Config=dict(Labels={'io.repoagent.role': 'auth-service'}, User='10001:10001'),
                     Os='linux', Architecture='amd64'), **changes)


def remote_image(**changes):
    return dict(dict(config=dict(Labels={'io.repoagent.role': 'auth-service'}, User='10001:10001'),
                     os='linux', architecture='amd64'), **changes)


def step(argv, response=None, error=None):
    return dict(argv=argv, response=response, error=error)


def publication(local=None, digests=None, remote=None):
    return [step(CHECK), step(METADATA, {'visibility': 'public'}), step(BUILD),
            step(INSPECT, [local if local is not None else local_image()]), step(PUSH),
            step(INSPECT, [local_image(RepoDigests=digests if digests is not None else [IMAGE])]),
            step(ATTEST, remote if remote is not None else remote_image())]


class AuthPipelineCliTests(unittest.TestCase):
    def invoke(self, verb, transcript=(), *, missing=None, sdk='10.0.100', environment=None, shell=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = root / 'scripts/delivery/auth-pipeline.py'
            script.parent.mkdir(parents=True)
            script.write_text(SCRIPT.read_text())
            for name in SOURCE_FILES:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('# Contract fixture only; not a service implementation.\n')
            (root / SOURCE_FILES[0]).write_text(json.dumps(dict(sdk=dict(version=sdk))))
            provider = root / 'infra-double.py'
            provider.write_text(DOUBLE)
            (root / 'infra/auth/deploy.sh').write_text(f'exec {sys.executable} "{provider}" "$@"\n')
            if missing:
                (root / missing).unlink()
            binaries = root / 'bin'
            binaries.mkdir()
            for name in ['gh', 'docker', 'skopeo']:
                path = binaries / name
                path.write_text(f'#!{sys.executable}\n' + DOUBLE)
                path.chmod(0o700)
            (binaries / 'bash').symlink_to(shutil.which('bash'))
            ledger, fixture, output = root / 'calls.jsonl', root / 'transcript.json', root / 'output'
            fixture.write_text(json.dumps(transcript))
            output.write_text('existing=value\n')
            env = dict(PATH=str(binaries), HOME=str(root), LANG='C.UTF-8', GH_TOKEN=CANARY,
                       TEST_LEDGER=str(ledger), TEST_TRANSCRIPT=str(fixture), GITHUB_OUTPUT=str(output),
                       AUTH_IMAGE_REPOSITORY=REPOSITORY, AUTH_IMAGE=IMAGE,
                       AUTH_DEPLOYMENT_READY='true', GITHUB_SHA=SHA,
                       AZURE_CLIENT_ID='11111111-1111-1111-1111-111111111111',
                       AZURE_TENANT_ID='22222222-2222-2222-2222-222222222222',
                       AZURE_SUBSCRIPTION_ID='33333333-3333-3333-3333-333333333333',
                       REGISTRY_TOKEN=CANARY, REGISTRY_USER='fixture-bot')
            env.update(environment or {})
            command = [str(binaries / 'bash'), '-e', '-c', shell] if shell else [sys.executable, '-B', str(script), verb]
            result = subprocess.run(command, cwd=root, env=env, capture_output=True, text=True, timeout=5)
            calls = [json.loads(line) for line in ledger.read_text().splitlines()] if ledger.exists() else []
            return result, calls, output.read_text(), (root / 'token-was-executed').exists()

    def test_missing_or_unsupported_service_source_fails_without_provider_calls(self):
        for missing in SOURCE_FILES:
            with self.subTest(missing=missing):
                result, calls, output, _ = self.invoke('publish', missing=missing)

                self.assertEqual(result.returncode, 1)
                self.assertIn('Missing ' + missing, result.stderr)
                self.assertEqual((calls, output), ([], 'existing=value\n'))
        for sdk in ['11.0.100', '10.0.100-preview.1', '']:
            with self.subTest(sdk=sdk):
                result, calls, output, _ = self.invoke('source', sdk=sdk)

                self.assertEqual(result.returncode, 1)
                self.assertIn('must pin a stable .NET 10 SDK', result.stderr)
                self.assertEqual((calls, output), ([], 'existing=value\n'))

    def test_stable_dotnet10_source_contract_is_accepted_without_publication(self):
        result, calls, output, _ = self.invoke('source')

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((calls, output), ([], 'existing=value\n'))

    def test_publication_requires_dedicated_public_repository_and_infra_readiness(self):
        for env, transcript, expected, error in [
                ({'AUTH_IMAGE_REPOSITORY': 'ghcr.io/repoagent/controller'}, [], [], 'must be the dedicated'),
                ({'AUTH_DEPLOYMENT_READY': ''}, [], [], 'AUTH_DEPLOYMENT_READY must be true'),
                ({}, [step(CHECK, error='runtime prerequisites unavailable')], [CHECK], 'failed during auth delivery'),
                ({}, [step(CHECK), step(METADATA, error=CANARY)], [CHECK, METADATA], 'metadata is unavailable'),
                ({}, [step(CHECK), step(METADATA, {'visibility': 'private'})], [CHECK, METADATA], 'must already exist and be public')]:
            with self.subTest(env=env, error=error):
                result, calls, output, _ = self.invoke('publish', transcript, environment=env)

                self.assertEqual(result.returncode, 1)
                self.assertIn(error, result.stderr)
                self.assertEqual(([call['argv'] for call in calls], output), (expected, 'existing=value\n'))
                self.assertNotIn(CANARY, result.stdout + result.stderr)

    def test_missing_or_malformed_azure_identity_stops_before_any_provider(self):
        for key in ['AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID']:
            for value in ['', 'not-a-uuid']:
                for verb in ['check', 'publish']:
                    with self.subTest(key=key, value=value, verb=verb):
                        # Keep every later prerequisite valid to expose a real
                        # check/publish continuation rather than a fixture error.
                        result, calls, output, _ = self.invoke(verb, publication(), environment={key: value})

                        self.assertEqual(result.returncode, 1)
                        self.assertIn(key + ' is missing or invalid', result.stderr)
                        self.assertEqual(([call['argv'] for call in calls], output), ([], 'existing=value\n'))

    def test_check_runs_infra_and_public_package_preflight_without_building(self):
        result, calls, output, _ = self.invoke('check', publication()[:2])

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(([call['argv'] for call in calls], output), ([CHECK, METADATA], 'existing=value\n'))

    def test_local_image_role_platform_and_numeric_nonroot_user_gate_push(self):
        invalid = [local_image(Config=dict(Labels={'io.repoagent.role': 'controller'}, User='10001')),
                   local_image(Os='windows'), local_image(Architecture='arm64')]
        invalid += [local_image(Config=dict(Labels={'io.repoagent.role': 'auth-service'}, User=user))
                    for user in ['', '0', '0:10001', 'root', 'app']]
        for image in invalid:
            with self.subTest(image=image):
                result, calls, output, _ = self.invoke('publish', publication(local=image)[:4])

                self.assertEqual(result.returncode, 1)
                self.assertIn('image', result.stderr.lower())
                self.assertEqual(([call['argv'] for call in calls], output),
                                 ([CHECK, METADATA, BUILD, INSPECT], 'existing=value\n'))

    def test_publish_requires_reviewed_source_sha_before_build(self):
        result, calls, output, _ = self.invoke('publish', publication()[:2], environment={'GITHUB_SHA': 'main;echo bad'})

        self.assertEqual(result.returncode, 1)
        self.assertIn('GITHUB_SHA must identify the reviewed source commit', result.stderr)
        self.assertEqual(([call['argv'] for call in calls], output), ([CHECK, METADATA], 'existing=value\n'))

    def test_publish_outputs_only_the_exact_anonymously_attested_digest(self):
        result, calls, output, _ = self.invoke('publish', publication())

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([call['argv'] for call in calls], [CHECK, METADATA, BUILD, INSPECT, PUSH, INSPECT, ATTEST])
        self.assertEqual(output, 'existing=value\nimage=' + IMAGE + '\n')
        self.assertEqual(calls[1]['gh_token'], CANARY)
        self.assertNotIn(CANARY, result.stdout + result.stderr + output + json.dumps([call['argv'] for call in calls]))

    def test_ambiguous_wrong_repository_or_malformed_digests_never_become_outputs(self):
        for digests in [[], [IMAGE, IMAGE], ['ghcr.io/repoagent/controller@sha256:' + 'a' * 64],
                        [REPOSITORY + ':latest'], [REPOSITORY + '@sha256:bad']]:
            with self.subTest(digests=digests):
                result, calls, output, _ = self.invoke('publish', publication(digests=digests)[:6])

                self.assertEqual(result.returncode, 1)
                self.assertIn('one unambiguous repository digest', result.stderr)
                self.assertEqual(([call['argv'] for call in calls], output),
                                 ([CHECK, METADATA, BUILD, INSPECT, PUSH, INSPECT], 'existing=value\n'))

    def test_failed_anonymous_attestation_never_exports_an_image(self):
        for remote in [remote_image(config=dict(Labels={}, User='10001')), remote_image(architecture='arm64'),
                       remote_image(config=dict(Labels={'io.repoagent.role': 'auth-service'}, User='0'))]:
            with self.subTest(remote=remote):
                result, calls, output, _ = self.invoke('publish', publication(remote=remote))

                self.assertEqual(result.returncode, 1)
                self.assertEqual(([call['argv'] for call in calls], output),
                                 ([CHECK, METADATA, BUILD, INSPECT, PUSH, INSPECT, ATTEST], 'existing=value\n'))
        result, calls, output, _ = self.invoke('publish', publication()[:6] + [step(ATTEST, error=CANARY)])

        self.assertEqual(result.returncode, 1)
        self.assertEqual(output, 'existing=value\n')
        self.assertNotIn(CANARY, result.stdout + result.stderr)

    def test_attest_checks_the_exact_digest_anonymously_without_publication(self):
        result, calls, output, _ = self.invoke('attest', [step(ATTEST, remote_image())])

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(([call['argv'] for call in calls], output), ([ATTEST], 'existing=value\n'))
        for image in [REPOSITORY + ':latest', 'ghcr.io/repoagent/controller@sha256:' + 'a' * 64]:
            with self.subTest(image=image):
                result, calls, output, _ = self.invoke('attest', environment={'AUTH_IMAGE': image})

                self.assertEqual(result.returncode, 1)
                self.assertEqual((calls, output), ([], 'existing=value\n'))

    def test_workflow_login_transports_token_only_through_stdin(self):
        # Execute the actual shell step; do not duplicate or assert workflow YAML.
        login = next(line.partition('run: ')[2] for line in WORKFLOW.read_text().splitlines()
                     if line.lstrip().startswith('run: ') and 'docker login ' in line)
        expected = ['docker', 'login', 'ghcr.io', '-u', 'fixture-bot', '--password-stdin']
        result, calls, output, injected = self.invoke('', [step(expected)], shell=login)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([(call['argv'], call['stdin']) for call in calls], [(expected, CANARY)])
        self.assertNotIn(CANARY, result.stdout + result.stderr + output + json.dumps([call['argv'] for call in calls]))
        self.assertFalse(injected)


if __name__ == '__main__':
    unittest.main()
