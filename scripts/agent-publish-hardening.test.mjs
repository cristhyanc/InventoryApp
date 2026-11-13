// Tests for the agent publishing hardening (audit #1227 findings WF-01, WF-02, WF-03 and WF-05):
// the real shell of the repair and architecture verification steps, the trusted publisher, and the
// contract checks that keep those properties in the workflows.
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';
import {
  architecturePath,
  implementPath,
  readRepositoryFile,
  repairPath,
  runContractChecks,
  verifyAgentPublishIsolation,
  verifyAppTokenStepsRunTrustedScripts,
  verifyTrustedAgentContext,
} from './validate-agent-workflows.mjs';

const publisher = fileURLToPath(new URL('./git-push-with-app-retry.sh', import.meta.url));

function stepShell(workflowPath, name) {
  const workflow = readRepositoryFile(workflowPath);
  const step = workflow.split(`      - name: ${name}\n`)[1]?.split('\n      - name: ')[0];
  assert.ok(step, `missing step ${name}`);
  return step.split('        run: |\n')[1].split('\n').map((line) => line.slice(10)).join('\n');
}

function fixture(fn) {
  const root = mkdtempSync(join(tmpdir(), 'agent-publish-'));
  const repo = join(root, 'repo');
  const remote = join(root, 'remote.git');
  mkdirSync(repo);
  const git = (...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  try {
    git('init', '-b', 'agent/issue-9-fix');
    git('config', 'user.name', 'Fixture');
    git('config', 'user.email', 'fixture@example.invalid');
    writeFileSync(join(repo, 'file'), 'base');
    git('add', '.');
    git('commit', '-m', 'base');
    const start = git('rev-parse', 'HEAD');
    execFileSync('git', ['init', '--quiet', '--bare', remote]);
    const commit = (path = 'file') => {
      mkdirSync(join(repo, path, '..'), { recursive: true });
      writeFileSync(join(repo, path), Math.random().toString());
      git('add', '.');
      git('commit', '-m', `change ${path}`);
      return git('rev-parse', 'HEAD');
    };
    const output = join(root, 'output');
    const run = (script, env = {}) => spawnSync('bash', ['-c', script], {
      cwd: repo,
      encoding: 'utf8',
      env: { ...process.env, GITHUB_OUTPUT: output, RUNNER_TEMP: root, ...env },
    });
    fn({ git, commit, run, start, repo, remote, output, root });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

for (const [label, path, name, branchVariable] of [
  ['repair', repairPath, 'Verify repair result', 'HEAD_REF'],
  ['architecture', architecturePath, 'Verify architecture result', 'BRANCH'],
]) {
  describe(`${label} verification step`, () => {
    const script = stepShell(path, name);
    const env = (start, worktree = 'clean') => ({ START_SHA: start, [branchVariable]: 'agent/issue-9-fix', AGENT_WORKTREE: worktree });

    it('accepts an ordinary new commit and asks for a push', () => fixture(({ commit, run, start, output }) => {
      const head = commit('backend/Fix.cs');
      const result = run(script, env(start));
      assert.equal(result.status, 0, result.stderr + result.stdout);
      const outputs = readFileSync(output, 'utf8');
      assert.ok(outputs.includes(`head_sha=${head}`));
      assert.ok(outputs.includes('push_required=true'));
    }));

    for (const protectedPath of ['.github/workflows/new.yml', '.agent-pr-body.md', 'inventory.db']) {
      it(`refuses a commit that touches ${protectedPath}, even when a later commit reverts it`, () => fixture(({ commit, git, run, start, output }) => {
        commit(protectedPath);
        git('rm', '--quiet', protectedPath);
        git('commit', '-m', 'revert');
        const result = run(script, env(start));
        assert.notEqual(result.status, 0);
        assert.match(result.stdout + result.stderr, /touches \.github, \.agent-\* or inventory\.db/);
        assert.ok(!readFileSync(output, { encoding: 'utf8', flag: 'a+' }).includes('push_required=true'));
      }));
    }

    it('refuses a result whose agent job left tracked changes behind', () => fixture(({ commit, run, start, output }) => {
      commit('backend/Fix.cs');
      const result = run(script, env(start, 'dirty'));
      assert.notEqual(result.status, 0);
      assert.match(result.stdout + result.stderr, /Tracked working-tree changes remain/);
      assert.ok(!readFileSync(output, { encoding: 'utf8', flag: 'a+' }).includes('push_required=true'));
    }));

    it('refuses rewritten history', () => fixture(({ commit, git, run, start }) => {
      commit('backend/Fix.cs');
      git('checkout', '--quiet', '--orphan', 'tmp');
      git('commit', '--quiet', '-m', 'unrelated');
      git('branch', '-D', 'agent/issue-9-fix');
      git('branch', '-m', 'agent/issue-9-fix');
      const result = run(script, env(start));
      assert.notEqual(result.status, 0);
      assert.match(result.stdout + result.stderr, /rewrote the pull request history/);
    }));
  });
}

describe('trusted publisher', () => {
  it('publishes from a clean repository: agent hooks and URL rewrites in .git/config never run', () => fixture(({ commit, git, run, repo, remote, start }) => {
    const head = commit('backend/Fix.cs');
    writeFileSync(join(repo, '.git', 'hooks', 'pre-push'), '#!/bin/sh\necho agent hook ran >&2\nexit 1\n', { mode: 0o755 });
    git('config', 'url.file:///nonexistent/.insteadOf', remote);
    git('config', 'core.fsmonitor', 'echo fsmonitor ran >&2; false');
    const result = run(`bash '${publisher}' '${remote}' '${head}:refs/heads/agent/issue-9-fix'`, { GH_TOKEN: 'fixture', AGENT_PUBLISH_BASE_SHA: start });
    assert.equal(result.status, 0, result.stderr + result.stdout);
    assert.doesNotMatch(result.stdout + result.stderr, /agent hook ran|fsmonitor ran/);
    assert.equal(execFileSync('git', ['--git-dir', remote, 'rev-parse', 'refs/heads/agent/issue-9-fix'], { encoding: 'utf8' }).trim(), head);
    const listed = run(`bash '${publisher}' --ls-remote '${remote}' refs/heads/agent/issue-9-fix`, { GH_TOKEN: 'fixture' });
    assert.equal(listed.status, 0, listed.stderr);
    assert.equal(listed.stdout.split('\t')[0], head);
  }));

  it('re-checks protected paths and the trusted base before pushing', () => fixture(({ commit, run, remote, start }) => {
    const head = commit('.github/workflows/new.yml');
    const refused = run(`bash '${publisher}' '${remote}' '${head}:refs/heads/agent/issue-9-fix'`, { GH_TOKEN: 'fixture', AGENT_PUBLISH_BASE_SHA: start });
    assert.notEqual(refused.status, 0);
    assert.match(refused.stdout + refused.stderr, /refusing to publish/);
    const unrelated = run(`bash '${publisher}' '${remote}' '${head}:refs/heads/agent/issue-9-fix'`, { GH_TOKEN: 'fixture', AGENT_PUBLISH_BASE_SHA: 'a'.repeat(40) });
    assert.notEqual(unrelated.status, 0);
    assert.equal(spawnSync('git', ['--git-dir', remote, 'rev-parse', '--verify', 'refs/heads/agent/issue-9-fix']).status, 128);
  }));

  it('refuses a destination that is not a branch', () => fixture(({ commit, run, remote }) => {
    const head = commit();
    const result = run(`bash '${publisher}' '${remote}' '${head}:refs/tags/v1'`, { GH_TOKEN: 'fixture' });
    assert.notEqual(result.status, 0);
  }));
});

function readWith(overrides) {
  return (path) => (Object.hasOwn(overrides, path) ? overrides[path] : readRepositoryFile(path));
}

function replaceOnce(text, from, to) {
  assert.ok(text.includes(from), `fixture must contain: ${from}`);
  return text.replace(from, to);
}

describe('publishing hardening contract', () => {
  const implement = readRepositoryFile(implementPath);
  const architecture = readRepositoryFile(architecturePath);
  const repair = readRepositoryFile(repairPath);

  it('accepts the repository workflows', () => {
    assert.doesNotThrow(() => verifyAppTokenStepsRunTrustedScripts());
    assert.doesNotThrow(() => verifyTrustedAgentContext());
    assert.doesNotThrow(() => verifyAgentPublishIsolation());
  });

  it('rejects an App token, the publisher or extra steps in the agent job', () => {
    for (const [path, workflow, agentStep, packageStep] of [
      [implementPath, implement, '      - name: Run Claude Code implementation agent\n', '      - name: Package implementation result for the publish job\n'],
      [repairPath, repair, '      - name: Run Claude Code repair agent\n', '      - name: Package repair result for the publish job\n'],
      [architecturePath, architecture, '      - name: Run Claude Code architecture agent\n', '      - name: Package architecture result for the publish job\n'],
    ]) {
      const token = replaceOnce(workflow, agentStep, '      - name: Mint token early\n        uses: actions/create-github-app-token@bcd2ba49218906704ab6c1aa796996da409d3eb1 # v3.2.0\n\n' + agentStep);
      assert.throws(() => verifyAgentPublishIsolation(readWith({ [path]: token })), /agent job must hold no App credential/, path);
      const extra = replaceOnce(workflow, packageStep, '      - name: Push from the agent job\n        run: git push origin HEAD\n\n' + packageStep);
      assert.throws(() => verifyAgentPublishIsolation(readWith({ [path]: extra })), /only the package and upload steps may follow the agent/, path);
      const env = replaceOnce(workflow, packageStep + '        id: package\n', packageStep + '        id: package\n        # echo "PATH=/tmp" >> "$GITHUB_PATH"\n');
      assert.throws(() => verifyAgentPublishIsolation(readWith({ [path]: env })), /package step only writes plain data/, path);
    }
  });

  it('rejects agent job outputs written after the agent ran', () => {
    const unsafe = replaceOnce(repair, '      claude_outcome: ${{ steps.claude.outcome }}\n', '      claude_outcome: ${{ steps.claude.outcome }}\n      head_sha: ${{ steps.package.outputs.head_sha }}\n');
    assert.throws(() => verifyAgentPublishIsolation(readWith({ [repairPath]: unsafe })), /output head_sha must come from a step that ran before the agent/);
  });

  it('rejects a publish job that runs an agent, skips the trusted handoff or mints the token first', () => {
    const agent = replaceOnce(implement, '      - name: Restore the agent result\n', '      - name: Agent in publish job\n        uses: anthropics/claude-code-action@cfc3eb22bfed5c26ef66e3223c982af27e4524de # v1.0.231\n\n      - name: Restore the agent result\n');
    assert.throws(() => verifyAgentPublishIsolation(readWith({ [implementPath]: agent })), /publish job must never run an agent/);
    const handoff = replaceOnce(repair, 'contents/scripts/agent-result-handoff.sh?ref=$GITHUB_WORKFLOW_SHA" > "$handoff"', 'contents/scripts/agent-result-handoff.sh?ref=$HEAD_REF" > "$handoff"');
    assert.throws(() => verifyAgentPublishIsolation(readWith({ [repairPath]: handoff })), /isolation: missing required text/);
    const early = replaceOnce(architecture, '      - name: Restore the architecture result\n', '      - name: Early token\n        uses: actions/create-github-app-token@bcd2ba49218906704ab6c1aa796996da409d3eb1 # v3.2.0\n\n      - name: Restore the architecture result\n');
    assert.throws(() => verifyAgentPublishIsolation(readWith({ [architecturePath]: early })), /restored before any App token/);
  });

  it('rejects an App-token step that runs a script from the agent-edited checkout', () => {
    for (const [path, workflow, from] of [
      [repairPath, repair, 'bash "$publisher" "https://github.com/${GITHUB_REPOSITORY}.git" "$HEAD_SHA:refs/heads/$HEAD_REF"'],
      [architecturePath, architecture, 'bash "$publisher" "https://github.com/${GITHUB_REPOSITORY}.git" "$HEAD_SHA:refs/heads/$BRANCH"'],
      [implementPath, implement, 'bash "$publisher" "https://github.com/${GITHUB_REPOSITORY}.git" "$HEAD_SHA:refs/heads/$BRANCH"'],
    ]) {
      const unsafe = replaceOnce(workflow, from, 'bash scripts/git-push-with-app-retry.sh "https://github.com/${GITHUB_REPOSITORY}.git" "HEAD:refs/heads/x"');
      assert.throws(() => verifyAppTokenStepsRunTrustedScripts(readWith({ [path]: unsafe })), /agent-editable checkout/);
    }
  });

  it('rejects a publisher taken from the agent repository instead of the workflow commit', () => {
    const unsafe = replaceOnce(
      repair,
      'gh api -H "Accept: application/vnd.github.raw" "repos/$GITHUB_REPOSITORY/contents/scripts/git-push-with-app-retry.sh?ref=$GITHUB_WORKFLOW_SHA" > "$publisher"',
      'git show HEAD:scripts/git-push-with-app-retry.sh > "$publisher"',
    );
    assert.throws(() => verifyAppTokenStepsRunTrustedScripts(readWith({ [repairPath]: unsafe })), /not fetched from the workflow's own commit/);
  });

  it('rejects credentialed git run directly in an App-token step', () => {
    const unsafe = replaceOnce(
      repair,
      'AGENT_PUBLISH_BASE_SHA="$START_SHA" bash "$publisher"',
      'git -c core.hooksPath=/dev/null push "https://x-access-token:${GH_TOKEN}@github.com/${GITHUB_REPOSITORY}.git" "HEAD:refs/heads/$HEAD_REF"\n          AGENT_PUBLISH_BASE_SHA="$START_SHA" bash "$publisher"',
    );
    assert.throws(() => verifyAppTokenStepsRunTrustedScripts(readWith({ [repairPath]: unsafe })), /trusted publisher/);
  });

  it('rejects a repair that can push workflow files or skips the protected-path check', () => {
    const withWorkflows = replaceOnce(repair, '          permission-contents: write\n\n      - name: Push repaired head', '          permission-contents: write\n          permission-workflows: write\n\n      - name: Push repaired head');
    assert.throws(() => runContractChecks({ read: readWith({ [repairPath]: withWorkflows }) }), /repair App token: contains forbidden text: permission-workflows/);
    const unchecked = replaceOnce(repair, 'done < <(git rev-list "$START_SHA..$head_sha")', 'done < /dev/null');
    assert.throws(() => runContractChecks({ read: readWith({ [repairPath]: unchecked }) }), /repair result: missing required text/);
    const architectureUnchecked = replaceOnce(architecture, 'done < <(git rev-list "$START_SHA..$head_sha")', 'done < /dev/null');
    assert.throws(() => runContractChecks({ read: readWith({ [architecturePath]: architectureUnchecked }) }), /architecture result: missing required text/);
  });

  it('rejects open-ended dotnet or npm commands and direct issue or PR readers in agent tool lists', () => {
    for (const [path, workflow, anchor] of [
      [implementPath, implement, 'Bash(dotnet restore *),'],
      [architecturePath, architecture, 'Bash(dotnet restore *),'],
      [repairPath, repair, 'Bash(dotnet restore *),'],
    ]) {
      for (const added of ['Bash(dotnet *),', 'Bash(npm --prefix frontend/inventory-app *),', 'Bash(gh issue view *),', 'Bash(gh pr view *),']) {
        const unsafe = replaceOnce(workflow, anchor, `${added}${anchor}`);
        assert.throws(() => verifyTrustedAgentContext(readWith({ [path]: unsafe })), /allowed tools: contains forbidden text/, `${path} ${added}`);
      }
    }
  });

  it('requires the trusted context to be written before the agent and read by it', () => {
    const missing = replaceOnce(implement, 'node "$context" issue "$ISSUE_NUMBER" .git/agent-issue-context.md', 'true');
    assert.throws(() => verifyTrustedAgentContext(readWith({ [implementPath]: missing })), /trusted context: missing required text/);
    const comments = replaceOnce(implement, 'Read the issue from the file `.git/agent-issue-context.md`', 'Read the issue with `gh issue view 1 --comments` and `.git/agent-issue-context.md`');
    assert.throws(() => verifyTrustedAgentContext(readWith({ [implementPath]: comments })), /agent prompt: contains forbidden text: --comments/);
    const untrusted = replaceOnce(repair, 'contents/scripts/agent-trusted-context.mjs?ref=$GITHUB_WORKFLOW_SHA" > "$context"', 'contents/scripts/agent-trusted-context.mjs?ref=$HEAD_REF" > "$context"');
    assert.throws(() => verifyTrustedAgentContext(readWith({ [repairPath]: untrusted })), /trusted context: missing required text/);
  });
});
