// Tests for the agent-to-publisher handoff (review of PR #1265): the agent job packages its result as
// plain data, and a separate publish job restores it with scripts/agent-result-handoff.sh before it
// mints the GitHub App token. These tests run the real workflow step shells and the real handoff
// script, including an agent environment that was poisoned in every way the agent job allows.
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';
import { readRepositoryFile, repairPath } from './validate-agent-workflows.mjs';

const handoff = fileURLToPath(new URL('./agent-result-handoff.sh', import.meta.url));
const publisher = fileURLToPath(new URL('./git-push-with-app-retry.sh', import.meta.url));
const BRANCH = 'agent/issue-9-fix';

function stepShell(workflowPath, name) {
  const step = readRepositoryFile(workflowPath).split(`      - name: ${name}\n`)[1]?.split('\n      - name: ')[0];
  assert.ok(step, `missing step ${name}`);
  return step.split('        run: |\n')[1].split('\n').map((line) => line.slice(10)).join('\n');
}

/** Writes a zip archive; each entry is [name, content] or [name, target, 'symlink'] or [name, null, 'dir']. */
function zip(path, entries) {
  const script = `
import json, sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w") as zf:
    for name, content, kind in json.loads(sys.argv[2]):
        info = zipfile.ZipInfo(name + ("/" if kind == "dir" else ""))
        if kind == "symlink":
            info.external_attr = (0o120777 << 16)
        elif kind == "dir":
            info.external_attr = (0o040755 << 16) | 0x10
        else:
            info.external_attr = (0o100644 << 16)
        zf.writestr(info, content or "")
`;
  execFileSync('python3', ['-I', '-c', script, path, JSON.stringify(entries.map(([n, c, k = 'file']) => [n, c, k]))]);
}

function zipDirectory(path, directory) {
  execFileSync('python3', ['-I', '-c', `
import os, sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w") as zf:
    for name in sorted(os.listdir(sys.argv[2])):
        zf.write(os.path.join(sys.argv[2], name), name)
`, path, directory]);
}

