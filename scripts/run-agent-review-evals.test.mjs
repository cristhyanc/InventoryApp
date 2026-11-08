import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

import {
  CANDIDATE_ADDENDUM,
  EVAL_ALLOWED_TOOLS,
  EVAL_TOOLS,
  REVIEW_WORKFLOW,
  applyVariant,
  buildReviewPrompt,
  claudeReviewArgs,
  createClaudeCliProvider,
  createFixtureProvider,
  extractReviewStep,
  gradeReviewCase,
  isActionable,
  loadReviewCases,
  main,
  parseArgs,
  resolveExpressions,
  runReviewEvals,
  validateReviewCase,
} from './run-agent-review-evals.mjs';

const workflowText = readFileSync(REVIEW_WORKFLOW, 'utf8');
const addendum = readFileSync(CANDIDATE_ADDENDUM, 'utf8');
const cases = loadReviewCases();
const reviewStep = extractReviewStep(workflowText);
const byId = Object.fromEntries(cases.map((c) => [c.id, c]));

function review(overrides = {}) {
  return {
    reviewed_head_sha: 'a'.repeat(40),
    verdict: 'CHANGES REQUESTED',
    blockers: [],
    criteria: [{ criterion: 'c', status: 'met', evidence: 'e' }],
    suggestions: [],
    validation_evidence: 'ci',
    inline_comments: [],
    ...overrides,
  };
}

test('the review step is read from agent-review.yml with its own model, effort, turns and schema', () => {
  assert.match(reviewStep.prompt, /^You are the InventoryApp review agent/);
  assert.match(reviewStep.prompt, /Hard limits:/);
  assert.equal(typeof reviewStep.model, 'string');
  assert.equal(typeof reviewStep.effort, 'string');
  assert.ok(Number.isInteger(reviewStep.maxTurns) && reviewStep.maxTurns > 0);
  assert.deepEqual(reviewStep.jsonSchema.required.slice().sort(), [
    'blockers', 'criteria', 'inline_comments', 'reviewed_head_sha', 'suggestions', 'validation_evidence', 'verdict',
  ]);
});

