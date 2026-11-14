// Tests for the structural workflow checks (audit findings WF-06, WF-10, WF-11 and WF-14).
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';
import { parseWorkflow, runWorkflowStructureChecks, WORKFLOW_DIRECTORY } from './workflow-structure.mjs';

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

  it('reject workflow-level repair concurrency or a repair job without it (WF-10)', () => {
    const jobLevel = '    concurrency:\n      group: agent-repair-pr-${{ github.event.issue.number }}\n      cancel-in-progress: true\n';
    const workflowLevel = (text) => replaceOnce(text, 'permissions: {}\n', 'permissions: {}\n\nconcurrency:\n  group: agent-repair-pr-${{ github.event.issue.number }}\n  cancel-in-progress: true\n');
    assert.throws(() => runWorkflowStructureChecks(withOverride('agent-repair.yml', workflowLevel)), /workflow-level concurrency/);
    assert.throws(() => runWorkflowStructureChecks(withOverride('agent-repair.yml', (text) => replaceOnce(text, jobLevel, ''))), /repair job must take/);
  });

  it('refuse a workflow outside the expected layout instead of skipping it', () => {
    assert.throws(() => parseWorkflow('name: x\non: push\n', 'x.yml'), /no top-level jobs/);
    assert.throws(() => parseWorkflow('jobs:\n    misplaced: true\n', 'x.yml'), /unexpected line before the first job/);
  });
});
