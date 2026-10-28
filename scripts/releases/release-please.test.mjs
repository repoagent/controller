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
test('installer and runtime dependencies produce controller patches independently of the broker', () => {
  for (const file of ['scripts/local-runtime.psm1', 'scripts/private-file.psm1', 'scripts/revoke-approval.ps1',
    'scripts/image-input.ps1', 'scripts/repository-settings.ps1', 'scripts/controller-enrollment.psm1',
    'scripts/enroll-controller.ps1', 'scripts/setup-wizard.sh', 'scripts/build-image.sh', 'scripts/build-image.ps1',
    'scripts/live-test.sh', 'scripts/publish-image.ps1', 'scripts/delivery/verify.sh', 'scripts/delivery/verify-dotnet.sh', 'scripts/delivery/hosted-smoke.py']) {
    const input = { currentVersion: '1.2.3', commits: [{ sha: 'a'.repeat(40), message: 'fix(controller): repair installed runtime', files: [file] }] };
    assert.equal(plan({ ...input, component: 'controller' })?.version, '1.2.4', file);
    assert.equal(plan({ ...input, component: 'broker' }), null, file);
  }
});
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

test('real manifest never releases components for internal-only or ownership-metadata changes', async () => {
  const { Manifest, setLogger } = await import('release-please');
  const { Version } = await import('release-please/build/src/version.js');
  const { attribute } = await import('./release-please.mjs');
  const quiet = { info() {}, warn() {}, error() {}, debug() {}, trace() {} };
  setLogger(quiet);
  const github = attribute({ repository: { owner: 'repoagent', repo: 'controller' },
    async *releaseIterator() { yield { tagName: 'controller-v1.0.0', sha: 'c'.repeat(40) }; yield { tagName: 'broker-v1.0.0', sha: 'b'.repeat(40) }; },
    async *mergeCommitIterator() {
      yield { sha: 'e'.repeat(40), message: 'feat(broker): classify an additional broker input', files: ['scripts/releases/components.json'] };
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

test('an upstream tool major change is a controller patch with previous and new versions in notes', () => {
  const result = plan({ component: 'controller', currentVersion: '1.2.3', commits: [{ sha: 'a'.repeat(40),
    message: 'fix(controller): refresh bundled gh from 2.103.3 to 3.0.0', files: ['image/default.json', 'scripts/compose-image.py'] }] });
  assert.equal(result.version, '1.2.4');
  assert.match(result.notes, /2\.103\.3 to 3\.0\.0/);
});

test('real manifest keeps broker mapping features out of controller maintenance releases', async () => {
  const { Manifest, setLogger } = await import('release-please');
  const { Version } = await import('release-please/build/src/version.js');
  const { attribute } = await import('./release-please.mjs');
  const quiet = { info() {}, warn() {}, error() {}, debug() {}, trace() {} };
  setLogger(quiet);
  const github = attribute({ repository: { owner: 'repoagent', repo: 'controller' },
    async *releaseIterator() {
      yield { tagName: 'controller-v1.0.0', sha: 'c'.repeat(40) };
      yield { tagName: 'broker-v1.0.0', sha: 'b'.repeat(40) };
    },
    async *mergeCommitIterator() {
      yield { sha: 'f'.repeat(40), message: 'feat(broker): add guarded version probe',
        files: ['scripts/delivery/broker-console-version.py', '.github/workflows/broker.yml', 'scripts/releases/components.json'] };
      yield { sha: 'e'.repeat(40), message: 'fix(broker): bound execution history and provision verification dependency',
        files: ['scripts/delivery/broker-diagnostics.py', 'scripts/delivery/verify.sh'] };
      yield { sha: 'd'.repeat(40), message: 'feat(broker): add operational diagnostics',
        files: ['scripts/delivery/broker-diagnostics.py', '.github/workflows/broker.yml', 'scripts/releases/components.json'] };
      yield { sha: 'c'.repeat(40), message: 'chore: release controller', files: ['releases/controller/version.txt'] };
      yield { sha: 'b'.repeat(40), message: 'chore: release broker', files: ['service/auth/version.txt'] };
    },
  });
  const manifest = new Manifest(github, 'main', {
    'releases/controller': { releaseType: 'simple', component: 'controller' },
    'service/auth': { releaseType: 'simple', component: 'broker' },
  }, { 'releases/controller': Version.parse('1.0.0'), 'service/auth': Version.parse('1.0.0') },
  { separatePullRequests: true, logger: quiet });
  const prs = await manifest.buildPullRequests();
  assert.deepEqual(prs.map(pr => pr.version.toString()), ['1.0.1', '1.1.0']);
  assert.match(prs[0].body.releaseData[0].notes, /bound execution history/);
  assert.doesNotMatch(prs[0].body.releaseData[0].notes, /guarded version probe|operational diagnostics/);
  assert.match(prs[1].body.releaseData[0].notes, /guarded version probe/);
});

test('ownership metadata accompanying either component never releases the other component', () => {
  for (const [component, other, file] of [
    ['controller', 'broker', 'src/RepoAgentController/Program.cs'],
    ['broker', 'controller', 'service/auth/src/RepoAgent.Auth/Program.cs'],
  ]) {
    const input = { currentVersion: '1.0.0', commits: [{ sha: 'a'.repeat(40),
      message: `feat(${other}): add a supported capability`, files: [file, 'scripts/releases/components.json'] }] };
    assert.equal(plan({ ...input, component })?.version, '1.1.0');
    assert.equal(plan({ ...input, component: other }), null);
  }
});

test('ownership metadata alone does not propose either component release', () => {
  const commits = [{ sha: 'a'.repeat(40), message: 'feat: extend ownership mapping', files: ['scripts/releases/components.json'] }];
  for (const component of ['controller', 'broker']) {
    assert.equal(plan({ component, currentVersion: '1.0.0', commits }), null);
  }
});
