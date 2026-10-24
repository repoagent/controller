"""A real controller process publishes its dashboard; only a stop it handles is reported.

GitHub and the credential broker are the local TLS GitProvider, reached through the installed gh and the
controller's own HTTPS client. The native app-server is a controlled stub. No live provider access.
"""
from contextlib import closing
import hashlib
import hmac
import http.client
import json
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import tempfile
import threading
import time
import unittest
import uuid

from test_managed_git import CONTROLLER, ROOT, GitProvider, free_port

CATEGORY = 'Controller status'
HEARTBEAT = re.compile(r'Heartbeat\W+(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)')
# Answers every native request; no Discussion is enrolled, so no session or turn starts.
NATIVE = """#!/usr/bin/python3
import json,os,sys
for line in sys.stdin:
 request=json.loads(line)
 if 'id' in request:print(json.dumps({'id':request['id'],'result':{'codexHome':os.getcwd()}}),flush=True)
"""


@unittest.skipIf(os.name == 'nt', 'The controller process fixture uses Linux signals and sockets')
class ControllerDashboardProcessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build = subprocess.run(['dotnet', 'build', str(ROOT / 'src/RepoAgentController/RepoAgentController.csproj'),
                                '--configuration', 'Release', '--nologo', '--verbosity', 'quiet'],
                               cwd=ROOT, capture_output=True, text=True, timeout=180)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        controller = os.environ.get('CHECKOUT_CONTROLLER')
        cls.command = [str(Path(controller).resolve(strict=True))] if controller else ['dotnet', str(CONTROLLER)]

    def fixture(self, directory, webhooks=False):
        provider = GitProvider(directory)
        self.addCleanup(provider.close)
        # A v1 enrollment: Discussions only, no clone; its leases follow it.
        provider.webhooks, provider.clone, provider.hooks = webhooks, False, []
        if not webhooks:
            provider.enrollment = lambda: dict(protocolVersion=1, brokerOrigin='https://broker.fixture', githubHost='github.com',
                                               deviceId='device-1', deviceSecret='a' * 43, appId=123, appSlug='test-app', installationId=234,
                                               repositoryId=345, repository='owner/project', permissions={'discussions': 'write'})
        github = OwnedDiscussions(provider)
        enrollment = directory / 'enrollment.json'
        enrollment.write_text(json.dumps(provider.enrollment()))
        enrollment.chmod(0o600)
        tools, workspace = directory / 'tools', directory / 'workspace'
        tools.mkdir()
        workspace.mkdir()
        (tools / 'codex').write_text(NATIVE)
        (tools / 'codex').chmod(0o700)
        environment = provider.environment()
        environment.update(PATH=str(tools) + os.pathsep + environment['PATH'])
        command = self.command + ['--repo', 'owner/project', '--app-id', '123', '--broker-url', 'https://broker.fixture',
                                  '--enrollment-file', str(enrollment), '--state', str(directory / 'state.json'),
                                  '--workspace', str(workspace), '--interval', '3600', '--dashboard-category', CATEGORY,
                                  '--status-port', str(free_port())]
        return provider, github, command, environment

    def test_killed_controller_leaves_a_stale_heartbeat_and_a_handled_stop_is_reported(self):
        with tempfile.TemporaryDirectory(prefix='dashboard-process-') as temporary:
            provider, github, command, environment = self.fixture(Path(temporary))

            with Controller(self, command, environment) as first:
                running = github.wait(self, lambda body: '**Controller:** running' in body)
                first.process.kill()
                first.process.wait(timeout=10)
                writes = len(github.writes)
            time.sleep(1)
            # Abrupt termination: the last report stands unchanged, so its heartbeat only ages; nothing claims a stop.
            self.assertEqual(len(github.writes), writes)
            self.assertEqual(github.body(), running)
            self.assertRegex(running, HEARTBEAT)
            self.assertNotIn('stopped', running)

            with Controller(self, command, environment) as second:
                restarted = github.wait(self, lambda body: '**Controller:** running' in body and body != running)
                self.assertGreaterEqual(heartbeat(restarted), heartbeat(running))
                second.process.send_signal(signal.SIGTERM)
                second.process.wait(timeout=15)
            stopped = github.body()
            self.assertRegex(stopped, r'\*\*Controller:\*\* stopped at \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ after a graceful shutdown')
            self.assertNotRegex(stopped, HEARTBEAT)
            self.assertEqual([kind for kind, _ in github.writes].count('createDiscussion'), 1, 'The restarted controller found its dashboard again')
            self.assertEqual(len(github.dashboards()), 1)
            for _, text in github.writes:
                self.assertNotIn(provider.token, text)

    def test_webhook_deliveries_about_the_dashboard_never_wake_the_controller(self):
        with tempfile.TemporaryDirectory(prefix='dashboard-webhook-') as temporary:
            directory = Path(temporary)
            provider, github, command, environment = self.fixture(directory, webhooks=True)
            with socket.socket() as listener:
                listener.bind(('127.0.0.1', 0))
                port = listener.getsockname()[1]
            command += ['--webhook-url', 'https://fixture.invalid', '--listen-port', str(port), '--reconcile-interval', '3600']

            with Controller(self, command, environment) as controller:
                body = github.wait(self, lambda body: '**Controller:** running' in body)
                controller.wait('reconciliation-completed discussion=all success=True')
                secret = json.loads(Path(str(directory / 'state.json') + '.webhook/hook.json').read_text())['secret'].encode()

                def deliver(event, action, number, author, sender='octocat'):
                    payload = json.dumps(dict(action=action, repository=dict(full_name='owner/project', id=345),
                                              discussion=dict(number=number, body=body, user=dict(login=author, type='Bot' if author.endswith('[bot]') else 'User')),
                                              sender=dict(login=sender, type='Bot' if sender.endswith('[bot]') else 'User'))).encode()
                    with closing(http.client.HTTPConnection('127.0.0.1', port, timeout=10)) as connection:
                        connection.request('POST', '/github/webhook', payload, {
                            'X-GitHub-Event': event, 'X-GitHub-Delivery': str(uuid.uuid4()),
                            'X-Hub-Signature-256': 'sha256=' + hmac.new(secret, payload, hashlib.sha256).hexdigest()})
                        response = connection.getresponse()
                        response.read()
                        return response.status

                # GitHub notifies every heartbeat edit, and participants may reply on the dashboard; none of it is agent input.
                self.assertEqual(deliver('discussion', 'edited', 100, 'test-app[bot]', sender='test-app[bot]'), 204)
                self.assertEqual(deliver('discussion_comment', 'created', 100, 'test-app[bot]'), 204)
                # A participant's copy of the dashboard text is an ordinary Discussion and still wakes the controller.
                self.assertEqual(deliver('discussion', 'created', 101, 'octocat'), 202)
                controller.wait('webhook-observed discussion=101 success=True')
            self.assertFalse([line for line in controller.lines if 'discussion=100' in line])


