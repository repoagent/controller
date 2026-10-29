"""Broker delivery selection rules from the shipped workflow.

These checks evaluate project-owned job conditions, not GitHub's scheduler, OIDC,
registry or Azure behavior. Hosted PR/main runs remain the wiring acceptance.
"""
from pathlib import Path
import re
import unittest

WORKFLOW = Path(__file__).resolve().parents[2] / '.github/workflows/broker.yml'


def condition(name):
    body = re.split(rf'(?m)^  {re.escape(name)}:\n', WORKFLOW.read_text().split('\njobs:\n', 1)[1], maxsplit=1)[1]
    body = re.split(r'(?m)^  (?=[a-z-]+:\n)', body, maxsplit=1)[0]
    return re.search(r'^    if: (.+)$', body, re.M)[1].removeprefix('${{').removesuffix('}}').strip()


def selected(name, *, event='push', operation='', ref='refs/heads/main', published='success', cancelled=False,
             approved=True, verified='success', recorded='success', repository='repoagent/controller'):
    context = {'github.event_name': event, 'github.ref': ref, 'inputs.operation': operation,
               'github.repository': repository,
               'needs.publish.result': published, 'needs.verify-service.result': verified,
               'needs.release-metadata.result': recorded, 'needs.select-release.outputs.release': 'true' if approved else 'false'}
    expression = condition(name).replace('!cancelled()', repr(not cancelled))
    expression = re.sub(r'(?:github|inputs|needs)\.[a-z_.-]+', lambda match: repr(context[match[0]]), expression)
    expression = expression.replace('&&', ' and ').replace('||', ' or ')
    if not re.fullmatch(r'(?:\s|\(|\)|==|!=|and|or|True|False)*', re.sub(r"'[^']*'", '', expression)):
        raise AssertionError('Unsupported workflow expression: ' + condition(name))
    return eval(expression, {'__builtins__': {}})


