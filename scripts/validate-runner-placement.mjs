// Runner placement contract (issues #645 and #807): every repository-owned GitHub Actions job selects
// its runner with the one approved expression over the owner's INVENTORY_AGENT_SELF_HOSTED_ALL switch.
// Exactly 'true' selects the self-hosted inventory-agent runners; false or unset selects ubuntu-latest.
// The switch is an explicit mode, never an automatic fallback when the self-hosted runners are offline.
// Run with: node scripts/validate-runner-placement.mjs
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

export const RUNNER_MODE_VARIABLE = 'INVENTORY_AGENT_SELF_HOSTED_ALL';
export const SELF_HOSTED_LABELS = ['self-hosted', 'linux', 'x64', 'inventory-agent'];
export const GITHUB_HOSTED_LABEL = 'ubuntu-latest';
// The only runs-on value a job may use. GitHub compares strings without regard to case, so a value
// such as 'TRUE' also selects the self-hosted runners; the isolation gate below uses the same
// comparison, so such a value still needs verified isolation, and the runner mode step then fails it.
export const RUNS_ON_SELECTOR =
  "${{ vars.INVENTORY_AGENT_SELF_HOSTED_ALL == 'true' && fromJSON('[\"self-hosted\",\"linux\",\"x64\",\"inventory-agent\"]') || 'ubuntu-latest' }}";
// Every job's first step: it logs the selected mode and the runner, and fails when the variable is
// anything other than exactly true, false or unset (expressions cannot compare case-sensitively), or
// when the job landed on a different kind of runner than the variable now selects.
export const RUNNER_MODE_STEP = `      - name: Report and validate runner mode
        env:
          RUNNER_MODE: \${{ vars.INVENTORY_AGENT_SELF_HOSTED_ALL }}
          RUNNER_KIND: \${{ runner.environment }}
        run: |
          case "$RUNNER_MODE" in
            true) selected=self-hosted ;;
            ''|false) selected=github-hosted ;;
            *) echo "::error::The repository variable INVENTORY_AGENT_SELF_HOSTED_ALL must be exactly true, false or unset. See docs/automation.md § Runner placement."; exit 1 ;;
          esac
          echo "Runner mode: INVENTORY_AGENT_SELF_HOSTED_ALL=\${RUNNER_MODE:-<unset>} selects $selected runners."
          echo "This job runs on $RUNNER_NAME ($RUNNER_KIND)."
          if [ "$RUNNER_KIND" != "$selected" ]; then
            echo "::error::This job runs on a $RUNNER_KIND runner, but INVENTORY_AGENT_SELF_HOSTED_ALL now selects $selected runners; the variable changed after the job was queued. Re-run the job. See docs/automation.md § Runner placement."
            exit 1
          fi
`;
// The step-level reading of the switch, used by the runner mode step and the visible refusal steps.
export const RUNNER_MODE_ENV = '          RUNNER_MODE: ${{ vars.INVENTORY_AGENT_SELF_HOSTED_ALL }}\n';
const LOCAL_REUSABLE_WORKFLOW = /^\.\/\.github\/workflows\/[\w.-]+\.ya?ml$/;
// A self-hosted runner has no GitHub-hosted image tools, so a job that runs `node` installs it with the
// pinned actions/setup-node step before its first use instead of assuming one on PATH, in either mode.
export const SETUP_NODE_STEP = 'uses: actions/setup-node@49933ea5288caeca8642d1e84afbd3f7d6820020 # v4.4.0';
// In self-hosted mode, jobs that receive a credential run only once a human confirms the persistent
// runners are reset or ephemeral per job (issue #645 review). A GitHub-hosted runner is a fresh VM
// per job, so in GitHub-hosted mode that verification of the owner's VM is not required (issue #807).
// A credential is any secret other than the job's own token, a reusable-workflow secret mapping,
// Azure OIDC, or a GITHUB_TOKEN with any write scope, declared on the job or inherited from the
// workflow: an earlier job's leftover process could use a write token to forge statuses or reviews,
// dispatch workflows or delete branches while the job runs. The condition must be on the job
// itself: credentials reach the runner when the job starts, so a step that fails afterwards would
// already be too late. It compares the switch exactly as RUNS_ON_SELECTOR does, so no value can
// select the self-hosted runners without also requiring verified isolation.
export const ISOLATION_CONDITION =
  "(vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true')";
// The job-level condition of a visible refusal job, the exact complement of ISOLATION_CONDITION.
export const ISOLATION_REFUSAL_CONDITION =
  "vars.INVENTORY_AGENT_SELF_HOSTED_ALL == 'true' && vars.INVENTORY_RUNNER_ISOLATION_VERIFIED != 'true'";
// The same refusal inside a step of a job that holds no credential.
export const ISOLATION_REFUSAL_STEP_TEST = 'if [ "$RUNNER_MODE" = "true" ] && [ "$RUNNER_ISOLATION_VERIFIED" != "true" ]; then';
// Deploy API and Deploy Frontend: a verified host baseline is required only of the self-hosted runners.
export const BASELINE_CONDITION =
  "(vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_BASELINE_VERIFIED == 'true')";
