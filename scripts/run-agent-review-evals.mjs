// InventoryApp review-prompt evals.
//
// Replays the agent-review.yml review prompt against real pull request heads where the right
// answer is already known: three heads that contained a bug a later fix commit repaired, and one
// head that a reviewer should pass without inventing a blocker. Each case is graded
// deterministically against a hidden rubric: did the review raise the known problem as a blocker,
// did it return the expected verdict, and did it raise a blocker the rubric marks as a false alarm.
//
// The prompt under test is read from .github/workflows/agent-review.yml itself (variant `current`),
// so the eval always tests what the pipeline runs. Variant `candidate` is the same prompt with the
// addendum in evals/agent/review/candidate-prompt.md inserted before the delivery instructions.
// Only the review agent's own model, effort, turn limit and response schema are reused from the
// workflow; the tools are narrowed to local, read-only ones (see evals/agent/README.md).
//
// Each case runs in a fresh temporary git repository that holds exactly two snapshots, the PR's
// base and head, so the reviewer can read the code around the change and run `git diff`, but cannot
// see later history, the fix commit, or this repository's eval case files.
//
// This is an explicitly invoked, local, on-demand command. It is not part of scripts/validate.sh
// (only its unit tests are), and no workflow runs it. The only live provider reuses the Claude
// Code CLI already installed and signed in on the machine; this script reads no credential.
//
// Usage:
//   node scripts/run-agent-review-evals.mjs --print-prompts [--variant current|candidate]
//   node scripts/run-agent-review-evals.mjs --provider claude-cli --variant current|candidate [--cases ID,ID] [--json] [--output <file>]
//   node scripts/run-agent-review-evals.mjs --provider fixture --responses <file> [--variant ...]
import { execFileSync, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { runProcess } from './run-agent-model-evals.mjs';

export const repositoryRoot = resolve(fileURLToPath(new URL('..', import.meta.url)));
export const DEFAULT_CASES_DIR = resolve(repositoryRoot, 'evals/agent/review/cases');
export const REVIEW_WORKFLOW = resolve(repositoryRoot, '.github/workflows/agent-review.yml');
export const CANDIDATE_ADDENDUM = resolve(repositoryRoot, 'evals/agent/review/candidate-prompt.md');
export const EVAL_VERSION = 'review-eval-v1';
export const VARIANTS = ['current', 'candidate'];
export const VERDICTS = ['CHANGES REQUESTED', 'READY FOR HUMAN REVIEW'];
const DEFAULT_TIMEOUT_MS = 40 * 60 * 1000;
const SHA = /^[0-9a-f]{40}$/;
const CASE_ID = /^REVIEW-\d{3}$/;
const DELIVERY_ANCHOR = 'Deliver the result only as the structured output';

// The tools the eval reviewer gets: reading files in its own temporary repository, and
// read-only git commands. Nothing reaches GitHub, the network, MCP servers or this repository.
export const EVAL_TOOLS = ['Read', 'Grep', 'Glob', 'Bash'];
export const EVAL_ALLOWED_TOOLS = [
  'Bash(git diff *)',
  'Bash(git log *)',
  'Bash(git show *)',
  'Bash(git status *)',
];

export class ProviderUnavailableError extends Error {}

// --- The prompt under test --------------------------------------------------------------------

/**
 * Reads the review agent step of agent-review.yml: its `prompt: |` block, dedented, and the
 * model, effort, turn limit and JSON schema from its `claude_args: |` block.
 */
export function extractReviewStep(workflowText) {
  const lines = workflowText.split('\n');
  const stepIndex = lines.findIndex((line) => line.trim() === 'id: review_agent');
  if (stepIndex < 0) {
    throw new Error('agent-review.yml has no step with id: review_agent.');
  }
  const block = (key) => {
    const start = lines.findIndex((line, i) => i > stepIndex && line.trim() === `${key}: |`);
    if (start < 0) {
      throw new Error(`The review_agent step has no "${key}: |" block.`);
    }
    const keyIndent = lines[start].length - lines[start].trimStart().length;
    const body = [];
    for (let i = start + 1; i < lines.length; i += 1) {
      const line = lines[i];
      if (line.trim() !== '' && line.length - line.trimStart().length <= keyIndent) {
        break;
      }
      body.push(line);
    }
    while (body.length > 0 && body.at(-1).trim() === '') {
      body.pop();
    }
    const indent = Math.min(...body.filter((line) => line.trim() !== '').map((line) => line.length - line.trimStart().length));
    return body.map((line) => line.slice(indent)).join('\n');
  };
  const prompt = block('prompt');
  const args = block('claude_args');
  const pick = (pattern, name) => {
    const match = pattern.exec(args);
    if (!match) {
      throw new Error(`The review_agent claude_args have no ${name}.`);
    }
    return match[1];
  };
  return {
    prompt,
    model: pick(/--model (\S+)/, '--model'),
    effort: pick(/--effort (\S+)/, '--effort'),
    maxTurns: Number(pick(/--max-turns (\d+)/, '--max-turns')),
    jsonSchema: JSON.parse(pick(/--json-schema '([^']+)'/, '--json-schema')),
  };
}

/** Replaces each GitHub expression in the prompt; an expression nobody mapped is an error. */
export function resolveExpressions(prompt, values) {
  const unresolved = new Set();
  const text = prompt.replace(/\$\{\{([^}]*)\}\}/g, (whole, inner) => {
    const expression = inner.trim();
    if (!Object.hasOwn(values, expression)) {
      unresolved.add(expression);
      return whole;
    }
    return values[expression];
  });
  if (unresolved.size > 0) {
    throw new Error(`Unmapped workflow expressions in the review prompt: ${[...unresolved].join(', ')}`);
  }
  return text;
}

