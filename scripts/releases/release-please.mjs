#!/usr/bin/env node
// Release Please owns version selection and changelogs. This adapter supplies source ownership before its path split.
import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { GitHub, Manifest, setLogger } from 'release-please';
import { Simple } from 'release-please/build/src/strategies/simple.js';
import { parseConventionalCommits } from 'release-please/build/src/commit.js';
import { Version } from 'release-please/build/src/version.js';
import { TagName } from 'release-please/build/src/util/tag-name.js';
const quiet = { info() {}, warn() {}, error() {}, debug() {}, trace() {} };
const components = { controller: 'releases/controller', broker: 'service/auth' };
const ownership = JSON.parse(fs.readFileSync(new URL('./components.json', import.meta.url), 'utf8'));
function matches(file, patterns) { return patterns.some(pattern => pattern.endsWith('/') ? file.startsWith(pattern) : file === pattern); }
export function owners(files) {
  if (!Array.isArray(files)) throw new Error('Commit file inventory is required for component attribution');
  const runtime = files.filter(file => file.startsWith('.agents/skills/') || (!/(^|\/)(?:tests?|docs|dev)\//.test(file) && !/\.(?:md|http)$/.test(file)));
  return Object.keys(components).filter(component => runtime.some(file => matches(file, ownership.shared) || matches(file, ownership[component])));
}
export function attribute(github) {
  const iterator = github.mergeCommitIterator.bind(github);
  github.mergeCommitIterator = async function* (...args) {
    for await (const commit of iterator(...args)) {
      const files = owners(commit.files).map(component => `${components[component]}/.runtime-change`);
      // Upstream includeEmpty means an empty file list would release every component.
      yield { ...commit, files: files.length ? files : ['repoagent-internal/.no-runtime-change'] };
    }
  };
  return github;
}
async function plan(input) {
  const component = input.component;
  if (!(component in components)) throw new Error('Unknown release component');
  const commits = input.commits.filter(commit => owners(commit.files).includes(component));
  const strategy = new Simple({ github: { repository: { owner: 'repoagent', repo: 'controller' } }, targetBranch: 'main',
    path: components[component], component, initialVersion: '1.0.0', includeComponentInTag: true, includeVInTag: true, logger: quiet });
  const previous = input.currentVersion ? { tag: new TagName(Version.parse(input.currentVersion), component), sha: 'b'.repeat(40), notes: '' } : undefined;
  const candidate = await strategy.buildReleasePullRequest(parseConventionalCommits(commits, quiet), previous);
  return candidate ? { version: candidate.version.toString(), tag: `${component}-v${candidate.version}`, notes: candidate.body.releaseData[0].notes } : null;
}
async function main() {
  setLogger(quiet);
  if (process.argv[2] === 'plan') { console.log(JSON.stringify(await plan(JSON.parse(fs.readFileSync(0, 'utf8'))))); return; }
  if (process.argv[2] === 'propose') {
    if (process.env.GITHUB_REF !== 'refs/heads/main') throw new Error('Release proposals run only from main');
    const repository = process.env.GITHUB_REPOSITORY || '';
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) throw new Error('Repository is required');
    const [owner, repo] = repository.split('/');
    const token = process.env.GH_TOKEN || process.env.GITHUB_TOKEN;
    if (!token) throw new Error('GitHub authority is required');
    const github = attribute(await GitHub.create({ owner, repo, token }));
    const manifest = await Manifest.fromManifest(github, 'main');
    const releases = (await manifest.createReleases()).filter(Boolean);
    const pullRequests = (await manifest.createPullRequests()).filter(Boolean);
    console.log(JSON.stringify({ releases: releases.map(release => ({ tag: release.tagName, path: release.path, version: release.version, sha: release.sha })),
      pullRequests: pullRequests.map(pr => ({ branch: pr.headBranchName, number: pr.number })) }));
    return;
  }
  if (process.argv[2] === 'dispatch') {
    if (process.env.GITHUB_REF !== 'refs/heads/main') throw new Error('Release dispatch runs only from main');
    const repository = process.env.GITHUB_REPOSITORY || '';
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) throw new Error('Repository is required');
    const result = JSON.parse(fs.readFileSync(0, 'utf8'));
    const run = args => { const child = spawnSync('gh', ['workflow', 'run', ...args, '--repo', repository], { stdio: 'inherit', shell: false }); if (child.status !== 0) throw new Error('Explicit workflow dispatch failed; rerun after inspecting existing release artifacts'); };
    for (const pr of result.pullRequests) {
      if (!/^release-please--[A-Za-z0-9_./-]+$/.test(pr.branch)) throw new Error('Unexpected release PR branch');
      run(['controller.yml', '--ref', pr.branch, '-f', 'verify=true']);
      run(['broker.yml', '--ref', pr.branch, '-f', 'operation=verify']);
    }
    for (const release of result.releases) {
      if (/^controller-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(release.tag))
        run(['controller-release.yml', '--ref', 'main', '-f', `tag=${release.tag}`]);
      else if (/^broker-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(release.tag))
        run(['broker.yml', '--ref', 'main', '-f', 'operation=release', '-f', `release-tag=${release.tag}`]);
      else throw new Error('Unexpected release tag');
    }
    return;
  }
  throw new Error('Expected plan, propose or dispatch');
}
if (process.argv[1] === fileURLToPath(import.meta.url)) main().catch(error => { console.error(`Release proposal refused: ${error.message}`); process.exitCode = 1; });