function fixture(fn) {
  const root = mkdtempSync(join(tmpdir(), 'agent-handoff-'));
  const remote = join(root, 'remote.git');
  const agentRepo = join(root, 'agent');
  const publishRepo = join(root, 'publish');
  const run = (cwd, args, env = process.env) => execFileSync('git', args, { cwd, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  try {
    execFileSync('git', ['init', '--quiet', '--bare', remote]);
    mkdirSync(agentRepo);
    run(agentRepo, ['init', '--quiet', '-b', BRANCH]);
    run(agentRepo, ['config', 'user.name', 'Fixture']);
    run(agentRepo, ['config', 'user.email', 'fixture@example.invalid']);
    writeFileSync(join(agentRepo, 'file'), 'base');
    run(agentRepo, ['add', '.']);
    run(agentRepo, ['commit', '--quiet', '-m', 'base']);
    const start = run(agentRepo, ['rev-parse', 'HEAD']);
    run(agentRepo, ['push', '--quiet', remote, `HEAD:refs/heads/${BRANCH}`]);
    // The publish job's checkout: the trusted starting commit, detached, in a fresh repository.
    run(root, ['clone', '--quiet', '--no-checkout', remote, publishRepo]);
    run(publishRepo, ['checkout', '--quiet', '--detach', start]);
    const commit = (path = 'backend/Fix.cs') => {
      mkdirSync(join(agentRepo, path, '..'), { recursive: true });
      writeFileSync(join(agentRepo, path), Math.random().toString());
      run(agentRepo, ['add', '.']);
      run(agentRepo, ['commit', '--quiet', '-m', `change ${path}`]);
      return run(agentRepo, ['rev-parse', 'HEAD']);
    };
    const output = join(root, 'publish-output');
    const restore = (archive, extraEnv = {}) => spawnSync('bash', [handoff, 'restore', 'agent-result-1-1'], {
      cwd: publishRepo,
      encoding: 'utf8',
      env: { ...process.env, GITHUB_OUTPUT: output, RUNNER_TEMP: root, AGENT_RESULT_ZIP: archive, ...extraEnv },
    });
    fn({ root, remote, agentRepo, publishRepo, start, commit, restore, output, git: (args) => run(publishRepo, args) });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function outputs(path) {
  return Object.fromEntries(readFileSync(path, 'utf8').trim().split('\n').map((line) => line.split(/=(.*)/s).slice(0, 2)));
}

describe('agent result handoff', () => {
  it('restores the agent commits and run files from a well-formed result', () => fixture(({ root, start, commit, restore, output, git, agentRepo }) => {
    const head = commit();
    execFileSync('git', ['bundle', 'create', join(root, 'result.bundle'), `${start}..refs/heads/${BRANCH}`], { cwd: agentRepo, stdio: 'ignore' });
    const archive = join(root, 'result.zip');
    zip(archive, [['branch', `${BRANCH}\n`], ['head_sha', `${head}\n`], ['worktree', 'clean\n'], ['run-status', 'success\n'], ['pr-title', 'Fix it\n']]);
    execFileSync('python3', ['-I', '-c', 'import sys, zipfile; zipfile.ZipFile(sys.argv[1], "a").write(sys.argv[2], "result.bundle")', archive, join(root, 'result.bundle')]);
    const result = restore(archive);
    assert.equal(result.status, 0, result.stderr + result.stdout);
    assert.deepEqual(outputs(output), { restored: 'true', branch: BRANCH, head_sha: head, worktree: 'clean', has_bundle: 'true' });
    assert.equal(git(['rev-parse', 'HEAD']), head);
    assert.equal(git(['branch', '--show-current']), BRANCH);
    assert.equal(readFileSync(join(git(['rev-parse', '--show-toplevel']), '.agent-run-status'), 'utf8'), 'success\n');
  }));

  it('restores a result with no new commits on the starting commit', () => fixture(({ root, start, restore, output, git }) => {
    const archive = join(root, 'result.zip');
    zip(archive, [['branch', `${BRANCH}\n`], ['head_sha', `${start}\n`], ['worktree', 'clean\n']]);
    const result = restore(archive);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(outputs(output).has_bundle, 'false');
    assert.equal(git(['branch', '--show-current']), BRANCH);
  }));

  const bad = [
    ['a symbolic link', (head) => [['branch', BRANCH], ['head_sha', head], ['worktree', 'clean'], ['run-status', '/etc/passwd', 'symlink']], /Unexpected entry/],
    ['an unknown file', (head) => [['branch', BRANCH], ['head_sha', head], ['worktree', 'clean'], ['.bashrc', 'evil']], /Unexpected entry/],
    ['a directory', (head) => [['branch', BRANCH], ['head_sha', head], ['worktree', 'clean'], ['result.bundle', null, 'dir']], /Unexpected entry/],
    ['a path outside the result', (head) => [['branch', BRANCH], ['head_sha', head], ['worktree', 'clean'], ['../pr-title', 'x']], /Unexpected entry/],
    ['a malformed head', () => [['branch', BRANCH], ['head_sha', 'HEAD'], ['worktree', 'clean']], /no valid head SHA/],
    ['an option-like branch', (head) => [['branch', '--upload-pack=touch /tmp/x'], ['head_sha', head], ['worktree', 'clean']], /invalid branch/],
    ['an unknown worktree state', (head) => [['branch', BRANCH], ['head_sha', head], ['worktree', 'maybe']], /invalid working-tree state/],
    ['a missing head', () => [['branch', BRANCH], ['worktree', 'clean']], /no head_sha entry/],
    ['a new head without a bundle', () => [['branch', BRANCH], ['head_sha', 'a'.repeat(40)], ['worktree', 'clean']], /uploaded no bundle/],
  ];
  for (const [label, entries, message] of bad) {
    it(`refuses a result with ${label}`, () => fixture(({ root, start, restore, output, git }) => {
      const archive = join(root, 'result.zip');
      zip(archive, entries(start));
      const result = restore(archive);
      assert.notEqual(result.status, 0);
      assert.match(result.stdout + result.stderr, message);
      assert.ok(!existsSync(output) || !readFileSync(output, 'utf8').includes('restored=true'));
      assert.equal(git(['rev-parse', 'HEAD']), start);
    }));
  }

  it('refuses a bundle whose branch does not match the reported head', () => fixture(({ root, start, commit, restore, agentRepo }) => {
    commit();
    execFileSync('git', ['bundle', 'create', join(root, 'result.bundle'), `${start}..refs/heads/${BRANCH}`], { cwd: agentRepo, stdio: 'ignore' });
    const archive = join(root, 'result.zip');
    zip(archive, [['branch', BRANCH], ['head_sha', 'b'.repeat(40)], ['worktree', 'clean']]);
    execFileSync('python3', ['-I', '-c', 'import sys, zipfile; zipfile.ZipFile(sys.argv[1], "a").write(sys.argv[2], "result.bundle")', archive, join(root, 'result.bundle')]);
    const result = restore(archive);
    assert.notEqual(result.status, 0);
    assert.match(result.stdout + result.stderr, /does not match the reported head/);
  }));
});

describe('poisoned agent environment', () => {
  it('cannot influence the publish job: only committed history crosses the handoff', () => fixture(({ root, remote, agentRepo, publishRepo, start, commit, restore, output, git }) => {
    const head = commit();
    const marker = join(root, 'poison-ran');
    // Everything the agent job can poison after the agent ran: a replacement gh and git on PATH,
    // GITHUB_PATH and GITHUB_ENV entries, Git hooks, fsmonitor, URL rewrites and an untracked file.
    const poisonBin = join(root, 'poison-bin');
    mkdirSync(poisonBin);
    for (const tool of ['gh', 'node', 'python3']) {
      writeFileSync(join(poisonBin, tool), `#!/bin/sh\necho ${tool} >> '${marker}'\nexit 1\n`, { mode: 0o755 });
    }
    const agentPathFile = join(root, 'agent-github-path');
    const agentEnvFile = join(root, 'agent-github-env');
    writeFileSync(agentPathFile, `${poisonBin}\n`);
    writeFileSync(agentEnvFile, `GIT_SSH_COMMAND=touch '${marker}'\nGH_TOKEN=stolen\nLD_PRELOAD=${join(poisonBin, 'evil.so')}\n`);
    writeFileSync(join(agentRepo, '.git', 'hooks', 'pre-push'), `#!/bin/sh\ntouch '${marker}'\n`, { mode: 0o755 });
    writeFileSync(join(agentRepo, '.git', 'hooks', 'post-checkout'), `#!/bin/sh\ntouch '${marker}'\n`, { mode: 0o755 });
    execFileSync('git', ['config', 'core.fsmonitor', `touch '${marker}'`], { cwd: agentRepo });
    execFileSync('git', ['config', `url.file:///nonexistent/.insteadOf`, remote], { cwd: agentRepo });

    // The agent job's real package step, run with the poisoned PATH (gh is replaced; git is not, so the
    // bundle is produced). Its output is the only thing the publish job receives.
    const agentTemp = join(root, 'agent-temp');
    mkdirSync(agentTemp);
    const packaged = spawnSync('bash', ['-c', stepShell(repairPath, 'Package repair result for the publish job')], {
      cwd: agentRepo,
      encoding: 'utf8',
      env: { ...process.env, PATH: `${poisonBin}:${process.env.PATH}`, RUNNER_TEMP: agentTemp, HEAD_REF: BRANCH, START_SHA: start, GITHUB_PATH: agentPathFile, GITHUB_ENV: agentEnvFile },
    });
    assert.equal(packaged.status, 0, packaged.stderr);
    const archive = join(root, 'artifact.zip');
    zipDirectory(archive, join(agentTemp, 'agent-result'));
    // The poison may fire inside the agent job itself (fsmonitor runs there); only the publish job matters.
    rmSync(marker, { force: true });

    // The publish job: a fresh environment that never sees the agent job's PATH, GITHUB_PATH, GITHUB_ENV
    // or .git directory.
    const publishEnv = { PATH: process.env.PATH, HOME: process.env.HOME ?? root, GITHUB_OUTPUT: output, RUNNER_TEMP: root };
    const restored = restore(archive, publishEnv);
    assert.equal(restored.status, 0, restored.stderr + restored.stdout);
    const values = outputs(output);
    assert.equal(values.head_sha, head);
    assert.equal(git(['rev-parse', 'HEAD']), head);
    assert.doesNotMatch(git(['config', '--list', '--local']), /insteadof|fsmonitor/i);
    assert.ok(!existsSync(join(publishRepo, '.git', 'hooks', 'pre-push')));

    const verify = spawnSync('bash', ['-c', stepShell(repairPath, 'Verify repair result')], {
      cwd: publishRepo,
      encoding: 'utf8',
      env: { ...publishEnv, HEAD_REF: BRANCH, START_SHA: start, AGENT_WORKTREE: values.worktree },
    });
    assert.equal(verify.status, 0, verify.stderr + verify.stdout);
    const pushed = spawnSync('bash', [publisher, remote, `${head}:refs/heads/${BRANCH}`], {
      cwd: publishRepo,
      encoding: 'utf8',
      env: { ...publishEnv, GH_TOKEN: 'fixture', AGENT_PUBLISH_BASE_SHA: start },
    });
    assert.equal(pushed.status, 0, pushed.stderr + pushed.stdout);
    assert.equal(execFileSync('git', ['--git-dir', remote, 'rev-parse', `refs/heads/${BRANCH}`], { encoding: 'utf8' }).trim(), head);
    assert.ok(!existsSync(marker), `poisoned agent state ran in the publish job: ${existsSync(marker) ? readFileSync(marker, 'utf8') : ''}`);
  }));

  it('still refuses a poisoned result that touches workflow files', () => fixture(({ root, agentRepo, start, commit, restore, output, publishRepo }) => {
    commit('.github/workflows/evil.yml');
    const agentTemp = join(root, 'agent-temp');
    mkdirSync(agentTemp);
    const packaged = spawnSync('bash', ['-c', stepShell(repairPath, 'Package repair result for the publish job')], {
      cwd: agentRepo, encoding: 'utf8', env: { ...process.env, RUNNER_TEMP: agentTemp, HEAD_REF: BRANCH, START_SHA: start },
    });
    assert.equal(packaged.status, 0, packaged.stderr);
    const archive = join(root, 'artifact.zip');
    zipDirectory(archive, join(agentTemp, 'agent-result'));
    assert.equal(restore(archive).status, 0);
    const verify = spawnSync('bash', ['-c', stepShell(repairPath, 'Verify repair result')], {
      cwd: publishRepo, encoding: 'utf8', env: { ...process.env, GITHUB_OUTPUT: output, HEAD_REF: BRANCH, START_SHA: start, AGENT_WORKTREE: 'clean' },
    });
    assert.notEqual(verify.status, 0);
    assert.match(verify.stdout + verify.stderr, /touches \.github, \.agent-\* or inventory\.db/);
  }));
});