export function applyVariant(prompt, variant, addendum) {
  if (variant === 'current') {
    return prompt;
  }
  if (variant !== 'candidate') {
    throw new Error(`Unknown variant "${variant}".`);
  }
  const at = prompt.indexOf(DELIVERY_ANCHOR);
  if (at < 0) {
    throw new Error(`The review prompt no longer contains "${DELIVERY_ANCHOR}"; update the candidate insertion point.`);
  }
  return `${prompt.slice(0, at)}${addendum.trim()}\n\n${prompt.slice(at)}`;
}

function bulletList(items) {
  return items.map((item) => `- ${item}`).join('\n');
}

/**
 * The full prompt for one case: the workflow's review prompt (variant applied), then the offline
 * pull request context. The rubric (`expected`, `source`) is never included.
 */
export function buildReviewPrompt(caseObj, { reviewStep, variant, addendum }) {
  const values = {
    'needs.context.outputs.review_relation':
      'This pull request was implemented by another Claude invocation. This is a same-provider review, not an independent one: you are a fresh invocation with no implementation conversation state; verify every claim.',
    'github.repository': 'cristhyanc/InventoryApp',
    'needs.context.outputs.pr_number': String(caseObj.pullRequest),
    'needs.context.outputs.head_sha': caseObj.headSha,
    'needs.context.outputs.base_ref': 'develop',
  };
  const prompt = applyVariant(resolveExpressions(reviewStep.prompt, values), variant, addendum);
  const c = caseObj.context;
  return `${prompt}

EVALUATION RUN (offline replay of a past pull request):
This review runs outside GitHub Actions. \`gh\`, the github_ci tools and the Nayax documentation tools are not available, and you cannot open pull requests, issues or CI logs. The working tree is the pull request head; \`origin/develop\` is its merge base, so \`git diff origin/develop...HEAD\` is the full pull request diff. Read any file in the working tree. The pull request, linked issue and CI facts below are the authoritative copies of what the GitHub commands would have shown you; treat them exactly as you would treat that evidence.

PULL REQUEST #${caseObj.pullRequest}: ${c.prTitle}

Description summary:
${c.prSummary}

Linked issue: ${c.linkedIssue}

Acceptance criteria:
${bulletList(c.acceptanceCriteria)}

Explicitly out of scope:
${bulletList(c.outOfScope)}

Documentation impact:
${c.documentationImpact}

Validation evidence (CI):
${c.validation}
`;
}