test('every workflow expression in the prompt is resolved and an unknown one is refused', () => {
  for (const variant of ['current', 'candidate']) {
    for (const caseObj of cases) {
      const prompt = buildReviewPrompt(caseObj, { reviewStep, variant, addendum });
      assert.doesNotMatch(prompt, /\$\{\{/);
      assert.ok(prompt.includes(caseObj.headSha));
    }
  }
  assert.throws(() => resolveExpressions('x ${{ secrets.TOKEN }}', {}), /secrets\.TOKEN/);
});

test('the prompt never contains the rubric', () => {
  for (const caseObj of cases) {
    const prompt = buildReviewPrompt(caseObj, { reviewStep, variant: 'candidate', addendum });
    assert.ok(!prompt.includes(caseObj.source), `${caseObj.id} leaks its source`);
    for (const matcher of [...caseObj.expected.findings, ...caseObj.expected.forbiddenBlockers]) {
      assert.ok(!prompt.includes(matcher.summary), `${caseObj.id} leaks ${matcher.id}`);
      assert.ok(!prompt.includes(matcher.id), `${caseObj.id} leaks the id ${matcher.id}`);
    }
  }
});

test('the candidate inserts the addendum before the delivery instructions; current is unchanged', () => {
  const current = applyVariant(reviewStep.prompt, 'current', addendum);
  const candidate = applyVariant(reviewStep.prompt, 'candidate', addendum);
  assert.equal(current, reviewStep.prompt);
  assert.ok(candidate.indexOf('Finding quality') < candidate.indexOf('Deliver the result only as the structured output'));
  assert.throws(() => applyVariant('no anchor here', 'candidate', addendum), /insertion point/);
  assert.throws(() => applyVariant(reviewStep.prompt, 'other', addendum), /Unknown variant/);
});

test('the set has a READY control and every case is valid', () => {
  assert.ok(cases.length >= 4);
  assert.ok(cases.some((c) => c.expected.verdict === 'READY FOR HUMAN REVIEW'));
  for (const caseObj of cases) assert.deepEqual(validateReviewCase(caseObj), []);
  const broken = structuredClone(cases[0]);
  broken.headSha = 'abc';
  broken.expected.findings = [];
  const errors = validateReviewCase(broken);
  assert.ok(errors.some((e) => e.includes('headSha')));
  assert.ok(errors.some((e) => e.includes('at least one expected finding')));
});

test('a known blocker raised as a blocker is caught; raised only as a suggestion it is missed', () => {
  const caseObj = byId['REVIEW-001'];
  const blocker = 'backend/Inventory.Application/MachineStockSync/ApplyMachineStockSync.cs:190 CommitAsync is outside the try, so a commit exception escapes and later products are never applied.';
  const caught = gradeReviewCase(caseObj, { raw: review({ blockers: [blocker] }) });
  assert.equal(caught.status, 'PASS');
  assert.equal(caught.findings[0].caught, true);

  const suggested = gradeReviewCase(caseObj, { raw: review({ verdict: 'CHANGES REQUESTED', blockers: ['something else'], suggestions: [blocker] }) });
  assert.equal(suggested.status, 'FAIL');
  assert.equal(suggested.findings[0].onlySuggested, true);
  assert.deepEqual(suggested.otherBlockers, ['something else']);
});

test('an inline comment counts as raising the finding', () => {
  const caseObj = byId['REVIEW-002'];
  const result = gradeReviewCase(caseObj, {
    raw: review({ inline_comments: [{ path: 'frontend/inventory-app/src/app/components/sites/site-list.component.html', line: 164, body: 'whitespace-nowrap keeps a long unbroken name on one line, clipped in the mobile card.' }] }),
  });
  assert.equal(result.findings[0].caught, true);
  assert.equal(result.status, 'PASS');
});

test('the control fails on the forbidden blocker and on a wrong verdict, and counts other blockers', () => {
  const caseObj = byId['REVIEW-004'];
  const ok = gradeReviewCase(caseObj, { raw: review({ verdict: 'READY FOR HUMAN REVIEW' }) });
  assert.equal(ok.status, 'PASS');
  const alarm = gradeReviewCase(caseObj, {
    raw: review({ verdict: 'CHANGES REQUESTED', blockers: ['SyncLatestNayaxSales.cs:364 the live shortcut also skips a forced Refresh.'] }),
  });
  assert.equal(alarm.status, 'FAIL');
  assert.equal(alarm.falseAlarms.length, 1);
  assert.ok(alarm.failures.some((f) => f.startsWith('Verdict')));
});

test('malformed responses and provider errors fail the case', () => {
  const caseObj = byId['REVIEW-001'];
  assert.equal(gradeReviewCase(caseObj, { raw: 'not json' }).status, 'FAIL');
  assert.equal(gradeReviewCase(caseObj, { raw: { verdict: 'MAYBE' } }).status, 'FAIL');
  assert.equal(gradeReviewCase(caseObj, { error: 'boom' }).status, 'FAIL');
});

test('actionable means a file:line and a test', () => {
  assert.equal(isActionable('a/b.cs:12 fails. Regression test: FooTests asserts it.'), true);
  assert.equal(isActionable('a/b.cs fails, add a test'), false);
  assert.equal(isActionable('a/b.cs:12 fails'), false);
});

test('the eval reviewer gets only local read tools and read-only git', () => {
  const args = claudeReviewArgs(reviewStep);
  assert.equal(args[args.indexOf('--tools') + 1], EVAL_TOOLS.join(','));
  assert.equal(args[args.indexOf('--allowedTools') + 1], EVAL_ALLOWED_TOOLS.join(','));
  for (const rule of EVAL_ALLOWED_TOOLS) assert.match(rule, /^Bash\(git (diff|log|show|status) \*\)$/);
  assert.ok(!EVAL_TOOLS.some((tool) => /Edit|Write|Web/.test(tool)));
  assert.ok(args.includes('--strict-mcp-config'));
  assert.ok(args.includes('--no-session-persistence'));
  assert.equal(args[args.indexOf('--model') + 1], reviewStep.model);
});

function fakeSpawn(stdout, seen) {
  return (command, args, options) => {
    seen.push({ command, args, options });
    const child = new EventEmitter();
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    child.stdin = new EventEmitter();
    child.stdin.end = () => {
      setImmediate(() => {
        child.stdout.emit('data', stdout);
        child.emit('close', 0);
      });
    };
    child.kill = () => {};
    return child;
  };
}

test('the CLI provider runs in the snapshot repository and returns the structured output', async () => {
  const seen = [];
  const provider = createClaudeCliProvider({
    spawnImpl: fakeSpawn(JSON.stringify({ structured_output: review(), total_cost_usd: 1.5, num_turns: 9, modelUsage: { m: {} } }), seen),
    makeRepository: () => '/nonexistent-review-eval-dir',
  });
  const out = await provider.review({ caseObj: byId['REVIEW-001'], prompt: 'p', reviewStep });
  assert.equal(seen[0].options.cwd, '/nonexistent-review-eval-dir');
  assert.equal(out.providerMetadata.costUsd, 1.5);
  assert.equal(out.raw.verdict, 'CHANGES REQUESTED');
});

test('a CLI result without structured output is an error, not a pass', async () => {
  const provider = createClaudeCliProvider({
    spawnImpl: fakeSpawn(JSON.stringify({ is_error: true, subtype: 'error_max_turns' }), []),
    makeRepository: () => '/nonexistent-review-eval-dir',
  });
  await assert.rejects(provider.review({ caseObj: byId['REVIEW-001'], prompt: 'p', reviewStep }), /error_max_turns/);
});

test('a fixture run grades every case and summarises caught, missed and false alarms', async () => {
  const responses = {
    'REVIEW-001': review({ blockers: ['ApplyMachineStockSync.cs:190 a failed commit throws and later products never run. Regression test: CommitFailureTests.'] }),
    'REVIEW-002': review({ blockers: ['unrelated'] }),
    'REVIEW-003': review({ blockers: ['docs/price-collector-testing.md:461 the bearer token is stored in plain text in the Logic App definition.'] }),
    'REVIEW-004': review({ verdict: 'READY FOR HUMAN REVIEW' }),
  };
  const report = await runReviewEvals({ cases, provider: createFixtureProvider(responses), reviewStep, variant: 'current', addendum });
  assert.equal(report.summary.expectedFindings, 3);
  assert.equal(report.summary.caughtFindings, 2);
  assert.equal(report.summary.missedFindings, 1);
  assert.equal(report.summary.falseAlarms, 0);
  assert.equal(report.summary.verdictMatches, 4);
  assert.equal(report.summary.actionableBlockers, 1);
  assert.equal(report.summary.passed, 3);
});

test('arguments: a provider is required and variants are checked', async () => {
  assert.throws(() => parseArgs([]), /provider/);
  assert.throws(() => parseArgs(['--provider', 'claude-cli', '--variant', 'x']), /variant/);
  assert.throws(() => parseArgs(['--provider', 'fixture']), /responses/);
  assert.equal(parseArgs(['--print-prompts']).printPrompts, true);
  const errors = [];
  assert.equal(await main(['--provider', 'nope'], { log: () => {}, error: (m) => errors.push(m) }), 2);
  assert.equal(await main(['--print-prompts', '--cases', 'REVIEW-999'], { log: () => {}, error: (m) => errors.push(m) }), 2);
});
