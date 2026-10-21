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
import textwrap


SCRIPT = Path(__file__).with_name('auth-pipeline.py')
WORKFLOW = SCRIPT.parents[2] / '.github/workflows/ci-auth-service.yml'
REPOSITORY = 'ghcr.io/repoagent/controller-auth'
SHA = 'b' * 40
TAG = REPOSITORY + ':sha-' + SHA
IMAGE = REPOSITORY + '@sha256:' + 'a' * 64
CANARY = 'fake-token-$(touch token-was-executed);keep-private'
SOURCE_FILES = ('service/auth/global.json', 'service/auth/verify.sh', 'service/auth/Dockerfile',
                'infra/auth/deploy.sh', 'infra/auth/main.bicep')
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
             gh_token=os.environ.get('GH_TOKEN', ''), registry_present='REGISTRY_TOKEN' in os.environ,
             config=os.environ.get('DOCKER_CONFIG', ''))
if entry['config']:
    entry['mode'] = pathlib.Path(entry['config']).stat().st_mode & 0o777
    if argv[1:2] == ['login']:
        (pathlib.Path(entry['config']) / 'config.json').write_text(os.environ['REGISTRY_TOKEN'])
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
    return dict(dict(config=dict(Labels={'io.repoagent.role': 'auth-service', 'org.opencontainers.image.revision': SHA}, User='10001:10001'),
                     os='linux', architecture='amd64'), **changes)


def step(argv, response=None, error=None):
    return dict(argv=argv, response=response, error=error)