// --- Cases -------------------------------------------------------------------------------------

function isNonEmptyString(value) {
  return typeof value === 'string' && value.trim() !== '';
}

function validateMatcher(matcher, where, errors) {
  if (!isNonEmptyString(matcher?.id)) errors.push(`${where}: id is required.`);
  if (!isNonEmptyString(matcher?.summary)) errors.push(`${where}: summary is required.`);
  if (!Array.isArray(matcher?.paths) || matcher.paths.length === 0) errors.push(`${where}: paths must be a non-empty array.`);
  if (!Array.isArray(matcher?.allOf) || matcher.allOf.length === 0) errors.push(`${where}: allOf must be a non-empty array.`);
  for (const pattern of [...(matcher?.paths ?? []), ...(matcher?.allOf ?? [])]) {
    try {
      new RegExp(pattern, 'i');
    } catch {
      errors.push(`${where}: invalid pattern ${JSON.stringify(pattern)}.`);
    }
  }
}

function validateContext(c, where, errors) {
  for (const key of ['prTitle', 'prSummary', 'linkedIssue', 'documentationImpact', 'validation']) {
    if (!isNonEmptyString(c?.[key])) errors.push(`${where}: context.${key} is required.`);
  }
  for (const key of ['acceptanceCriteria', 'outOfScope']) {
    if (!Array.isArray(c?.[key]) || c[key].length === 0 || !c[key].every(isNonEmptyString)) {
      errors.push(`${where}: context.${key} must be a non-empty list of strings.`);
    }
  }
}

function validateExpected(e, where, errors) {
  if (!VERDICTS.includes(e?.verdict)) errors.push(`${where}: expected.verdict must be one of ${VERDICTS.join(', ')}.`);
  const findings = Array.isArray(e?.findings) ? e.findings : null;
  const forbidden = Array.isArray(e?.forbiddenBlockers) ? e.forbiddenBlockers : null;
  if (!findings) errors.push(`${where}: expected.findings must be an array.`);
  if (!forbidden) errors.push(`${where}: expected.forbiddenBlockers must be an array.`);
  (findings ?? []).forEach((m, i) => validateMatcher(m, `${where} expected.findings[${i}]`, errors));
  (forbidden ?? []).forEach((m, i) => validateMatcher(m, `${where} expected.forbiddenBlockers[${i}]`, errors));
  const count = (findings ?? []).length;
  if (e?.verdict === 'CHANGES REQUESTED' && count === 0) {
    errors.push(`${where}: a CHANGES REQUESTED case needs at least one expected finding.`);
  }
  if (e?.verdict === 'READY FOR HUMAN REVIEW' && count > 0) {
    errors.push(`${where}: a READY FOR HUMAN REVIEW case cannot expect a blocking finding.`);
  }
}

/** Returns a list of problems with one case; an empty list means the case is valid. */
export function validateReviewCase(caseObj) {
  const errors = [];
  const id = caseObj?.id;
  const where = isNonEmptyString(id) ? id : '<case>';
  if (!CASE_ID.test(id ?? '')) errors.push(`${where}: id must match REVIEW-NNN.`);
  if (!Number.isInteger(caseObj?.pullRequest)) errors.push(`${where}: pullRequest must be an integer.`);
  for (const key of ['baseSha', 'headSha']) {
    if (!SHA.test(caseObj?.[key] ?? '')) errors.push(`${where}: ${key} must be a full 40-character SHA.`);
  }
  validateContext(caseObj?.context, where, errors);
  validateExpected(caseObj?.expected, where, errors);
  if (!isNonEmptyString(caseObj?.source)) errors.push(`${where}: source (how the right answer is known) is required.`);
  return errors;
}