export const ISOLATION_GATE_STEP = 'Require verified runner isolation for credentialed jobs';
// The workflows whose credentialed jobs a person starts, so a refusal must fail visibly, not skip.
export const VISIBLE_ISOLATION_GATE_WORKFLOWS = [
  '.github/workflows/agent-implement.yml',
  '.github/workflows/agent-architecture.yml',
  '.github/workflows/agent-review.yml',
  '.github/workflows/agent-repair.yml',
  '.github/workflows/deploy-production.yml',
  '.github/workflows/deploy-production-recovery.yml',
  '.github/workflows/deploy-production-release.yml',
  '.github/workflows/deploy-testing.yml',
  '.github/workflows/validate.yml',
  '.github/workflows/agent-head-update.yml',
  '.github/workflows/agent-review-request.yml',
  '.github/workflows/agent-branch-cleanup.yml',
  '.github/workflows/price-collector.yml',
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
  if (value === RUNS_ON_SELECTOR) return undefined;
  return `${where}: runs-on must be exactly the approved runner mode selector ${RUNS_ON_SELECTOR} (found ${value || 'a multi-line value'}); a fixed runner, another expression or a fallback would ignore or bypass the ${RUNNER_MODE_VARIABLE} mode.`;
}

function modeStepViolation(where, job) {
  if (job.includes(`\n    steps:\n${RUNNER_MODE_STEP}`)) return undefined;
  return `${where}: its first step must be exactly the approved 'Report and validate runner mode' step, which logs the selected mode and runner and rejects any value other than true, false or unset.`;
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
  return permissions.split('\n').some((line) => {
    const value = line.split('#')[0].trim();
    return value === 'write-all' || value.split(':').at(-1).trim() === 'write';
  });
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
  if (runsOn.length !== 1) return [`${where}: must declare exactly one runs-on: ${RUNS_ON_SELECTOR} (found ${runsOn.length}).`];
  return [runsOnViolation(where, runsOn[0]), modeStepViolation(where, `\n${job}`), nodeSetupViolation(where, job), isolation].filter(Boolean);
}

// Any reading of the switch in an expression, whatever its case or accessor form.
const MODE_READ = new RegExp(String.raw`\bvars\s*(?:\.\s*|\[\s*['"])${RUNNER_MODE_VARIABLE}\b`, 'i');
const APPROVED_MODE_READS = [RUNS_ON_SELECTOR, ISOLATION_CONDITION, ISOLATION_REFUSAL_CONDITION, RUNNER_MODE_ENV];

/**
 * The switch may be read only through the approved texts: the selector, the isolation gate and its
 * refusal, the deployment baseline gate and the step-level RUNNER_MODE variable. Any other reading
 * could choose a runner, or relax a gate, differently from the selector.
 */
function modeReadViolations(text, source) {
  let remaining = text;
  for (const approved of [...APPROVED_MODE_READS, BASELINE_CONDITION]) remaining = remaining.replaceAll(approved, '');
  return remaining
    .split('\n')
    .filter((line) => !/^\s*#/.test(line) && MODE_READ.test(line))
    .map((line) => `${source}: reads ${RUNNER_MODE_VARIABLE} outside the approved selector, gates and RUNNER_MODE step variable (${line.trim()}).`);
}

/** A refusal that ignores the mode would block GitHub-hosted runs that need no VM isolation. */
function refusalViolations(text, source) {
  const unconditional = text.replaceAll(ISOLATION_REFUSAL_CONDITION, '').replaceAll(ISOLATION_REFUSAL_STEP_TEST, '');
  const violations = [];
  if (/vars\.INVENTORY_RUNNER_ISOLATION_VERIFIED\s*!=/.test(unconditional) || unconditional.includes('if [ "$RUNNER_ISOLATION_VERIFIED"')) {
    violations.push(`${source}: refuses on INVENTORY_RUNNER_ISOLATION_VERIFIED without the self-hosted mode condition; use ${ISOLATION_REFUSAL_CONDITION} or ${ISOLATION_REFUSAL_STEP_TEST}.`);
  }
  if (text.includes(ISOLATION_REFUSAL_STEP_TEST) && !text.includes(RUNNER_MODE_ENV)) {
    violations.push(`${source}: its isolation refusal step reads $RUNNER_MODE but no step sets it from the switch.`);
  }
  return violations;
}

/** Returns a list of violations for one workflow; an empty list means it satisfies the contract. */
export function runnerPlacementViolations(workflowText, source) {
  const text = workflowText.replaceAll('\r\n', '\n');
  const violations = [...modeReadViolations(text, source), ...refusalViolations(text, source)];
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
  console.log(`Runner placement contract passed: ${paths.length} workflows, every job selects its runner with the ${RUNNER_MODE_VARIABLE} mode.`);
}
