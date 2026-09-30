import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync, execFileSync } from 'node:child_process';
import { test } from 'node:test';
const workflow = readFileSync(new URL('../.github/workflows/agent-implement.yml', import.meta.url), 'utf8');
function shell(name) {
  const step = workflow.split(`      - name: ${name}\n`)[1]?.split('      - name: ')[0];
  assert.ok(step, `missing trusted step: ${name}`);
  return step.split('        run: |\n')[1].split('\n').map(line => line.slice(10)).join('\n');
}
function fixture(fn) {
  const root = mkdtempSync(join(tmpdir(), 'agent-persist-'));
  const repo = join(root, 'repo'), remote = join(root, 'remote.git');
  mkdirSync(repo);
  const git = (...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  try {
    git('init', '-b', 'develop'); git('config', 'user.name', 'Fixture'); git('config', 'user.email', 'fixture@example.invalid');
    // The persist step calls the push helper by relative path, so the fixture repo needs its own copy.
    mkdirSync(join(repo, 'scripts'));
    writeFileSync(join(repo, 'scripts', 'git-push-with-app-retry.sh'), readFileSync(new URL('./git-push-with-app-retry.sh', import.meta.url)));
    writeFileSync(join(repo, 'file'), 'base'); git('add', '.'); git('commit', '-m', 'base');
    const base = git('rev-parse', 'HEAD'); git('init', '--bare', remote); git('remote', 'add', 'origin', remote);
    git('checkout', '-b', 'agent/issue-250-fixture');
    const commit = (path = 'file') => { mkdirSync(join(repo, path, '..'), { recursive: true }); writeFileSync(join(repo, path), Math.random().toString()); git('add', '.'); git('commit', '-m', 'implementation'); return git('rev-parse', 'HEAD'); };
    const output = join(root, 'output'), summary = join(root, 'summary');
    const run = (name, extra = {}) => spawnSync('bash', ['-c', shell(name)], { cwd: repo, encoding: 'utf8', env: { ...process.env, ISSUE_NUMBER: '250', RUN_ID: '123', RUN_ATTEMPT: '1', BASE_SHA: base, EXPECTED_HEAD: git('rev-parse', 'HEAD'), GH_TOKEN: 'fixture-token', CLAUDE_OUTCOME: 'success', PUBLISH_REMOTE: remote, GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary, ...extra } });
    fn({ git, base, commit, run, output, summary, repo, remote });
  } finally { rmSync(root, { recursive: true, force: true }); }
}
for (const stage of ['postcondition/validation', 'architecture', 'PR metadata', 'PR creation']) {
  test(`failure during ${stage} leaves exact implementation remotely persisted`, () => fixture(({ commit, run, git }) => {
    const head = commit(); const result = run('Persist committed implementation'); assert.equal(result.status, 0, result.stderr + result.stdout);
    // A later stage fails. No cleanup/deletion is allowed to undo the saved ref.
    assert.equal(spawnSync('bash', ['-c', 'exit 1']).status, 1);
    assert.equal(git('ls-remote', 'origin', 'refs/heads/agent/issue-250-checkpoint-123-1').split('\t')[0], head);
  }));
}
test('non-zero agent exit preserves recovery without reporting readiness', () => fixture(({ commit, run, git, output }) => {
  const head = commit(); const result = run('Persist committed implementation', { CLAUDE_OUTCOME: 'failure' }); assert.equal(result.status, 0, result.stderr);
  assert.equal(git('ls-remote', 'origin', 'refs/heads/agent/recovery-250-123-1').split('\t')[0], head);
  assert.ok(!readFileSync(output, 'utf8').includes('ready=true'));
  assert.ok(workflow.includes("if: success() && steps.claude.outcome == 'success'"));
}));
test('no new commit produces no recovery ref', () => fixture(({ run, git }) => {
  const result = run('Persist committed implementation', { CLAUDE_OUTCOME: 'failure' }); assert.equal(result.status, 0, result.stderr); assert.equal(git('ls-remote', 'origin'), '');
}));
for (const extra of [{ BASE_SHA: 'a'.repeat(40) }, { EXPECTED_HEAD: 'b'.repeat(40) }]) test('unexpected base/head is rejected', () => fixture(({ commit, run, git }) => { commit(); assert.notEqual(run('Persist committed implementation', extra).status, 0); assert.equal(git('ls-remote', 'origin'), ''); }));
test('unrelated existing checkpoint is never overwritten', () => fixture(({ git, commit, run, base }) => {
  git('push', 'origin', `${base}:refs/heads/agent/issue-250-checkpoint-123-1`); commit(); assert.notEqual(run('Persist committed implementation').status, 0); assert.equal(git('ls-remote', 'origin', 'refs/heads/agent/issue-250-checkpoint-123-1').split('\t')[0], base);
}));
test('protected-file change reverted in later commit is still rejected', () => fixture(({ git, commit, run }) => {
  commit('.github/workflows/unsafe.yml'); git('rm', '.github/workflows/unsafe.yml'); git('commit', '-m', 'revert'); assert.notEqual(run('Persist committed implementation').status, 0); assert.equal(git('ls-remote', 'origin'), '');
}));
test('rerun resumes exact saved commit instead of starting from scratch', () => fixture(({ commit, run, git, output }) => {
  const head = commit(); assert.equal(run('Persist committed implementation').status, 0); git('checkout', 'develop');
  const result = run('Capture trusted base and resume checkpoint', { RUN_ATTEMPT: '2' }); assert.equal(result.status, 0, result.stderr + result.stdout); assert.equal(git('rev-parse', 'HEAD'), head); assert.ok(readFileSync(output, 'utf8').includes('resumed=true'));
}));
test('persistence precedes fragile verification and runs on failed agent outcomes', () => {
  assert.ok(workflow.indexOf('- name: Persist committed implementation') < workflow.indexOf('- name: Verify implementation result'));
  const step = workflow.split('- name: Persist committed implementation')[1]?.split('- name: ')[0]; assert.ok(step?.includes('if: always()'));
});
