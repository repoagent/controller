import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
const cli = fileURLToPath(new URL('./release-please.mjs', import.meta.url));
function plan(input) {
  const result = spawnSync(process.execPath, [cli, 'plan'], { input: JSON.stringify(input), encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
}
test('the first controller release is 1.0.0 with actual generated feature notes', () => {
  const result = plan({ component: 'controller', commits: [{ sha: 'a'.repeat(40), message: 'feat(controller): add queued work', files: ['src/RepoAgentController/BridgeCommand.cs'] }] });
  assert.equal(result.version, '1.0.0');
  assert.match(result.notes, /add queued work/);
  assert.equal(result.tag, 'controller-v1.0.0');
});

test('real manifest keeps independent cutoffs when a shared change predates the latest controller release', async () => {
  const { Manifest } = await import('release-please');
  const { Version } = await import('release-please/build/src/version.js');
  const { attribute } = await import('./release-please.mjs');
  const quiet = { info() {}, warn() {}, error() {}, debug() {}, trace() {} };
  const commits = [
    { sha: 'd'.repeat(40), message: 'fix(controller): repair queue', files: ['src/RepoAgentController/BridgeCommand.cs'] },
    { sha: 'c'.repeat(40), message: 'chore: release controller1.0.0', files: ['releases/controller/version.txt'] },
    { sha: 'e'.repeat(40), message: 'feat: improve shared policy', files: ['Directory.Build.props'] },
    { sha: 'b'.repeat(40), message: 'chore: release broker1.0.0', files: ['service/auth/version.txt'] },
  ];
  const github = attribute({ repository: { owner: 'repoagent', repo: 'controller' },
    async *releaseIterator() { yield { tagName: 'controller-v1.0.0', sha: 'c'.repeat(40) }; yield { tagName: 'broker-v1.0.0', sha: 'b'.repeat(40) }; },
    async *mergeCommitIterator() { yield* commits; },
  });
  const config = { 'releases/controller': { releaseType: 'simple', component: 'controller', initialVersion: '1.0.0' },
    'service/auth': { releaseType: 'simple', component: 'broker', initialVersion: '1.0.0' } };
  const manifest = new Manifest(github, 'main', config, { 'releases/controller': Version.parse('1.0.0'), 'service/auth': Version.parse('1.0.0') }, { separatePullRequests: true, logger: quiet });
  const prs = await manifest.buildPullRequests();
  assert.deepEqual(prs.map(pr => pr.version.toString()), ['1.0.1', '1.1.0']);
});

test('real manifest never turns an internal-only feature commit into an empty release for every component', async () => {
  const { Manifest, setLogger } = await import('release-please');
  const { Version } = await import('release-please/build/src/version.js');
  const { attribute } = await import('./release-please.mjs');
  const quiet = { info() {}, warn() {}, error() {}, debug() {}, trace() {} };
  setLogger(quiet);
  const github = attribute({ repository: { owner: 'repoagent', repo: 'controller' },
    async *releaseIterator() { yield { tagName: 'controller-v1.0.0', sha: 'c'.repeat(40) }; yield { tagName: 'broker-v1.0.0', sha: 'b'.repeat(40) }; },
    async *mergeCommitIterator() {
      yield { sha: 'd'.repeat(40), message: 'feat(controller): document release policy', files: ['docs/releases.md'] };
      yield { sha: 'c'.repeat(40), message: 'chore: release controller', files: ['releases/controller/version.txt'] };
      yield { sha: 'b'.repeat(40), message: 'chore: release broker', files: ['service/auth/version.txt'] };
    },
  });
  const manifest = new Manifest(github, 'main', { 'releases/controller': { releaseType: 'simple', component: 'controller' }, 'service/auth': { releaseType: 'simple', component: 'broker' } },
    { 'releases/controller': Version.parse('1.0.0'), 'service/auth': Version.parse('1.0.0') }, { separatePullRequests: true, logger: quiet });
  assert.deepEqual(await manifest.buildPullRequests(), []);
});

test('documentation inside a runtime source tree is not an artifact change', () => {
  assert.equal(plan({ component: 'broker', currentVersion: '1.0.0', commits: [{ sha: 'a'.repeat(40), message: 'feat(broker): document infrastructure', files: ['infra/auth/README.md'] }] }), null);
});