export function loadReviewCases(dir = DEFAULT_CASES_DIR) {
  const cases = readdirSync(dir)
    .filter((name) => name.endsWith('.json'))
    .sort()
    .map((name) => JSON.parse(readFileSync(join(dir, name), 'utf8')));
  const errors = cases.flatMap(validateReviewCase);
  const ids = cases.map((c) => c.id);
  const duplicate = ids.find((id, i) => ids.indexOf(id) !== i);
  if (duplicate) errors.push(`Duplicate case id ${duplicate}.`);
  if (!cases.some((c) => c.expected?.verdict === 'READY FOR HUMAN REVIEW')) {
    errors.push('The set needs at least one READY FOR HUMAN REVIEW control case, so an "always block" reviewer cannot pass.');
  }
  if (!cases.some((c) => c.expected?.verdict === 'CHANGES REQUESTED')) {
    errors.push('The set needs at least one CHANGES REQUESTED case.');
  }
  if (errors.length > 0) {
    throw new Error(`Invalid review eval cases:\n${errors.join('\n')}`);
  }
  return cases;
}

// --- Grading -----------------------------------------------------------------------------------

/** Validates the reviewer's structured output against the workflow's own schema essentials. */
export function parseReviewResponse(raw) {
  let value = raw;
  if (typeof value === 'string') {
    try {
      value = JSON.parse(value);
    } catch {
      return { ok: false, error: 'The response is not JSON.' };
    }
  }
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    return { ok: false, error: 'The response is not a JSON object.' };
  }
  if (!VERDICTS.includes(value.verdict)) return { ok: false, error: 'verdict is missing or invalid.' };
  for (const key of ['blockers', 'suggestions', 'criteria', 'inline_comments']) {
    if (!Array.isArray(value[key])) return { ok: false, error: `${key} is missing or not an array.` };
  }
  return { ok: true, value };
}

// One "finding" the reviewer raised, as text a matcher can search. Blockers and inline comments
// both ask for a change; suggestions are recorded separately and never count as a blocker.
function findingTexts(review) {
  const blockers = review.blockers.map((text) => ({ kind: 'blocker', text: String(text) }));
  const inline = review.inline_comments.map((comment) => ({
    kind: 'inline',
    text: `${comment?.path ?? ''}:${comment?.line ?? ''} ${comment?.body ?? ''}`,
  }));
  const suggestions = review.suggestions.map((text) => ({ kind: 'suggestion', text: String(text) }));
  return { raised: [...blockers, ...inline], blockers, suggestions };
}

export function matches(matcher, text) {
  return (
    matcher.paths.some((pattern) => new RegExp(pattern, 'i').test(text)) &&
    matcher.allOf.every((pattern) => new RegExp(pattern, 'i').test(text))
  );
}

// A blocker is "actionable" when it names a file:line and a test, which is what the candidate
// prompt asks for. Reported for both variants; it does not decide pass or fail.
export function isActionable(text) {
  return /\.\w+:\d+/.test(text) && /\btests?\b/i.test(text);
}

