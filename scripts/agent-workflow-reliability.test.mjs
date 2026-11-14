// Runs the real step shells for two reliability fixes from the workflow audit:
//   WF-09: committed repair work that is not published is kept on a recovery branch, and the outcome comment
//          says where it is (or that it was lost) instead of reading like success.
//   WF-15: a validation run cancelled because a newer run took over the same SHA does not overwrite that run's
//          status with a failure.
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

const publisher = fileURLToPath(new URL('./git-push-with-app-retry.sh', import.meta.url));
const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');

function stepShell(workflowPath, name) {
  const step = read(workflowPath).split(`      - name: ${name}\n`)[1]?.split('\n      - name: ')[0]?.split('\n  dispatch-')[0];
  assert.ok(step, `missing step ${name}`);
  return step.split('        run: |\n')[1].split('\n').map((line) => line.slice(10)).join('\n');
}

function withTemp(fn) {
  const root = mkdtempSync(join(tmpdir(), 'agent-reliability-'));
  try {
    return fn(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

/** A fake `gh` that serves the trusted publisher, logs every call and answers fixed reads. */
function fakeGh(root, { prHead = '', latestStatusUrl = '', runSource = '' } = {}) {
  const bin = join(root, 'bin');
  mkdirSync(bin, { recursive: true });
  const log = join(root, 'gh.log');
  writeFileSync(join(bin, 'gh'), `#!/usr/bin/env bash
printf '%s\\n' "$*" >> '${log}'
case "$*" in
  *contents/scripts/git-push-with-app-retry.sh?ref=trusted-sha*) cat '${publisher}' ;;
  "pr view"*"--json headRefOid"*) printf '{"headRefOid":"%s"}\\n' '${prHead}' ;;
  "pr comment"*) ;;
  "api repos/owner/repo/commits/"*"/statuses?per_page=100"*) printf '%s\\n' '${latestStatusUrl}' ;;
  "api repos/owner/repo/actions/runs/"*) [ -n '${runSource}' ] || exit 1; printf '%s\\n' '${runSource}' ;;
  "api --method POST"*) ;;
  *) echo "unexpected gh $*" >&2; exit 1 ;;
esac
`, { mode: 0o755 });
  return { bin, log: () => (existsSync(log) ? readFileSync(log, 'utf8') : '') };
}

function repairFixture(fn) {
  return withTemp((root) => {
    const repo = join(root, 'repo');
    const remote = join(root, 'remote.git');
    mkdirSync(repo);
    const git = (...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
    git('init', '--quiet', '-b', 'agent/issue-77-fix');
    git('config', 'user.name', 'Fixture');
    git('config', 'user.email', 'fixture@example.invalid');
    writeFileSync(join(repo, 'file'), 'base');
    git('add', '.');
    git('commit', '--quiet', '-m', 'base');
    const start = git('rev-parse', 'HEAD');
    execFileSync('git', ['init', '--quiet', '--bare', remote]);
    const commit = (path = 'backend/Fix.cs') => {
      mkdirSync(join(repo, path, '..'), { recursive: true });
      writeFileSync(join(repo, path), Math.random().toString());
      git('add', '.');
      git('commit', '--quiet', '-m', `change ${path}`);
      return git('rev-parse', 'HEAD');
    };
    const temp = join(root, 'runner-temp');
    mkdirSync(temp);
    const output = join(root, 'output');
    const env = (gh, extra) => ({
      ...process.env, PATH: `${gh.bin}:${process.env.PATH}`, GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: join(root, 'summary'),
      GITHUB_REPOSITORY: 'owner/repo', GITHUB_WORKFLOW_SHA: 'trusted-sha', RUNNER_TEMP: temp, PUBLISH_REMOTE: remote,
      GH_TOKEN: 'fixture-token', START_SHA: start, ISSUE_NUMBER: '77', RUN_ID: '555', RUN_ATTEMPT: '1', PR_NUMBER: '9',
      RUN_URL: 'https://example.invalid/run', HEAD_REF: 'agent/issue-77-fix', ...extra,
    });
    const run = (name, gh, extra = {}) => spawnSync('bash', ['-c', stepShell('.github/workflows/agent-repair.yml', name)], { cwd: repo, encoding: 'utf8', env: env(gh, extra) });
    return fn({ root, repo, remote, git, start, commit, run, output });
  });
}

const remoteRef = (remote, ref) => spawnSync('git', ['--git-dir', remote, 'rev-parse', '--verify', '--quiet', ref], { encoding: 'utf8' }).stdout.trim();

describe('repair recovery (WF-09)', () => {
  it('saves committed repair work that was not published to a recovery branch', () => repairFixture(({ root, remote, commit, run, output }) => {
    const head = commit('frontend/inventory-app/src/app/app.component.html');
    const gh = fakeGh(root);
    const result = run('Persist committed repair', gh, { EXPECTED_HEAD: head });
    assert.equal(result.status, 0, result.stderr + result.stdout);
    assert.equal(remoteRef(remote, 'refs/heads/agent/recovery-77-555-1'), head);
    assert.match(readFileSync(output, 'utf8'), /saved_branch=agent\/recovery-77-555-1/);
    // Re-running the same attempt is idempotent; a different head is never written over it.
    assert.equal(run('Persist committed repair', gh, { EXPECTED_HEAD: head }).status, 0);
  }));

  it('refuses protected paths, rewritten history and an unexpected head', () => repairFixture(({ root, remote, git, commit, run }) => {
    const gh = fakeGh(root);
    const workflowHead = commit('.github/workflows/evil.yml');
    const refused = run('Persist committed repair', gh, { EXPECTED_HEAD: workflowHead });
    assert.notEqual(refused.status, 0);
    assert.match(refused.stdout + refused.stderr, /Protected control-plane or scratch files/);
    git('reset', '--quiet', '--hard', 'HEAD~1');
    assert.notEqual(run('Persist committed repair', gh, { EXPECTED_HEAD: 'a'.repeat(40) }).status, 0);
    git('checkout', '--quiet', '--orphan', 'other');
    git('commit', '--quiet', '-m', 'unrelated');
    const unrelated = git('rev-parse', 'HEAD');
    const rewritten = run('Persist committed repair', gh, { EXPECTED_HEAD: unrelated });
    assert.notEqual(rewritten.status, 0);
    assert.match(rewritten.stdout + rewritten.stderr, /not ahead of the pull request head/);
    assert.equal(remoteRef(remote, 'refs/heads/agent/recovery-77-555-1'), '');
  }));

  it('does nothing when the repair made no commit', () => repairFixture(({ root, remote, start, run }) => {
    const result = run('Persist committed repair', fakeGh(root), { EXPECTED_HEAD: start });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(remoteRef(remote, 'refs/heads/agent/recovery-77-555-1'), '');
  }));

  for (const [label, extra, expected] of [
    ['a dirty working tree, saved', { AGENT_WORKTREE: 'dirty', SAVED_BRANCH: 'agent/recovery-77-555-1', PERSIST_OUTCOME: 'success', REPAIR_RESULT_OUTCOME: 'failure' },
      [/saved on branch `agent\/recovery-77-555-1`/, /left tracked files modified and uncommitted/, /styles\.css/, /No verified new commit was published/]],
    ['a failed recovery', { AGENT_WORKTREE: 'clean', SAVED_BRANCH: '', PERSIST_OUTCOME: 'failure', REPAIR_RESULT_OUTCOME: 'failure' },
      [/could not be saved to a recovery branch \(recovery step: failure\)/]],
  ]) {
    it(`says in the outcome comment where unpublished work is: ${label}`, () => repairFixture(({ root, start, commit, run }) => {
      const head = commit();
      const gh = fakeGh(root, { prHead: start });
      const result = run('Record outcome on the pull request', gh, { CLAUDE_OUTCOME: 'success', EXPECTED_SHA: '', RESTORE_OUTCOME: 'success', RESTORED_HEAD: head, ...extra });
      assert.equal(result.status, 0, result.stderr + result.stdout);
      const comment = gh.log().slice(gh.log().indexOf('pr comment'));
      for (const pattern of expected) assert.match(comment, pattern);
      assert.ok(comment.includes(head));
    }));
  }

  for (const restoreOutcome of ['failure', '']) {
    it(`says no commit could be recovered when the hand-off artifact is missing (restore: ${restoreOutcome || 'not run'})`, () => repairFixture(({ root, start, run, output }) => {
      const gh = fakeGh(root, { prHead: start });
      const result = run('Record outcome on the pull request', gh, {
        CLAUDE_OUTCOME: 'cancelled', REPAIR_RESULT_OUTCOME: '', EXPECTED_SHA: '', RESTORE_OUTCOME: restoreOutcome, RESTORED_HEAD: '', AGENT_WORKTREE: '',
        SAVED_BRANCH: '', PERSIST_OUTCOME: '',
      });
      assert.equal(result.status, 0, result.stderr + result.stdout);
      const comment = gh.log().slice(gh.log().indexOf('pr comment'));
      assert.match(comment, new RegExp(`could not be handed to this job \\(restore step: ${restoreOutcome || 'not run'}\\)`));
      assert.match(comment, /cannot tell whether the agent committed anything/);
      assert.match(comment, /No verified new commit was published/);
      assert.doesNotMatch(comment, /saved on branch/);
      assert.match(readFileSync(output, 'utf8'), /dispatch_validation=false/);
      assert.doesNotMatch(readFileSync(output, 'utf8'), /dispatch_validation=true/);
    }));
  }

  it('adds no recovery note when the repair was published', () => repairFixture(({ root, commit, run, output }) => {
    const head = commit();
    const gh = fakeGh(root, { prHead: head });
    const result = run('Record outcome on the pull request', gh, {
      CLAUDE_OUTCOME: 'success', REPAIR_RESULT_OUTCOME: 'success', EXPECTED_SHA: head, RESTORE_OUTCOME: 'success', RESTORED_HEAD: head, AGENT_WORKTREE: 'clean',
    });
    assert.equal(result.status, 0, result.stderr);
    assert.doesNotMatch(gh.log(), /recovery branch|was not published/);
    assert.match(readFileSync(output, 'utf8'), /dispatch_validation=true/);
  }));
});

describe('cancelled validation status (WF-15)', () => {
  const script = stepShell('.github/workflows/validate.yml', 'Publish exact-SHA status');
  const runs = 'https://example.invalid/owner/repo/actions/runs';
  const run = (root, gh, extra) => spawnSync('bash', ['-c', script], {
    encoding: 'utf8',
    env: {
      ...process.env, PATH: `${gh.bin}:${process.env.PATH}`, GITHUB_REPOSITORY: 'owner/repo', GITHUB_STEP_SUMMARY: join(root, 'summary'),
      HEAD_SHA: 'c'.repeat(40), STATUS_CONTEXT: 'merge-validation', RUN_ID: '100', RUN_NUMBER: '10', EVENT_NAME: 'pull_request',
      RUN_URL: `${runs}/100`, ...extra,
    },
  });
  const posted = (gh) => gh.log().split('\n').filter((line) => line.startsWith('api --method POST'));
  const validation = (number, event = 'pull_request') => `.github/workflows/validate.yml ${event} ${number}`;

  // Run ids are identifiers, not an ordering: run_number decides which run is newer, whatever the ids are.
  for (const [label, latest, runSource] of [
    ['a newer run (higher run number) of the same workflow and mode owns the SHA', `${runs}/200`, validation(11)],
    ['the newer run has a lower run id but a higher run number', `${runs}/50`, validation(11)],
  ]) {
    it(`stands down when ${label}`, () => withTemp((root) => {
      const gh = fakeGh(root, { latestStatusUrl: latest, runSource });
      const result = run(root, gh, { VALIDATION_RESULT: 'cancelled' });
      assert.equal(result.status, 0, result.stderr);
      assert.deepEqual(posted(gh), []);
      assert.match(result.stdout, /a newer run .* run number 11\) already owns merge-validation/);
    }));
  }

  for (const [label, latest, runSource] of [
    ['this run posted the latest status', `${runs}/100`, validation(11)],
    ['no status exists yet', '', validation(11)],
    ['the latest status belongs to an older run (a stale success)', `${runs}/50`, validation(9)],
    ['a higher run id has a lower run number', `${runs}/200`, validation(9)],
    ['the other run has the same run number', `${runs}/200`, validation(10)],
    ['the newer run is another workflow', `${runs}/200`, '.github/workflows/agent-review.yml pull_request 11'],
    ['the newer run is the other validation mode', `${runs}/200`, validation(11, 'workflow_dispatch')],
    ['the newer run cannot be read', `${runs}/200`, ''],
    ['the run number is not a number', `${runs}/200`, '.github/workflows/validate.yml pull_request null'],
    ['the latest status links elsewhere', 'https://elsewhere.invalid/owner/repo/actions/runs/200', validation(11)],
    ['the latest status is not a run link', `${runs}/200/attempts/2`, validation(11)],
  ]) {
    it(`still reports a cancellation as a failure when ${label}`, () => withTemp((root) => {
      const gh = fakeGh(root, { latestStatusUrl: latest, runSource });
      const result = run(root, gh, { VALIDATION_RESULT: 'cancelled' });
      assert.equal(result.status, 0, result.stderr);
      assert.equal(posted(gh).length, 1);
      assert.match(posted(gh)[0], /state=failure/);
      assert.match(posted(gh)[0], /Validation cancelled/);
    }));
  }

  it('never looks up its own run, so every attempt of one run is treated as its own', () => withTemp((root) => {
    const gh = fakeGh(root, { latestStatusUrl: `${runs}/100`, runSource: validation(99) });
    assert.equal(run(root, gh, { VALIDATION_RESULT: 'cancelled' }).status, 0);
    assert.doesNotMatch(gh.log(), /api repos\/owner\/repo\/actions\/runs/);
    assert.equal(posted(gh).length, 1);
  }));

  it('publishes success and failure without looking at other runs', () => withTemp((root) => {
    const gh = fakeGh(root, { latestStatusUrl: `${runs}/200`, runSource: validation(11) });
    assert.equal(run(root, gh, { VALIDATION_RESULT: 'success' }).status, 0);
    assert.equal(run(root, gh, { VALIDATION_RESULT: 'failure' }).status, 0);
    assert.deepEqual(posted(gh).map((line) => /state=(\w+)/.exec(line)[1]), ['success', 'failure']);
    assert.doesNotMatch(gh.log(), /statuses\?per_page|api repos\/owner\/repo\/actions\/runs/);
  }));
});