class BrokerWorkflowTests(unittest.TestCase):
    def test_restore_helpers_trigger_broker_verification_and_compiled_check_is_mandatory(self):
        text = WORKFLOW.read_text()
        paths = text.split('paths: &auth-paths', 1)[1].split('pull_request:', 1)[0]
        for helper in ('scripts/delivery/auth-restore-plan.py',
                       'scripts/delivery/check_auth_restore_foundation.py'):
            self.assertIn("- '" + helper + "'", paths)
        verification = text.split('\n  verify-service:\n', 1)[1].split('\n  publish:\n', 1)[0]
        compiler = verification.index('export BICEP_BIN=')
        check = verification.index('python3 scripts/delivery/check_auth_restore_foundation.py -v')
        self.assertLess(compiler, check)
        self.assertNotIn('continue-on-error', verification)

    def test_authority_probe_is_explicit_main_only_and_never_selects_version_get_or_delivery_jobs(self):
        context = dict(event='workflow_dispatch',operation='authority-probe',published='skipped',recorded='skipped')
        self.assertTrue(selected('authority-probe',**context))
        for overrides in (dict(ref='refs/heads/other'),dict(repository='other/controller'),dict(event='push')):
            self.assertFalse(selected('authority-probe',**dict(context,**overrides)))
        for name in ('version-probe','diagnostics','verify-service','publish','deploy'):
            self.assertFalse(selected(name,**context),name)
        self.assertFalse(selected('authority-probe',event='workflow_dispatch',operation='version-probe'))

    def test_version_probe_is_explicit_main_only_and_does_not_select_get_diagnostics_or_delivery(self):
        context = dict(event='workflow_dispatch', operation='version-probe', published='skipped', recorded='skipped')
        self.assertTrue(selected('version-probe', **context))
        for overrides in (dict(ref='refs/heads/other'), dict(repository='other/controller'), dict(event='push')):
            self.assertFalse(selected('version-probe', **dict(context, **overrides)))
        for name in ('diagnostics','verify-service','publish','deploy'):
            self.assertFalse(selected(name, **context), name)
        self.assertFalse(selected('version-probe', event='workflow_dispatch', operation='diagnostics'))

    def test_diagnostics_only_selects_fixed_repository_main_dispatch_and_never_deploys(self):
        context = dict(event='workflow_dispatch', operation='diagnostics', published='skipped', recorded='skipped')
        self.assertTrue(selected('diagnostics', **context))
        self.assertFalse(selected('diagnostics', **context, ref='refs/heads/other'))
        self.assertFalse(selected('diagnostics', **context, repository='other/controller'))
        self.assertFalse(selected('diagnostics'))
        for name in ('verify-service', 'publish', 'deploy'):
            self.assertFalse(selected(name, **context), name)

    def test_release_pr_explicit_verification_is_read_only_on_its_branch(self):
        context = dict(event='workflow_dispatch', operation='verify', ref='refs/heads/release-please--branches--main',
                       published='skipped', recorded='skipped', approved=False)
        self.assertTrue(selected('verify-service', **context))
        self.assertFalse(selected('publish', **context))
        self.assertFalse(selected('deploy', **context))

    def test_unreleased_main_source_is_verified_without_republishing_the_previous_version(self):
        self.assertTrue(selected('verify-service', approved=False))
        self.assertFalse(selected('publish', approved=False))
        self.assertFalse(selected('deploy', published='skipped', recorded='skipped', approved=False))

    def test_pull_requests_verify_without_publication_or_deployment(self):
        context = dict(event='pull_request', ref='refs/pull/7/merge', published='skipped')
        self.assertTrue(selected('verify-service', **context))
        self.assertFalse(selected('publish', **context))
        self.assertFalse(selected('deploy', **context))

    def test_main_delivery_deploys_only_a_successful_publication(self):
        self.assertTrue(selected('verify-service'))
        self.assertTrue(selected('publish'))
        self.assertTrue(selected('deploy'))
        for result in ['failure', 'cancelled', 'skipped']:
            with self.subTest(publication=result):
                self.assertFalse(selected('deploy', published=result))
        self.assertFalse(selected('deploy', cancelled=True))
        for result in ['failure', 'cancelled', 'skipped']:
            with self.subTest(metadata=result):
                self.assertFalse(selected('deploy', recorded=result))

    def test_explicit_image_rollback_runs_without_build_or_publication(self):
        context = dict(event='workflow_dispatch', operation='deploy-image', published='skipped')
        self.assertFalse(selected('verify-service', **context))
        self.assertFalse(selected('publish', **context))
        self.assertTrue(selected('deploy', **context))
        self.assertFalse(selected('deploy', **context, ref='refs/heads/feature'))
        self.assertFalse(selected('deploy', **context, cancelled=True))
        # Without an explicit status function GitHub skips jobs whose dependency was skipped.
        self.assertIn('!cancelled()', condition('deploy'))

    def test_explicit_version_rollback_resolves_existing_metadata_without_publication(self):
        context = dict(event='workflow_dispatch', operation='deploy-release', published='skipped', recorded='skipped')
        self.assertFalse(selected('verify-service', **context))
        self.assertFalse(selected('publish', **context))
        self.assertTrue(selected('deploy', **context))
        self.assertFalse(selected('deploy', **context, ref='refs/heads/feature'))

    def test_manual_source_retry_preserves_the_full_delivery_path(self):
        context = dict(event='workflow_dispatch', operation='deploy')
        for name in ['verify-service', 'publish', 'deploy']:
            self.assertTrue(selected(name, **context), name)

    def test_access_and_guarded_recovery_do_not_accidentally_deploy(self):
        for operation in ['access-check', 'diagnostics', 'version-probe', 'authority-probe', 'bootstrap-publish', 'foundation-plan', 'foundation-apply',
                          'stage-plan', 'stage-apply', 'promote-staged', 'stage-stop', 'stage-resume',
                          'cleanup-identity-plan', 'cleanup-identity-apply', 'cleanup-plan', 'cleanup-apply']:
            context = dict(event='workflow_dispatch', operation=operation, published='skipped')
            for name in ['verify-service', 'publish', 'deploy']:
                with self.subTest(operation=operation, job=name):
                    self.assertFalse(selected(name, **context))


if __name__ == '__main__':
    unittest.main()