def publication(local=None, digests=None, remote=None):
    return [step(BUILD),
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
            for name in ['gh', 'docker', 'skopeo', 'az', 'python3']:
                path = binaries / name
                path.write_text(f'#!{sys.executable}\n' + DOUBLE)
                path.chmod(0o700)
            for name in ('bash', 'mktemp', 'rm'):
                (binaries / name).symlink_to(shutil.which(name))
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
                       REGISTRY_TOKEN=CANARY, REGISTRY_USER='fixture-bot', RUNNER_TEMP=str(root),
                       BOOTSTRAP_RUN_URL='https://example.test/actions/runs/1', BOOTSTRAP_RUN_ATTEMPT='2')
            env.update(environment or {})
            command = [str(binaries / 'bash'), '-e', '-c', shell] if shell else [sys.executable, '-B', str(script), verb]
            result = subprocess.run(command, cwd=root, env=env, capture_output=True, text=True, timeout=5)
            calls = [json.loads(line) for line in ledger.read_text().splitlines()] if ledger.exists() else []
            if shell:
                self.assertEqual(list(root.glob('auth-bootstrap-docker.*')), [], 'Credentials must be removed before fixture teardown')
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

    def test_publication_requires_the_dedicated_repository(self):
        result, calls, output, _ = self.invoke('publish', environment={
            'AUTH_IMAGE_REPOSITORY': 'ghcr.io/repoagent/controller'})

        self.assertEqual(result.returncode, 1)
        self.assertIn('must be the dedicated', result.stderr)
        self.assertEqual((calls, output), ([], 'existing=value\n'))

    def test_check_needs_no_cloud_credentials_or_acceptance_assertions(self):
        result, calls, output, _ = self.invoke('check', environment={
            'AUTH_DEPLOYMENT_READY': '', 'AZURE_CLIENT_ID': '', 'AZURE_TENANT_ID': '',
            'AZURE_SUBSCRIPTION_ID': ''})

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((calls, output), ([], 'existing=value\n'))

    def test_local_image_role_platform_and_numeric_nonroot_user_gate_push(self):
        invalid = [local_image(Config=dict(Labels={'io.repoagent.role': 'controller'}, User='10001')),
                   local_image(Os='windows'), local_image(Architecture='arm64')]
        invalid += [local_image(Config=dict(Labels={'io.repoagent.role': 'auth-service'}, User=user))
                    for user in ['', '0', '0:10001', 'root', 'app']]
        for image in invalid:
            with self.subTest(image=image):
                result, calls, output, _ = self.invoke('publish', publication(local=image)[:2])

                self.assertEqual(result.returncode, 1)
                self.assertIn('image', result.stderr.lower())
                self.assertEqual(([call['argv'] for call in calls], output),
                                 ([BUILD, INSPECT], 'existing=value\n'))

    def test_publish_requires_reviewed_source_sha_before_build(self):
        result, calls, output, _ = self.invoke('publish', [], environment={'GITHUB_SHA': 'main;echo bad'})

        self.assertEqual(result.returncode, 1)
        self.assertIn('GITHUB_SHA must identify the reviewed source commit', result.stderr)
        self.assertEqual(([call['argv'] for call in calls], output), ([], 'existing=value\n'))

    def test_publish_outputs_only_the_exact_anonymously_attested_digest(self):
        result, calls, output, _ = self.invoke('publish', publication(), environment={'AUTH_DEPLOYMENT_READY': ''})

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([call['argv'] for call in calls], [BUILD, INSPECT, PUSH, INSPECT, ATTEST])
        self.assertEqual(output, 'existing=value\nimage=' + IMAGE + '\n')
        self.assertNotIn(CANARY, result.stdout + result.stderr + output + json.dumps([call['argv'] for call in calls]))

    def test_ambiguous_wrong_repository_or_malformed_digests_never_become_outputs(self):
        for digests in [[], [IMAGE, IMAGE], ['ghcr.io/repoagent/controller@sha256:' + 'a' * 64],
                        [REPOSITORY + ':latest'], [REPOSITORY + '@sha256:bad']]:
            with self.subTest(digests=digests):
                result, calls, output, _ = self.invoke('publish', publication(digests=digests)[:4])

                self.assertEqual(result.returncode, 1)
                self.assertIn('one unambiguous repository digest', result.stderr)
                self.assertEqual(([call['argv'] for call in calls], output),
                                 ([BUILD, INSPECT, PUSH, INSPECT], 'existing=value\n'))

    def test_failed_anonymous_attestation_never_exports_an_image(self):
        for remote in [remote_image(config=dict(Labels={}, User='10001')), remote_image(architecture='arm64'),
                       remote_image(config=dict(Labels={'io.repoagent.role': 'auth-service'}, User='0'))]:
            with self.subTest(remote=remote):
                result, calls, output, _ = self.invoke('publish', publication(remote=remote))

                self.assertEqual(result.returncode, 1)
                self.assertEqual(([call['argv'] for call in calls], output),
                                 ([BUILD, INSPECT, PUSH, INSPECT, ATTEST], 'existing=value\n'))
        result, calls, output, _ = self.invoke('publish', publication()[:4] + [step(ATTEST, error=CANARY)])

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

    def test_attest_rejects_an_image_from_a_different_accepted_source(self):
        image = remote_image(config=dict(Labels={
            'io.repoagent.role': 'auth-service',
            'org.opencontainers.image.revision': 'c' * 40}, User='10001:10001'))

        result, calls, output, _ = self.invoke(
            'attest', [step(ATTEST, image)], environment={'AUTH_EXPECTED_SOURCE_SHA': SHA})

        self.assertEqual(([call['argv'] for call in calls], output), ([ATTEST], 'existing=value\n'))
        self.assertNotIn(CANARY, result.stdout + result.stderr + output)
        self.assertNotEqual(result.returncode, 0, 'Attestation accepted an image from a different source commit')

    def test_attest_accepts_the_image_matching_the_accepted_source(self):
        image = remote_image(config=dict(Labels={
            'io.repoagent.role': 'auth-service',
            'org.opencontainers.image.revision': SHA}, User='10001:10001'))

        result, calls, output, _ = self.invoke(
            'attest', [step(ATTEST, image)], environment={'AUTH_EXPECTED_SOURCE_SHA': SHA})

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(([call['argv'] for call in calls], output), ([ATTEST], 'existing=value\n'))
        self.assertNotIn(CANARY, result.stdout + result.stderr + output)

    def test_attest_requires_full_expected_sha_and_an_image_revision_when_binding_source(self):
        for expected, revision in (('', SHA), (SHA[:12], SHA), ('main', SHA), (SHA, None)):
            with self.subTest(expected=expected, revision=revision):
                labels = {'io.repoagent.role': 'auth-service'}
                if revision is not None:
                    labels['org.opencontainers.image.revision'] = revision
                image = remote_image(config=dict(Labels=labels, User='10001'))
                result, calls, output, _ = self.invoke(
                    'attest', [step(ATTEST, image)], environment={'AUTH_EXPECTED_SOURCE_SHA': expected})
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(([call['argv'] for call in calls], output), ([ATTEST], 'existing=value\n'))

    def test_bootstrap_job_is_manual_isolated_and_keeps_inputs_as_data(self):
        workflow = WORKFLOW.read_text()
        job = workflow.split('\n  bootstrap-publish:\n', 1)[1].split('\n  activation:', 1)[0]
        self.assertIn("if: github.ref == 'refs/heads/main' && github.event_name == 'workflow_dispatch' && inputs.operation == 'bootstrap-publish'", job)
        self.assertIn('permissions:\n      contents: read\n      packages: write\n    env:', job)
        for forbidden in ('needs:', 'environment:', 'id-token:', 'azure/', 'AUTH_DEPLOYMENT_READY:'):
            self.assertNotIn(forbidden, job)
        self.assertIn('AUTH_EXPECTED_SOURCE_SHA: ${{ inputs.expected-source-sha }}', job)
        self.assertIn("AUTH_PACKAGE_ACTIONS_ACCESS_CONFIRMED: ''", job)
        self.assertIn('AUTH_IMAGE_REPOSITORY: ' + REPOSITORY, job)
        self.assertIn('persist-credentials: false', job)

    def test_bootstrap_shell_unsets_token_and_removes_private_config_on_every_exit(self):
        job = WORKFLOW.read_text().split('\n  bootstrap-publish:\n', 1)[1].split('\n  activation:', 1)[0]
        shell = textwrap.dedent(job.split('        run: |\n', 1)[1])
        self.assertNotIn('${{', shell)
        self.assertIn('REGISTRY_TOKEN: ${{ github.token }}', job)
        login = ['docker', 'login', 'ghcr.io', '-u', 'fixture-bot', '--password-stdin']
        helper = ['python3', 'scripts/delivery/auth-pipeline.py', 'bootstrap-publish-actions']
        for login_error, helper_error in ((None, None), ('login failed', None), (None, 'helper failed')):
            with self.subTest(login_error=login_error, helper_error=helper_error):
                transcript = [step(login, error=login_error)]
                if not login_error:
                    transcript.append(step(helper, error=helper_error))
                result, calls, output, injected = self.invoke('', transcript, shell=shell, environment={'GH_TOKEN': ''})
                self.assertEqual(result.returncode == 0, not (login_error or helper_error), result.stderr)
                self.assertEqual([call['argv'] for call in calls], [item['argv'] for item in transcript])
                self.assertEqual(calls[0]['stdin'], CANARY)
                self.assertTrue(all(call['mode'] == 0o700 for call in calls))
                if not login_error:
                    self.assertFalse(calls[-1]['registry_present'], 'Helper must not inherit registry token')
                    self.assertEqual(calls[-1]['gh_token'], '')
                self.assertNotIn(CANARY, result.stdout + result.stderr + output + json.dumps([call['argv'] for call in calls]))
                self.assertFalse(injected)

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
