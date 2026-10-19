// Runner placement contract (issue #645): every repository-owned GitHub Actions job runs on the
// self-hosted inventory-agent runners, with no GitHub-hosted runner and no fallback to one.
// Run with: node scripts/validate-runner-placement.mjs
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

export const SELF_HOSTED_RUNS_ON = '[self-hosted, linux, x64, inventory-agent]';
// The retired opt-in selector: while it existed, leaving it unset fell back to ubuntu-latest.
export const RETIRED_RUNNER_SWITCH = 'INVENTORY_AGENT_SELF_HOSTED_ALL';
const HOSTED_LABEL = /\b(?:ubuntu|windows|macos)-(?:latest|\d[\w.-]*)\b/;
const LOCAL_REUSABLE_WORKFLOW = /^\.\/\.github\/workflows\/[\w.-]+\.ya?ml$/;
// A self-hosted runner has no GitHub-hosted image tools, so a job that runs `node` installs it
// with the pinned actions/setup-node step before its first use instead of assuming one on PATH.
export const SETUP_NODE_STEP = 'uses: actions/setup-node@49933ea5288caeca8642d1e84afbd3f7d6820020 # v4.4.0';
const NODE_INVOCATION = /(^|[\s;(|&"'`$])node\s+(--test\b|-e\b|"|\$|[\w./-]+\.m?js\b)/m;

export function listWorkflowFiles(root = repositoryRoot) {
  return readdirSync(resolve(root, '.github/workflows'))
    .filter((name) => /\.ya?ml$/.test(name))
    .sort()
    .map((name) => `.github/workflows/${name}`);
}

/** Splits a workflow's top-level `jobs:` map into `{ id: text }`, at the two-space indent the repository uses. */
export function workflowJobs(workflowText, source) {
  const text = workflowText.replaceAll('\r\n', '\n');
  const start = text.search(/^jobs:[ \t]*$/m);
  if (start < 0) throw new Error(`${source}: has no top-level jobs: map.`);
  const lines = text.slice(start).split('\n').slice(1);
  const jobs = {};
  let current;
  for (const line of lines) {
    if (/^\S/.test(line)) break; // the next top-level key ends the jobs map
    const header = /^ {2}([A-Za-z_][\w-]*):\s*$/.exec(line);
    if (header) {
      current = header[1];
      if (current in jobs) throw new Error(`${source}: job '${current}' is declared twice.`);
      jobs[current] = '';
      continue;
    }
    if (line.trim() !== '' && !/^\s*#/.test(line) && !/^ {4}/.test(line)) {
      throw new Error(`${source}: cannot read the jobs map at line: ${line.trim()}`);
    }
    if (current) jobs[current] += `${line}\n`;
  }
  if (Object.keys(jobs).length === 0) throw new Error(`${source}: jobs: map is empty.`);
  return jobs;
}

const jobValues = (job, key) => [...job.matchAll(new RegExp(`^ {4}${key}:(.*)$`, 'gm'))].map((match) => match[1].trim());

// A job that calls a reusable workflow has no runner of its own; the called workflow's jobs are
// checked as workflow files. Only a local reusable workflow is repository-owned.
function reusableWorkflowViolations(where, uses, runsOn) {
  const violations = [];
  if (runsOn.length > 0) violations.push(`${where}: declares both uses: and runs-on:.`);
  if (uses.length > 1 || !LOCAL_REUSABLE_WORKFLOW.test(uses[0])) {
    violations.push(`${where}: calls ${uses.join(', ')}; only a reusable workflow in this repository's .github/workflows is allowed.`);
  }
  return violations;
}

function runsOnViolation(where, value) {
  if (HOSTED_LABEL.test(value)) return `${where}: selects a GitHub-hosted runner (${value}).`;
  if (value.includes('${{')) return `${where}: selects its runner with an expression (${value}); a computed runner can fall back to a GitHub-hosted one.`;
  if (value !== SELF_HOSTED_RUNS_ON) return `${where}: runs-on must be exactly ${SELF_HOSTED_RUNS_ON} (found ${value || 'a multi-line value'}).`;
  return undefined;
}

function nodeSetupViolation(where, job) {
  const nodeAt = job.search(NODE_INVOCATION);
  if (nodeAt < 0) return undefined;
  const setupAt = job.indexOf(SETUP_NODE_STEP);
  return setupAt < 0 || setupAt > nodeAt ? `${where}: runs node without first installing it with ${SETUP_NODE_STEP}.` : undefined;
}

function jobViolations(where, job) {
  const runsOn = jobValues(job, 'runs-on');
  const uses = jobValues(job, 'uses');
  if (uses.length > 0) return reusableWorkflowViolations(where, uses, runsOn);
  if (runsOn.length !== 1) return [`${where}: must declare exactly one runs-on: ${SELF_HOSTED_RUNS_ON} (found ${runsOn.length}).`];
  return [runsOnViolation(where, runsOn[0]), nodeSetupViolation(where, job)].filter(Boolean);
}

/** Returns a list of violations for one workflow; an empty list means it satisfies the contract. */
export function runnerPlacementViolations(workflowText, source) {
  const text = workflowText.replaceAll('\r\n', '\n');
  const violations = text.includes(RETIRED_RUNNER_SWITCH)
    ? [`${source}: still reads the retired ${RETIRED_RUNNER_SWITCH} runner switch, which allowed a GitHub-hosted fallback.`]
    : [];
  for (const [id, job] of Object.entries(workflowJobs(text, source))) {
    violations.push(...jobViolations(`${source} job '${id}'`, job));
  }
  return violations;
}

export function verifyRunnerPlacement({ read, paths }) {
  if (paths.length === 0) throw new Error('Runner placement: no workflow files found.');
  const violations = paths.flatMap((path) => runnerPlacementViolations(read(path), path));
  if (violations.length > 0) throw new Error(`Runner placement contract failed:\n${violations.join('\n')}`);
}

const invokedDirectly =
  process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url);

if (invokedDirectly) {
  const paths = listWorkflowFiles();
  verifyRunnerPlacement({ read: (path) => readFileSync(resolve(repositoryRoot, path), 'utf8'), paths });
  console.log(`Runner placement contract passed: ${paths.length} workflows, every job on ${SELF_HOSTED_RUNS_ON}.`);
}
