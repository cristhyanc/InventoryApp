import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const script = fileURLToPath(new URL('./agent-branch-cleanup.sh', import.meta.url));

// Real temporary repository and bare remote, as in the persistence tests.
function fixture(fn) {
  const root = mkdtempSync(join(tmpdir(), 'agent-cleanup-'));
  const repo = join(root, 'repo');
  const remote = join(root, 'remote.git');
  mkdirSync(repo);
  const git = (...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  try {
    git('init', '-b', 'develop');
    git('config', 'user.name', 'Fixture');
    git('config', 'user.email', 'fixture@example.invalid');
    const commit = () => {
      writeFileSync(join(repo, 'file'), Math.random().toString());
      git('add', '.');
      git('commit', '-m', 'change');
      return git('rev-parse', 'HEAD');
    };
    const base = commit();
    git('init', '--bare', remote);
    git('remote', 'add', 'origin', remote);
    const push = (sha, branch) => git('push', '--quiet', 'origin', `${sha}:refs/heads/${branch}`);
    const branches = () => git('ls-remote', '--heads', 'origin').split('\n').filter(Boolean).map(line => line.split('\t')[1].replace('refs/heads/', '')).sort();
    const run = (...args) => spawnSync('bash', [script, remote, ...args], { cwd: repo, encoding: 'utf8', env: { ...process.env, GH_TOKEN: '' } });
    fn({ git, base, commit, push, branches, run });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('contained mode deletes only storage branches already in the published head', () => fixture(({ git, base, commit, push, branches, run }) => {
  git('checkout', '--quiet', '-b', 'agent/issue-250-fixture');
  const first = commit();
  const published = commit();
  push(first, 'agent/issue-250-checkpoint-100-1');
  push(published, 'agent/issue-250-checkpoint-100-2');
  push(published, 'agent/recovery-250-90-1');
  git('checkout', '--quiet', '-b', 'unrelated', base);
  const unpublished = commit();
  push(unpublished, 'agent/recovery-250-80-1');
  push(published, 'agent/issue-250-fixture');
  push(published, 'agent/issue-2501-checkpoint-100-1');
  push(published, 'agent/issue-250-checkpoint-notes');

  const result = run('250', 'contained', published);
  assert.equal(result.status, 0, result.stderr + result.stdout);
  assert.deepEqual(branches(), [
    'agent/issue-250-checkpoint-notes',
    'agent/issue-250-fixture',
    'agent/issue-2501-checkpoint-100-1',
    'agent/recovery-250-80-1',
  ]);
}));

test('all mode deletes every storage branch of the issue and nothing else', () => fixture(({ base, commit, push, branches, run }) => {
  const other = commit();
  push(base, 'agent/issue-7-checkpoint-1-1');
  push(other, 'agent/recovery-7-2-1');
  push(base, 'agent/issue-7-feature');
  push(base, 'agent/issue-70-checkpoint-1-1');
  push(base, 'develop');

  const result = run('7', 'all');
  assert.equal(result.status, 0, result.stderr + result.stdout);
  assert.deepEqual(branches(), ['agent/issue-7-feature', 'agent/issue-70-checkpoint-1-1', 'develop']);
}));

test('no storage branches is a no-op', () => fixture(({ base, push, branches, run }) => {
  push(base, 'develop');
  const result = run('9', 'contained', base);
  assert.equal(result.status, 0, result.stderr + result.stdout);
  assert.deepEqual(branches(), ['develop']);
}));

test('invalid arguments are rejected without deleting anything', () => fixture(({ base, push, branches, run }) => {
  push(base, 'agent/issue-5-checkpoint-1-1');
  for (const args of [['abc', 'all'], ['5', 'everything'], ['5', 'contained'], ['5', 'contained', 'deadbeef']]) {
    assert.notEqual(run(...args).status, 0, args.join(' '));
  }
  assert.deepEqual(branches(), ['agent/issue-5-checkpoint-1-1']);
}));