export function gradeReviewCase(caseObj, outcome) {
  const base = { id: caseObj.id, pullRequest: caseObj.pullRequest, expectedVerdict: caseObj.expected.verdict };
  if (outcome.error) {
    return { ...base, status: 'FAIL', failures: [`Provider error: ${outcome.error}`], providerMetadata: outcome.providerMetadata ?? null };
  }
  const parsed = parseReviewResponse(outcome.raw);
  if (!parsed.ok) {
    return { ...base, status: 'FAIL', failures: [`Malformed response: ${parsed.error}`], providerMetadata: outcome.providerMetadata ?? null };
  }
  const review = parsed.value;
  const { raised, blockers, suggestions } = findingTexts(review);
  const failures = [];

  const findings = caseObj.expected.findings.map((matcher) => {
    const caught = raised.some((item) => matches(matcher, item.text));
    const onlySuggested = !caught && suggestions.some((item) => matches(matcher, item.text));
    if (!caught) {
      failures.push(`Missed blocker ${matcher.id}: ${matcher.summary}${onlySuggested ? ' (raised only as a non-blocking suggestion)' : ''}`);
    }
    return { id: matcher.id, caught, onlySuggested };
  });

  const falseAlarms = caseObj.expected.forbiddenBlockers.flatMap((matcher) =>
    raised.filter((item) => matches(matcher, item.text)).map((item) => ({ id: matcher.id, text: item.text })),
  );
  for (const alarm of falseAlarms) {
    failures.push(`False-alarm blocker ${alarm.id}: ${alarm.text.slice(0, 300)}`);
  }

  // On a clean control any blocker or inline change request is unwanted, whether or not a
  // forbidden matcher names it, so each one fails the case (named false alarms are reported above).
  if (caseObj.expected.verdict === 'READY FOR HUMAN REVIEW') {
    const named = new Set(falseAlarms.map((alarm) => alarm.text));
    for (const item of raised.filter((entry) => !named.has(entry.text))) {
      failures.push(`Unexpected ${item.kind === 'inline' ? 'inline comment' : 'blocker'} on the clean control: ${item.text.slice(0, 300)}`);
    }
  }

  if (review.verdict !== caseObj.expected.verdict) {
    failures.push(`Verdict ${review.verdict}, expected ${caseObj.expected.verdict}.`);
  }

  const expectedMatchers = caseObj.expected.findings;
  const otherBlockers = blockers
    .filter((item) => !expectedMatchers.some((matcher) => matches(matcher, item.text)))
    .map((item) => item.text);

  return {
    ...base,
    status: failures.length === 0 ? 'PASS' : 'FAIL',
    verdict: review.verdict,
    failures,
    findings,
    falseAlarms,
    // Blockers the rubric neither expects nor forbids. On a READY control case every one of them is
    // an unwanted blocker; on the other cases a human decides whether each is real.
    otherBlockers,
    blockerCount: blockers.length,
    raisedCount: raised.length,
    actionableBlockers: blockers.filter((item) => isActionable(item.text)).length,
    review,
    providerMetadata: outcome.providerMetadata ?? null,
  };
}

export function summarizeReviewResults(results, cases) {
  const byId = new Map(cases.map((c) => [c.id, c]));
  let expectedFindings = 0;
  let caughtFindings = 0;
  let falseAlarms = 0;
  let controlBlockers = 0;
  let verdictMatches = 0;
  let blockers = 0;
  let actionable = 0;
  let costUsd = 0;
  for (const result of results) {
    const caseObj = byId.get(result.id);
    expectedFindings += caseObj.expected.findings.length;
    caughtFindings += (result.findings ?? []).filter((f) => f.caught).length;
    falseAlarms += (result.falseAlarms ?? []).length;
    if (caseObj.expected.verdict === 'READY FOR HUMAN REVIEW') {
      controlBlockers += result.raisedCount ?? 0;
    }
    if (result.verdict === caseObj.expected.verdict) verdictMatches += 1;
    blockers += result.blockerCount ?? 0;
    actionable += result.actionableBlockers ?? 0;
    costUsd += Number(result.providerMetadata?.costUsd ?? 0);
  }
  return {
    cases: results.length,
    passed: results.filter((r) => r.status === 'PASS').length,
    caughtFindings,
    expectedFindings,
    missedFindings: expectedFindings - caughtFindings,
    falseAlarms,
    controlBlockers,
    verdictMatches,
    blockers,
    actionableBlockers: actionable,
    costUsd: Math.round(costUsd * 100) / 100,
  };
}

// --- Isolated repository ------------------------------------------------------------------------

