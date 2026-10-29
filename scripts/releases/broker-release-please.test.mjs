import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const cli = fileURLToPath(new URL('./release-please.mjs', import.meta.url));
const brokerFile = 'service/auth/src/RepoAgent.Auth/BrokerEndpoints.cs';
const commit = (message, files = [brokerFile], letter = 'a') => ({ sha: letter.repeat(40), message, files });
function plan(input) {
  const result = spawnSync(process.execPath, [cli, 'plan'], { input: JSON.stringify(input), encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
}

test('the first broker publication starts at 1.0.0 independently of controller history', () => {
  const result = plan({ component: 'broker', commits: [commit('feat(broker): add renewal observations')] });
  assert.equal(result.version, '1.0.0');
  assert.equal(result.tag, 'broker-v1.0.0');
  assert.match(result.notes, /add renewal observations/);
});

for (const [message, expected] of [
  ['fix(broker): preserve safe renewal', '1.2.4'],
  ['feat(broker): add credential diagnostics', '1.3.0'],
  ['feat(broker)!: require a new state contract\n\nBREAKING CHANGE: Migrate retained state before upgrading.', '2.0.0'],
]) {
  test(`broker release classification: ${expected}`, () => {
    const result = plan({ component: 'broker', currentVersion: '1.2.3', commits: [commit(message)] });
    assert.equal(result.version, expected);
    assert.equal(result.tag, `broker-v${expected}`);
    assert.match(result.notes, /renewal|diagnostics|state contract/);
  });
}

test('actual runtime paths preserve independent components despite misleading scopes', () => {
  assert.equal(plan({ component: 'broker', currentVersion: '1.2.3', commits: [
    commit('feat(broker): change controller dispatch', ['src/RepoAgentController/BridgeCommand.cs']),
  ] }), null);
  assert.equal(plan({ component: 'controller', currentVersion: '1.2.3', commits: [
    commit('feat(controller): change broker renewal'),
  ] }), null);
  assert.equal(plan({ component: 'broker', currentVersion: '1.2.3', commits: [
    commit('feat(controller): change broker renewal'),
  ] }).version, '1.3.0');
});

test('restore operator and compiled safety helpers belong only to the broker release', () => {
  for (const file of ['scripts/delivery/auth-restore-plan.py', 'scripts/delivery/check_auth_restore_foundation.py',
    'scripts/operations/RestoreTable/RestoreCommand.cs', 'scripts/operations/RestoreTable.LocalCheck/Program.cs']) {
    const input = { currentVersion: '1.2.3', commits: [commit('fix: maintain isolated restore safety', [file])] };
    assert.equal(plan({ ...input, component: 'broker' })?.version, '1.2.4');
    assert.equal(plan({ ...input, component: 'controller' }), null);
  }
});

test('shared maintenance reaches both components and highest owned change wins', () => {
  const commits = [commit('fix(shared): update the reviewed SDK', ['global.json']),
    commit('feat(broker): add a compatible endpoint', [brokerFile], 'b')];
  assert.equal(plan({ component: 'broker', currentVersion: '1.2.3', commits }).version, '1.3.0');
  assert.equal(plan({ component: 'controller', currentVersion: '1.2.3', commits }).version, '1.2.4');
});

test('broker Docker build-context changes remain broker release inputs', () => {
  assert.equal(plan({ component: 'broker', currentVersion: '1.2.3', commits: [
    commit('fix(broker): include required runtime source', ['service/auth/Dockerfile.dockerignore']),
  ] }).version, '1.2.4');
});

test('internal-only broker docs, tests and refactors do not automatically publish', () => {
  for (const item of [
    commit('feat(broker): document broker behavior', ['service/auth/README.md']),
    commit('fix(broker): improve regression evidence', ['service/auth/tests/RepoAgent.Auth.Tests/BrokerCoreTests.cs']),
    commit('feat(broker): expand release-tool regression coverage', ['scripts/releases/broker-release-please.test.mjs']),
    commit('refactor(broker): rearrange the unchanged adapter'),
  ]) assert.equal(plan({ component: 'broker', currentVersion: '1.2.3', commits: [item] }), null);
});
