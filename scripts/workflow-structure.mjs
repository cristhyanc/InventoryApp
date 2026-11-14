// Structural checks over every GitHub Actions workflow (audit finding WF-14).
//
// The older contract in validate-agent-workflows.mjs mostly requires exact text, which misses a property
// that is phrased differently and turns every rewording into a contract change. These checks read the
// workflows' structure instead (jobs, their job-level keys and their steps) and assert properties that
// must hold however a step is worded:
//   - every action is pinned to a full commit SHA (WF-06);
//   - every Claude Code invocation names its model (WF-11);
//   - a job that runs Claude Code never mints the GitHub App token (the agent/publish split);
//   - repair concurrency is taken by the repair job, not the whole workflow (WF-10).
//
// The repository keeps no YAML dependency, so this reads the fixed layout every workflow here uses:
// top-level keys at column 0, jobs at two spaces under `jobs:`, job keys at four, and steps as a sequence
// whose items start at four (indentless) or six spaces. Column-zero comments are allowed anywhere. A line
// outside that layout fails the check instead of being skipped, and every `uses:` in the file must be
// attributed to a step or a job, so an action the parser could not place fails the check too.
//
// Run with: node scripts/workflow-structure.mjs
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const WORKFLOW_DIRECTORY = '.github/workflows';
const CLAUDE_CODE_ACTION = 'anthropics/claude-code-action@';
const APP_TOKEN_ACTION = 'actions/create-github-app-token@';
const JOB_HEADER = /^ {2}([A-Za-z0-9_-]+):\s*(?:#.*)?$/;
const STEPS_KEY = /^ {4}steps:\s*(?:#.*)?$/;
const USES_KEY = /(?:^|[\s{,-])uses\s*:/;

const isBlankOrComment = (line) => line.trim() === '' || line.trim().startsWith('#');

/** Splits a workflow into its top-level text and its jobs, each with job-level text and steps. */
export function parseWorkflow(text, source = 'workflow') {
  const lines = text.replaceAll('\r\n', '\n').split('\n');
  const jobsIndex = lines.indexOf('jobs:');
  if (jobsIndex < 0) throw new Error(`${source}: no top-level jobs: key`);
  const topLevel = lines.slice(0, jobsIndex);
  const jobs = [];
  let index = jobsIndex + 1;
  for (; index < lines.length; index += 1) {
    const line = lines[index];
    if (/^\S/.test(line) && !line.startsWith('#')) break;
    addJobLine(jobs, line, source);
  }
  // Top-level keys may also follow `jobs:`; they belong to the workflow, not to the last job.
  topLevel.push(...lines.slice(index));
  const workflow = { topLevel: topLevel.join('\n'), jobs: jobs.map(finishJob) };
  verifyEveryUsesIsAttributed(lines, workflow, source);
  return workflow;
}

function addJobLine(jobs, line, source) {
  const jobHeader = JOB_HEADER.exec(line);
  if (jobHeader) {
    jobs.push({ name: jobHeader[1], header: [], steps: [], inSteps: false, stepIndent: 0 });
    return;
  }
  const job = jobs.at(-1);
  if (isBlankOrComment(line)) {
    // Comments and blank lines never change the structure; inside a step they stay part of its text.
    if (job?.inSteps && job.steps.length > 0) job.steps.at(-1).lines.push(line);
    return;
  }
  if (!job || /^ {2}\S/.test(line)) throw new Error(`${source}: unexpected line outside a job: ${line}`);
  if (STEPS_KEY.test(line)) {
    job.inSteps = true;
    return;
  }
  if (job.inSteps) addStepLine(job, line, source);
  else job.header.push(line);
}

function addStepLine(job, line, source) {
  if (job.stepIndent === 0) {
    const first = /^( {4}| {6})- /.exec(line);
    if (!first) throw new Error(`${source} job ${job.name}: steps must be a sequence indented four or six spaces: ${line}`);
    job.stepIndent = first[1].length;
  }
  const indent = ' '.repeat(job.stepIndent);
  if (line.startsWith(`${indent}- `)) {
    job.steps.push({ indent: job.stepIndent, lines: [line] });
  } else if (/^ {4}\S/.test(line)) {
    job.inSteps = false;
    job.header.push(line);
  } else {
    job.steps.at(-1).lines.push(line);
  }
}

function finishJob({ name, header, steps }) {
  return {
    name,
    header: header.join('\n'),
    steps: steps.map(({ indent, lines }) => {
      const body = lines.join('\n');
      return { text: body, name: field(body, 'name', indent), id: field(body, 'id', indent), uses: field(body, 'uses', indent) };
    }),
  };
}

function withoutComment(value) {
  const comment = value.indexOf(' #');
  return (comment < 0 ? value : value.slice(0, comment)).trim();
}

function field(stepText, key, indent) {
  const prefixes = [`${' '.repeat(indent)}- ${key}: `, `${' '.repeat(indent + 2)}${key}: `];
  const line = stepText.split('\n').find((candidate) => prefixes.some((prefix) => candidate.startsWith(prefix)));
  return line ? withoutComment(line.slice(prefixes[0].length)) : '';
}

const jobUses = (job) => {
  const line = job.header.split('\n').find((candidate) => candidate.startsWith('    uses: '));
  return line ? withoutComment(line.slice('    uses: '.length)) : '';
};

/** Every non-comment line naming `uses:` must be one the parser attributed to a step or a job. */
function verifyEveryUsesIsAttributed(lines, workflow, source) {
  const written = lines.filter((line) => !isBlankOrComment(line) && USES_KEY.test(line)).length;
  const attributed = workflow.jobs.reduce((total, job) => total + job.steps.filter((step) => step.uses).length + (jobUses(job) ? 1 : 0), 0);
  if (written !== attributed) {
    throw new Error(`${source}: ${written - attributed} uses: reference(s) are outside the layout this check reads; keep every step under a job's steps: list.`);
  }
}

/** Every `uses:` names a local action or `owner/repo[/path]@<40-hex SHA>` (WF-06). */
export function verifyPinnedActions(file, workflow) {
  for (const job of workflow.jobs) {
    const uses = [...job.steps.map((step) => [step.name || step.id || 'unnamed step', step.uses]), ['job', jobUses(job)]];
    for (const [where, value] of uses) {
      if (!value || value.startsWith('./')) continue;
      if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_./-]+@[0-9a-f]{40}$/.test(value)) {
        throw new Error(`${file} job ${job.name} (${where}): action ${value} is not pinned to a full commit SHA.`);
      }
    }
  }
}

/** Every Claude Code invocation names its model, so the model never silently follows a default (WF-11). */
export function verifyClaudeModels(file, workflow) {
  for (const job of workflow.jobs) {
    for (const step of job.steps.filter((candidate) => candidate.uses.startsWith(CLAUDE_CODE_ACTION))) {
      if (!step.text.split('\n').some((line) => /^ +--model \S/.test(line))) {
        throw new Error(`${file} job ${job.name} step ${step.name}: Claude Code must be given an explicit --model.`);
      }
    }
  }
}

/** A job that runs Claude Code never holds the GitHub App credential; a separate job publishes. */
export function verifyAgentJobsHoldNoAppToken(file, workflow) {
  for (const job of workflow.jobs) {
    const runsAgent = job.steps.some((step) => step.uses.startsWith(CLAUDE_CODE_ACTION));
    const mintsToken = job.steps.some((step) => step.uses.startsWith(APP_TOKEN_ACTION) || step.text.includes('AGENT_AUTOMATION_APP_PRIVATE_KEY'));
    if (runsAgent && mintsToken) {
      throw new Error(`${file} job ${job.name}: runs Claude Code and also mints the GitHub App token; publish from a separate job.`);
    }
  }
}

/** Repair concurrency belongs to the repair job: workflow-level concurrency would let any comment cancel it (WF-10). */
export function verifyRepairConcurrency(file, workflow) {
  if (/^concurrency:/m.test(workflow.topLevel)) {
    throw new Error(`${file}: workflow-level concurrency is applied before job conditions, so any pull request comment would cancel a running repair. Put it on the repair job.`);
  }
  const repair = workflow.jobs.find((job) => job.name === 'repair');
  if (!repair) throw new Error(`${file}: no repair job`);
  if (!/^ {4}concurrency:\n {6}group: agent-repair-pr-\$\{\{ github\.event\.issue\.number \}\}\n {6}cancel-in-progress: true$/m.test(repair.header)) {
    throw new Error(`${file}: the repair job must take the agent-repair-pr-<number> concurrency group with cancel-in-progress.`);
  }
}

export function workflowFiles(read = (path) => readFileSync(join(repositoryRoot, path), 'utf8'), list = () => readdirSync(join(repositoryRoot, WORKFLOW_DIRECTORY))) {
  return list().filter((name) => /\.ya?ml$/.test(name)).sort().map((name) => {
    const path = `${WORKFLOW_DIRECTORY}/${name}`;
    return { path, workflow: parseWorkflow(read(path), path) };
  });
}

export function runWorkflowStructureChecks(options = {}) {
  const files = workflowFiles(options.read, options.list);
  for (const { path, workflow } of files) {
    verifyPinnedActions(path, workflow);
    verifyClaudeModels(path, workflow);
    verifyAgentJobsHoldNoAppToken(path, workflow);
    if (path.endsWith('/agent-repair.yml')) verifyRepairConcurrency(path, workflow);
  }
  return files.length;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  try {
    const count = runWorkflowStructureChecks();
    console.log(`Workflow structure checks passed: ${count} workflows.`);
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(1);
  }
}
