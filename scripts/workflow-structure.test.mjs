// Tests for the structural workflow checks (audit findings WF-06, WF-10, WF-11 and WF-14).
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';
import { parseWorkflow, runWorkflowStructureChecks, verifyPinnedActions, WORKFLOW_DIRECTORY } from './workflow-structure.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const read = (path) => readFileSync(join(root, path), 'utf8');
const names = readdirSync(join(root, WORKFLOW_DIRECTORY)).filter((name) => name.endsWith('.yml'));
const withOverride = (file, change) => ({
  read: (path) => (path === `${WORKFLOW_DIRECTORY}/${file}` ? change(read(path)) : read(path)),
  list: () => names,
});
const replaceOnce = (text, from, to) => {
  assert.ok(text.includes(from), `fixture must contain: ${from}`);
  return text.replace(from, to);
};

describe('workflow structure checks', () => {
  it('accept every repository workflow', () => {
    assert.equal(runWorkflowStructureChecks(), names.length);
  });

  it('find every action reference, including reusable workflows', () => {
    let count = 0;
    for (const name of names) {
      const workflow = parseWorkflow(read(`${WORKFLOW_DIRECTORY}/${name}`), name);
      for (const job of workflow.jobs) {
        count += job.steps.filter((step) => step.uses).length;
        if (/^ {4}uses:/m.test(job.header)) count += 1;
      }
    }
    const raw = names.reduce((total, name) => total + (read(`${WORKFLOW_DIRECTORY}/${name}`).match(/^\s+(?:- )?uses: /gm) ?? []).length, 0);
    assert.equal(count, raw);
  });

  it('reject an action pinned by tag or branch (WF-06)', () => {
    for (const [file, from, to] of [
      ['deploy-testing.yml', 'actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093 # v4.3.0', 'actions/download-artifact@v4'],
      ['vm-manager.yml', 'actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4.4.0', 'actions/checkout@main'],
      ['deploy-production-release.yml', 'actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4.6.2', 'actions/upload-artifact@ea165f8'],
    ]) {
      assert.throws(() => runWorkflowStructureChecks(withOverride(file, (text) => replaceOnce(text, from, to))), /is not pinned to a full commit SHA/, file);
    }
  });

  it('reject a Claude Code invocation without an explicit model (WF-11)', () => {
    for (const [file, model] of [
      ['agent-architecture.yml', '            --model opus\n'],
      ['agent-repair.yml', '            --model opus\n'],
      ['agent-review.yml', '            --model sonnet\n'],
      ['agent-model-selection.yml', '            --model haiku\n'],
    ]) {
      assert.throws(() => runWorkflowStructureChecks(withOverride(file, (text) => replaceOnce(text, model, ''))), /explicit --model/, file);
    }
  });

  it('reject a job that runs Claude Code and mints the App token', () => {
    const unsafe = (text) => replaceOnce(
      text,
      '      - name: Run Claude Code repair agent\n',
      '      - name: Early token\n        uses: actions/create-github-app-token@bcd2ba49218906704ab6c1aa796996da409d3eb1 # v3.2.0\n\n      - name: Run Claude Code repair agent\n',
    );
    assert.throws(() => runWorkflowStructureChecks(withOverride('agent-repair.yml', unsafe)), /mints the GitHub App token/);
  });

  it('reject the App private key in job-level or workflow-level env of a job that runs Claude Code', () => {
    const secret = '${{ secrets.AGENT_AUTOMATION_APP_PRIVATE_KEY }}';
    const jobEnv = (text) => replaceOnce(text, '    concurrency:\n      group: agent-repair-pr-', `    env:\n      APP_KEY: ${secret}\n    concurrency:\n      group: agent-repair-pr-`);
    assert.throws(() => runWorkflowStructureChecks(withOverride('agent-repair.yml', jobEnv)), /job repair: runs Claude Code and also mints/);
    const workflowEnv = (text) => replaceOnce(text, 'permissions: {}\n', `permissions: {}\n\nenv:\n  APP_KEY: ${secret}\n`);
    assert.throws(() => runWorkflowStructureChecks(withOverride('agent-repair.yml', workflowEnv)), /job repair: runs Claude Code and also mints/);
  });

  it('reject workflow-level repair concurrency or a repair job without it (WF-10)', () => {
    const jobLevel = '    concurrency:\n      group: agent-repair-pr-${{ github.event.issue.number }}\n      cancel-in-progress: true\n';
    const workflowLevel = (text) => replaceOnce(text, 'permissions: {}\n', 'permissions: {}\n\nconcurrency:\n  group: agent-repair-pr-${{ github.event.issue.number }}\n  cancel-in-progress: true\n');
    assert.throws(() => runWorkflowStructureChecks(withOverride('agent-repair.yml', workflowLevel)), /workflow-level concurrency/);
    assert.throws(() => runWorkflowStructureChecks(withOverride('agent-repair.yml', (text) => replaceOnce(text, jobLevel, ''))), /repair job must take/);
  });

  it('refuse a workflow outside the expected layout instead of skipping it', () => {
    assert.throws(() => parseWorkflow('name: x\non: push\n', 'x.yml'), /no top-level jobs/);
    assert.throws(() => parseWorkflow('jobs:\n    misplaced: true\n', 'x.yml'), /unexpected line outside a job/);
    assert.throws(() => parseWorkflow('jobs:\n  build:\n    steps:\n        - run: echo\n', 'x.yml'), /steps must be a sequence/);
  });

  it('keep checking the jobs after a column-zero comment', () => {
    const commentedPublish = (text) => {
      const [agent, publish] = text.split('\n  publish:\n');
      assert.ok(publish, 'agent-repair.yml must have a publish job');
      const unpinned = publish.replace(/actions\/checkout@[0-9a-f]{40}[^\n]*/, 'actions/checkout@v4');
      return `${agent}\n# A column-zero comment between two jobs.\n  publish:\n${unpinned}`;
    };
    assert.throws(() => runWorkflowStructureChecks(withOverride('agent-repair.yml', commentedPublish)), /job publish .*actions\/checkout@v4 is not pinned/);
    const workflow = parseWorkflow('jobs:\n  first:\n    steps:\n      - run: echo\n# between jobs\n  second:\n    steps:\n      - uses: actions/checkout@v4\n', 'x.yml');
    assert.deepEqual(workflow.jobs.map((job) => job.name), ['first', 'second']);
    assert.throws(() => verifyPinnedActions('x.yml', workflow), /job second .*not pinned/);
  });

  it('read indentless steps the same as indented ones', () => {
    const indentless = (uses) => `jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n    - name: Check out\n      uses: ${uses}\n    - run: echo done\n    timeout-minutes: 5\n`;
    const pinned = parseWorkflow(indentless(`actions/checkout@${'a'.repeat(40)} # v4`), 'x.yml');
    assert.deepEqual(pinned.jobs[0].steps.map((step) => step.uses), [`actions/checkout@${'a'.repeat(40)}`, '']);
    assert.match(pinned.jobs[0].header, /timeout-minutes: 5/);
    verifyPinnedActions('x.yml', pinned);
    assert.throws(() => verifyPinnedActions('x.yml', parseWorkflow(indentless('actions/checkout@v4'), 'x.yml')), /actions\/checkout@v4 is not pinned/);
  });

  it('refuse quoted keys, which the field parser does not read', () => {
    for (const steps of [
      "      - 'uses': actions/checkout@v4\n",
      '      - "uses": actions/checkout@v4\n',
      "      - name: x\n        'uses': actions/checkout@v4\n",
      "    'steps':\n      - run: echo\n",
    ]) {
      assert.throws(() => parseWorkflow(`jobs:\n  build:\n    steps:\n${steps}`, 'x.yml'), /quoted key/, steps);
    }
    const quotedUses = (text) => replaceOnce(text, '        uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262', "        'uses': actions/checkout@v4 #");
    assert.throws(() => runWorkflowStructureChecks(withOverride('vm-manager.yml', quotedUses)), /quoted key/);
  });

  it('refuse a uses: the parser cannot attribute to a step', () => {
    for (const steps of [
      '      - run: echo\n    - uses: actions/checkout@v4\n',
      '      - {uses: actions/checkout@v4}\n',
      "      - {'uses': actions/checkout@v4}\n",
      '      - name: deep\n          uses: actions/checkout@v4\n',
    ]) {
      assert.throws(() => parseWorkflow(`jobs:\n  build:\n    steps:\n${steps}`, 'x.yml'), /outside the layout this check reads/, steps);
    }
  });

  it('treat top-level keys after jobs: as workflow-level', () => {
    const trailing = (text) => `${text.trimEnd()}\n\nconcurrency:\n  group: agent-repair-pr-\${{ github.event.issue.number }}\n  cancel-in-progress: true\n`;
    assert.throws(() => runWorkflowStructureChecks(withOverride('agent-repair.yml', trailing)), /workflow-level concurrency/);
  });
});
