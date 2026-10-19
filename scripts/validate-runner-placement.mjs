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
// Jobs that receive a credential run only once a human confirms the persistent runners are reset or
// ephemeral per job (issue #645 review). A credential is any secret other than the job's own token,
// a reusable-workflow secret mapping, Azure OIDC, or a GITHUB_TOKEN with any write scope, declared
// on the job or inherited from the workflow: an earlier job's leftover process could use a write
// token to forge statuses or reviews, dispatch workflows or delete branches while the job runs.
// The condition must be on the job itself: credentials reach the runner when the job starts, so a
// step that fails afterwards would already be too late.
export const ISOLATION_CONDITION = "vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true'";
export const ISOLATION_GATE_STEP = 'Require verified runner isolation for credentialed jobs';
// The workflows whose credentialed jobs a person starts, so a refusal must fail visibly, not skip.
export const VISIBLE_ISOLATION_GATE_WORKFLOWS = [
  '.github/workflows/agent-implement.yml',
  '.github/workflows/agent-architecture.yml',
  '.github/workflows/agent-review.yml',
  '.github/workflows/agent-repair.yml',
  '.github/workflows/deploy-production.yml',
  '.github/workflows/validate.yml',
  '.github/workflows/agent-head-update.yml',
  '.github/workflows/agent-review-request.yml',
  '.github/workflows/agent-branch-cleanup.yml',
];
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

/**
 * The value of `key:` at `indent` spaces, with the lines indented under it, or undefined when the key
 * is absent. Read line by line so that no pattern can backtrack across the workflow text.
 */
function blockValue(text, key, indent) {
  const prefix = `${' '.repeat(indent)}${key}:`;
  const lines = text.split('\n');
  const start = lines.findIndex((line) => line.startsWith(prefix));
  if (start < 0) return undefined;
  const block = [lines[start].slice(prefix.length)];
  for (const line of lines.slice(start + 1)) {
    if (!line.startsWith(' '.repeat(indent + 2))) break;
    block.push(line);
  }
  return block.join('\n');
}

// GitHub expressions only; `secrets` in a comment or a log message is not a reference.
const expressions = (job) => [...job.matchAll(/\$\{\{([^}]*)\}\}/g)].map((match) => match[1]);
const JOB_TOKEN = /\bsecrets\s*(?:\.\s*GITHUB_TOKEN\b|\[\s*['"]GITHUB_TOKEN['"]\s*\])/g;
const SECRETS_CONTEXT = /\bsecrets\b/;

/** True when the permissions value grants write, or is absent everywhere and so takes the repository default. */
function grantsWrite(permissions) {
  if (permissions === undefined) return true;
  const value = permissions.replace(/#.*$/gm, '');
  return /\bwrite-all\b/.test(value) || /:\s*write\b/.test(value);
}

function isCredentialed(job, workflowPermissions) {
  if (expressions(job).some((expression) => SECRETS_CONTEXT.test(expression.replace(JOB_TOKEN, '')))) return true;
  if (/^ {4}secrets:/m.test(job)) return true;
  return grantsWrite(blockValue(job, 'permissions', 4) ?? workflowPermissions);
}

/** The job-level `if:`, joined into one line whether written inline or as a `|`/`>-` block. */
function jobCondition(job) {
  const value = blockValue(job, 'if', 4);
  if (value === undefined) return '';
  const [first, ...rest] = value.split('\n');
  const text = /^[|>][-+]?$/.test(first.trim()) ? rest.join(' ') : first.trim();
  return text.replace(/^\$\{\{([\s\S]*)\}\}$/, '$1').replace(/\s+/g, ' ').trim();
}

/** The expression's operands of `&&` outside parentheses, or undefined when a top-level `||` lets a branch bypass them. */
function topLevelConjuncts(expression) {
  const conjuncts = [];
  let depth = 0;
  let start = 0;
  for (let index = 0; index < expression.length; index += 1) {
    const char = expression[index];
    if (char === '(') depth += 1;
    else if (char === ')') depth -= 1;
    else if (depth === 0 && expression.startsWith('||', index)) return undefined;
    else if (depth === 0 && expression.startsWith('&&', index)) {
      conjuncts.push(expression.slice(start, index).trim());
      start = index + 2;
    }
  }
  conjuncts.push(expression.slice(start).trim());
  return conjuncts;
}

function isolationViolation(where, job, workflowPermissions) {
  if (!isCredentialed(job, workflowPermissions)) return undefined;
  if (topLevelConjuncts(jobCondition(job))?.includes(ISOLATION_CONDITION)) return undefined;
  return `${where}: receives a secret, an OIDC token or a write-capable GITHUB_TOKEN, so its job-level if: must require ${ISOLATION_CONDITION} with a top-level &&.`;
}

function jobViolations(where, job, workflowPermissions) {
  const runsOn = jobValues(job, 'runs-on');
  const uses = jobValues(job, 'uses');
  const isolation = isolationViolation(where, job, workflowPermissions);
  if (uses.length > 0) return [...reusableWorkflowViolations(where, uses, runsOn), isolation].filter(Boolean);
  if (runsOn.length !== 1) return [`${where}: must declare exactly one runs-on: ${SELF_HOSTED_RUNS_ON} (found ${runsOn.length}).`];
  return [runsOnViolation(where, runsOn[0]), nodeSetupViolation(where, job), isolation].filter(Boolean);
}

/** Returns a list of violations for one workflow; an empty list means it satisfies the contract. */
export function runnerPlacementViolations(workflowText, source) {
  const text = workflowText.replaceAll('\r\n', '\n');
  const violations = text.includes(RETIRED_RUNNER_SWITCH)
    ? [`${source}: still reads the retired ${RETIRED_RUNNER_SWITCH} runner switch, which allowed a GitHub-hosted fallback.`]
    : [];
  const workflowPermissions = blockValue(text.slice(0, text.search(/^jobs:/m)), 'permissions', 0);
  for (const [id, job] of Object.entries(workflowJobs(text, source))) {
    violations.push(...jobViolations(`${source} job '${id}'`, job, workflowPermissions));
  }
  if (VISIBLE_ISOLATION_GATE_WORKFLOWS.includes(source) && !text.includes(`      - name: ${ISOLATION_GATE_STEP}\n`)) {
    violations.push(`${source}: has no '${ISOLATION_GATE_STEP}' step, so a refused credentialed job would be skipped silently.`);
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