// Absolute paths, never a lookup on PATH; override them for a machine that keeps git or tar elsewhere.
const GIT_PATH = process.env.REVIEW_EVAL_GIT_PATH || '/usr/bin/git';
const TAR_PATH = process.env.REVIEW_EVAL_TAR_PATH || '/usr/bin/tar';

function git(args, options) {
  return execFileSync(GIT_PATH, args, { stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 256 * 1024 * 1024, ...options });
}

const SNAPSHOT_ENV = {
  GIT_AUTHOR_NAME: 'review-eval',
  GIT_AUTHOR_EMAIL: 'review-eval@example.invalid',
  GIT_COMMITTER_NAME: 'review-eval',
  GIT_COMMITTER_EMAIL: 'review-eval@example.invalid',
  GIT_AUTHOR_DATE: '2026-01-01T00:00:00Z',
  GIT_COMMITTER_DATE: '2026-01-01T00:00:00Z',
};

function extractSnapshot(sha, dir, root) {
  const archive = git(['archive', '--format=tar', sha], { cwd: root });
  execFileSync(TAR_PATH, ['-x', '-C', dir], { input: archive, maxBuffer: 256 * 1024 * 1024 });
}

/**
 * Builds a fresh repository with two commits, the PR base (also `origin/develop`) and the PR
 * head, copied by content from this repository. It shares no objects with this repository, so
 * no later commit, fix, or eval case file is reachable from it.
 */
export function createSnapshotRepository(caseObj, { root = repositoryRoot } = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'inventoryapp-review-eval-'));
  const env = { ...process.env, ...SNAPSHOT_ENV };
  try {
    git(['init', '-q', '-b', `pr-${caseObj.pullRequest}`], { cwd: dir, env });
    extractSnapshot(caseObj.baseSha, dir, root);
    git(['add', '-A'], { cwd: dir, env });
    git(['commit', '-q', '-m', `Base: develop at ${caseObj.baseSha}`], { cwd: dir, env });
    git(['update-ref', 'refs/remotes/origin/develop', 'HEAD'], { cwd: dir, env });
    git(['rm', '-rq', '.'], { cwd: dir, env });
    extractSnapshot(caseObj.headSha, dir, root);
    git(['add', '-A'], { cwd: dir, env });
    git(['commit', '-q', '-m', `Pull request #${caseObj.pullRequest} head ${caseObj.headSha}`], { cwd: dir, env });
    return dir;
  } catch (error) {
    rmSync(dir, { recursive: true, force: true });
    throw error;
  }
}

// --- Providers ---------------------------------------------------------------------------------

export function claudeReviewArgs(reviewStep) {
  return [
    '-p',
    '--output-format',
    'json',
    '--model',
    reviewStep.model,
    '--effort',
    reviewStep.effort,
    '--max-turns',
    String(reviewStep.maxTurns),
    '--json-schema',
    JSON.stringify(reviewStep.jsonSchema),
    '--tools',
    EVAL_TOOLS.join(','),
    '--allowedTools',
    EVAL_ALLOWED_TOOLS.join(','),
    '--strict-mcp-config',
    '--setting-sources',
    '',
    '--disable-slash-commands',
    '--no-session-persistence',
  ];
}

