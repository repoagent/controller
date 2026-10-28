"""A real controller process publishes its dashboard; only a stop it handles is reported.

GitHub and the credential broker are the local TLS GitProvider, reached through the installed gh and the
controller's own HTTPS client. The agent app-server is a controlled stub. No live provider access.
"""
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import tempfile
import threading
import time
import unittest

from test_managed_git import CONTROLLER, ROOT, GitProvider, free_port

CATEGORY = 'Controller status'
HEARTBEAT = re.compile(r'Last heartbeat\W+(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)')
# Answers every agent request; no Discussion is enrolled, so no session or turn starts.
AGENT = """#!/usr/bin/python3
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

    def fixture(self, directory):
        provider = GitProvider(directory)
        self.addCleanup(provider.close)
        # A v1 enrollment: Discussions only, no clone; its leases follow it.
        provider.webhooks, provider.clone, provider.hooks = False, False, []
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
        (tools / 'codex').write_text(AGENT)
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
                running = github.wait(self, publishing)
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
                restarted = github.wait(self, lambda body: publishing(body) and body != running)
                self.assertGreaterEqual(heartbeat(restarted), heartbeat(running))
                second.process.send_signal(signal.SIGTERM)
                second.process.wait(timeout=15)
            stopped = github.body()
            self.assertRegex(stopped, r'\*\*Controller:\*\* stopped at \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ after a graceful shutdown')
            self.assertRegex(stopped, HEARTBEAT)
            self.assertGreaterEqual(heartbeat(stopped), heartbeat(restarted))
            self.assertEqual([kind for kind, _ in github.writes].count('createDiscussion'), 1, 'The restarted controller found its dashboard again')
            self.assertEqual(len(github.dashboards()), 1)
            for _, text in github.writes:
                self.assertNotIn(provider.token, text)


def publishing(body):
    # A current snapshot has time evidence, not an indefinite claim that this process is running.
    return HEARTBEAT.search(body) and 'lifetime started at' in body and 'starting;' not in body and 'stopped at' not in body


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