def heartbeat(body):
    return HEARTBEAT.search(body).group(1)


class OwnedDiscussions:
    """GitHub's Discussions for this repository: one dashboard category, written only by the controller's gh."""

    def __init__(self, provider):
        self.lock = threading.Lock()
        self.discussions, self.writes = [], []
        provider.discussions = self.discussions  # Polls list them too, so exclusion is exercised.
        provider.graphql = self.answer

    def answer(self, request):
        query, variables = request.get('query', ''), request.get('variables') or {}
        page = lambda nodes: {'nodes': nodes, 'pageInfo': {'hasNextPage': False, 'endCursor': None}}
        with self.lock:
            if 'createDiscussion' in query:
                number = 100 + len(self.discussions)
                created = {'id': 'DASH%d' % number, 'number': number, 'url': 'https://github.com/owner/project/discussions/%d' % number,
                           'title': variables['title'], 'body': variables['body'], 'bodyHTML': '', 'closed': False, 'locked': False,
                           'answer': None, 'category': {'id': variables['category'], 'name': CATEGORY},
                           'author': {'__typename': 'Bot', 'login': 'test-app'}, 'reactionGroups': []}
                self.discussions.append(created)
                self.writes.append(('createDiscussion', json.dumps(variables)))
                return {'data': {'createDiscussion': {'discussion': {'id': created['id'], 'url': created['url']}}}}
            if 'updateDiscussion' in query:
                target = next(d for d in self.discussions if d['id'] == variables['id'])
                target.update(title=variables['title'], body=variables['body'])
                self.writes.append(('updateDiscussion', json.dumps(variables)))
                return {'data': {'updateDiscussion': {'discussion': {'id': target['id']}}}}
            if 'discussionCategories' in query:
                return {'data': {'repository': {'id': 'R_project', 'discussionCategories': page([{'id': 'DIC9', 'name': CATEGORY}])}}}
            if 'categoryId' in query:
                return {'data': {'repository': {'discussions': page([d for d in self.discussions if d['category']['id'] == variables['category']])}}}
        return None

    def dashboards(self):
        with self.lock:
            return [d for d in self.discussions if d['author']['login'] == 'test-app']

    def body(self):
        owned = self.dashboards()
        return owned[0]['body'] if len(owned) == 1 else None

    def wait(self, test, condition, timeout=30):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            body = self.body()
            if body is not None and condition(body):
                return body
            time.sleep(0.05)
        test.fail('Dashboard did not reach the expected state:\n' + str(self.body()))


class Controller:
    """A controller process whose output is drained, and which is never left running."""

    def __init__(self, test, command, environment):
        self.test, self.command, self.environment, self.lines = test, command, environment, []

    def __enter__(self):
        self.process = subprocess.Popen(self.command, env=self.environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        self.reader = threading.Thread(target=lambda: self.lines.extend(self.process.stdout), daemon=True)
        self.reader.start()
        return self

    def wait(self, text, timeout=30):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline and not any(text in line for line in self.lines):
            time.sleep(0.05)
        self.test.assertTrue(any(text in line for line in self.lines), 'Controller never logged ' + text + ':\n' + ''.join(self.lines))

    def __exit__(self, *_):
        if self.process.poll() is None:
            self.process.kill()
        self.process.wait(timeout=10)
        self.reader.join(timeout=5)
        self.process.stdout.close()
        print('DASHBOARD_CONTROLLER exit=' + str(self.process.returncode) + ' ' +
              ''.join(line for line in self.lines if ' dashboard' in line or ' fatal ' in line), flush=True)


if __name__ == '__main__':
    unittest.main()