export function createClaudeCliProvider({
  command = 'claude',
  timeoutMs = DEFAULT_TIMEOUT_MS,
  spawnImpl = spawn,
  makeRepository = createSnapshotRepository,
} = {}) {
  return {
    id: 'claude-cli',
    async review({ caseObj, prompt, reviewStep }) {
      let dir;
      let result;
      try {
        dir = makeRepository(caseObj);
        result = await runProcess(spawnImpl, command, claudeReviewArgs(reviewStep), { cwd: dir, input: prompt, timeoutMs });
      } catch (error) {
        if (error?.code === 'ENOENT') {
          throw new ProviderUnavailableError(`"${command}" is not installed or not on PATH.`);
        }
        throw error;
      } finally {
        if (dir) rmSync(dir, { recursive: true, force: true });
      }
      let parsed;
      try {
        parsed = JSON.parse(result.stdout);
      } catch {
        throw new Error(`${command} exited ${result.code} without a JSON result.`);
      }
      const providerMetadata = {
        provider: 'claude-cli',
        modelsReported: Object.keys(parsed.modelUsage ?? {}),
        numTurns: parsed.num_turns ?? null,
        costUsd: parsed.total_cost_usd ?? null,
        durationMs: parsed.duration_ms ?? null,
      };
      if (parsed.is_error || parsed.structured_output === undefined) {
        const error = new Error(`${command} returned no structured review (${String(parsed.subtype ?? parsed.result ?? 'unknown').slice(0, 300)})`);
        error.providerMetadata = providerMetadata;
        throw error;
      }
      return { raw: parsed.structured_output, providerMetadata };
    },
  };
}

export function createFixtureProvider(responses) {
  return {
    id: 'fixture',
    async review({ caseObj }) {
      if (!Object.hasOwn(responses, caseObj.id)) {
        throw new Error(`No recorded response for ${caseObj.id}.`);
      }
      return { raw: responses[caseObj.id], providerMetadata: { provider: 'fixture' } };
    },
  };
}

// --- Run ---------------------------------------------------------------------------------------

async function mapWithConcurrency(items, limit, work) {
  const results = new Array(items.length);
  let next = 0;
  const worker = async () => {
    if (next >= items.length) return;
    const index = next;
    next += 1;
    results[index] = await work(items[index]);
    await worker();
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return results;
}

export async function runReviewEvals({ cases, provider, reviewStep, variant, addendum, concurrency = 1, log = () => {} }) {
  const results = await mapWithConcurrency(cases, concurrency, async (caseObj) => {
    const prompt = buildReviewPrompt(caseObj, { reviewStep, variant, addendum });
    const promptSha256 = createHash('sha256').update(prompt).digest('hex');
    log(`Running ${caseObj.id} (PR #${caseObj.pullRequest}, ${variant})...`);
    let outcome;
    try {
      outcome = await provider.review({ caseObj, prompt, reviewStep });
    } catch (error) {
      if (error instanceof ProviderUnavailableError) throw error;
      outcome = { error: error.message, providerMetadata: error.providerMetadata };
    }
    const graded = { ...gradeReviewCase(caseObj, outcome), promptSha256 };
    log(`${caseObj.id}: ${graded.status}`);
    return graded;
  });
  return {
    evalVersion: EVAL_VERSION,
    variant,
    provider: provider.id,
    reviewModel: reviewStep.model,
    reviewEffort: reviewStep.effort,
    maxTurns: reviewStep.maxTurns,
    timestamp: new Date().toISOString(),
    results,
    summary: summarizeReviewResults(results, cases),
  };
}

export function formatReport(report) {
  const s = report.summary;
  const lines = [
    `Review evals (${report.evalVersion}), variant ${report.variant}, provider ${report.provider}, model ${report.reviewModel}/${report.reviewEffort}`,
    '',
  ];
  for (const r of report.results) {
    lines.push(`${r.status}  ${r.id}  PR #${r.pullRequest}  verdict ${r.verdict ?? '-'} (expected ${r.expectedVerdict})`);
    for (const failure of r.failures) lines.push(`      ${failure}`);
    for (const other of r.otherBlockers ?? []) lines.push(`      other blocker: ${other.slice(0, 200)}`);
  }
  lines.push(
    '',
    `Known blockers caught: ${s.caughtFindings}/${s.expectedFindings}`,
    `False-alarm blockers: ${s.falseAlarms}; blockers on the READY control: ${s.controlBlockers}`,
    `Verdicts as expected: ${s.verdictMatches}/${s.cases}`,
    `Blockers naming file:line and a test: ${s.actionableBlockers}/${s.blockers}`,
    `Cases passed: ${s.passed}/${s.cases}; reported cost USD ${s.costUsd}`,
  );
  return lines.join('\n');
}

// --- CLI ---------------------------------------------------------------------------------------

export function parseArgs(argv) {
  const options = { variant: 'current', json: false, printPrompts: false, concurrency: 1 };
  for (let i = 0; i < argv.length; i += 1) {
    const flag = argv[i];
    const value = () => {
      i += 1;
      if (i >= argv.length) throw new Error(`${flag} needs a value.`);
      return argv[i];
    };
    switch (flag) {
      case '--provider': options.provider = value(); break;
      case '--variant': options.variant = value(); break;
      case '--cases': options.cases = value().split(',').map((id) => id.trim()).filter(Boolean); break;
      case '--responses': options.responses = value(); break;
      case '--output': options.output = value(); break;
      case '--timeout-ms': options.timeoutMs = Number(value()); break;
      case '--concurrency': options.concurrency = Number(value()); break;
      case '--json': options.json = true; break;
      case '--print-prompts': options.printPrompts = true; break;
      default: throw new Error(`Unknown option ${flag}.`);
    }
  }
  if (!VARIANTS.includes(options.variant)) throw new Error(`--variant must be one of ${VARIANTS.join(', ')}.`);
  if (!options.printPrompts && !['claude-cli', 'fixture'].includes(options.provider)) {
    throw new Error('--provider claude-cli or --provider fixture is required (there is no default).');
  }
  if (options.provider === 'fixture' && !options.responses) throw new Error('--provider fixture needs --responses <file>.');
  if (!Number.isInteger(options.concurrency) || options.concurrency < 1) throw new Error('--concurrency must be a positive integer.');
  return options;
}

function loadInputs(options) {
  let cases = loadReviewCases();
  if (options.cases) {
    const unknown = options.cases.filter((id) => !cases.some((c) => c.id === id));
    if (unknown.length > 0) throw new Error(`Unknown case id(s): ${unknown.join(', ')}`);
    cases = cases.filter((c) => options.cases.includes(c.id));
  }
  return {
    cases,
    reviewStep: extractReviewStep(readFileSync(REVIEW_WORKFLOW, 'utf8')),
    addendum: readFileSync(CANDIDATE_ADDENDUM, 'utf8'),
  };
}

function createProvider(options) {
  if (options.provider === 'fixture') {
    return createFixtureProvider(JSON.parse(readFileSync(options.responses, 'utf8')));
  }
  return createClaudeCliProvider({ timeoutMs: options.timeoutMs ?? DEFAULT_TIMEOUT_MS });
}

async function runAndReport(options, inputs, provider, log) {
  const report = await runReviewEvals({
    ...inputs,
    provider,
    variant: options.variant,
    concurrency: options.concurrency,
    log: options.json ? () => {} : log,
  });
  if (options.output) writeFileSync(options.output, `${JSON.stringify(report, null, 2)}\n`);
  log(options.json ? JSON.stringify(report, null, 2) : formatReport(report));
  return report.summary.passed === report.summary.cases ? 0 : 1;
}

export async function main(argv, { log = console.log, error = console.error, providerFactory } = {}) {
  let options;
  let inputs;
  try {
    options = parseArgs(argv);
    inputs = loadInputs(options);
  } catch (e) {
    error(e.message);
    return 2;
  }
  if (options.printPrompts) {
    for (const caseObj of inputs.cases) {
      log(`===== ${caseObj.id} (${options.variant}) =====`);
      log(buildReviewPrompt(caseObj, { ...inputs, variant: options.variant }));
    }
    return 0;
  }
  const provider = (providerFactory ?? createProvider)(options);
  try {
    return await runAndReport(options, inputs, provider, log);
  } catch (e) {
    if (e instanceof ProviderUnavailableError) {
      error(e.message);
      return 3;
    }
    throw e;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = await main(process.argv.slice(2));
}
