// Deterministic tests for the model-decision eval runner. No model is called: every provider
// here is a fixture or a fake process.
// Run with: node --test scripts/run-agent-model-evals.test.mjs
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { after, before, describe, it } from 'node:test';

import { DEFAULT_CASES_DIR, loadCorpus, repositoryRoot } from './run-agent-evals.mjs';
import {
  DEFAULT_SET_PATH,
  EVAL_VERSION,
  EXIT_FAILED,
  EXIT_OK,
  EXIT_PROVIDER_UNAVAILABLE,
  EXIT_USAGE,
  MAX_RATIONALE_LENGTH,
  PROMPT_VERSION,
  ProviderUnavailableError,
  buildDecisionPrompt,
  buildPolicyContext,
  claudeCliArgs,
  createClaudeCliProvider,
  createFixtureProvider,
  extractMarkdownSection,
  gradeCase,
  main,
  parseDecisionResponse,
  readGitHead,
  runModelEvals,
  summarize,
  validateModelEvalSet,
} from './run-agent-model-evals.mjs';

// --- Fixtures ---------------------------------------------------------------------------

const corpus = loadCorpus(DEFAULT_CASES_DIR).cases;
const casesById = new Map(corpus.map((c) => [c.id, c]));
const setText = readFileSync(DEFAULT_SET_PATH, 'utf8');
const realSet = JSON.parse(setText);

function response(decision, overrides = {}) {
  return {
    decision,
    rationale: 'Grounded in the cited rule.',
    invariants: ['AGENTS.md § Required workflow'],
    humanDecisionRequired: decision === 'stop',
    ...overrides,
  };
}

// The correct structured answer for every case in the real set.
function correctResponses() {
  return Object.fromEntries(realSet.cases.map((entry) => [entry.id, response(casesById.get(entry.id).expectedDecision)]));
}

function entryFor(id) {
  return realSet.cases.find((entry) => entry.id === id);
}

let scratch;
before(() => {
  scratch = mkdtempSync(join(tmpdir(), 'model-evals-test-'));
});
after(() => {
  rmSync(scratch, { recursive: true, force: true });
});

// --- The committed set --------------------------------------------------------------------

describe('committed model-decision set', () => {
  it('is valid against the committed corpus', () => {
    assert.deepEqual(validateModelEvalSet(realSet, casesById), []);
  });

  it('holds the six critical scenarios from issue #249, including the proceed control', () => {
    const ids = realSet.cases.map((entry) => entry.id);
    assert.deepEqual(ids, ['INV-004', 'INV-007', 'TENANT-002', 'DB-001', 'AUTH-003', 'AUTH-014']);
    const decisions = ids.map((id) => casesById.get(id).expectedDecision);
    assert.deepEqual(decisions, ['reject', 'reject', 'reject', 'stop', 'stop', 'proceed']);
    assert.ok(ids.every((id) => casesById.get(id).critical));
    assert.notEqual(casesById.get('AUTH-014').risk, 'high');
  });
});

// --- validateModelEvalSet -----------------------------------------------------------------

describe('validateModelEvalSet', () => {
  const withCases = (cases) => ({ version: 1, cases });

  it('rejects an unknown case id', () => {
    const errors = validateModelEvalSet(withCases([...realSet.cases, { id: 'NOPE-001', checks: [] }]), casesById);
    assert.ok(errors.some((e) => e.includes('NOPE-001')));
  });

  it('rejects a set without a proceed case, so always-reject/stop cannot pass', () => {
    const errors = validateModelEvalSet(withCases(realSet.cases.filter((e) => e.id !== 'AUTH-014')), casesById);
    assert.ok(errors.some((e) => e.includes('"proceed"')));
  });

  it('rejects a check that would fail the expected decision', () => {
    const bad = { id: 'AUTH-014', checks: [{ type: 'decisionNotIn', decisions: ['proceed'], covers: [0] }] };
    const errors = validateModelEvalSet(withCases([entryFor('INV-004'), bad]), casesById);
    assert.ok(errors.some((e) => e.includes('own expected decision')));
  });

  it('rejects a stop case that forbids asking a human', () => {
    const bad = { id: 'DB-001', checks: [{ type: 'humanDecisionRequired', equals: false, covers: [0] }] };
    const errors = validateModelEvalSet(withCases([bad, entryFor('AUTH-014')]), casesById);
    assert.ok(errors.some((e) => e.includes('cannot forbid')));
  });

  it('rejects covers indexes outside forbiddenOutcomes, and unknown check types', () => {
    const errors = validateModelEvalSet(
      withCases([
        { id: 'INV-004', checks: [{ type: 'decisionNotIn', decisions: ['proceed'], covers: [9] }] },
        { id: 'AUTH-014', checks: [{ type: 'llmJudge', covers: [0] }] },
      ]),
      casesById,
    );
    assert.ok(errors.some((e) => e.includes('"covers"')));
    assert.ok(errors.some((e) => e.includes('"type"')));
  });

  it('rejects duplicates and a missing version', () => {
    const errors = validateModelEvalSet({ cases: [entryFor('INV-004'), entryFor('INV-004'), entryFor('AUTH-014')] }, casesById);
    assert.ok(errors.some((e) => e.includes('repeats')));
    assert.ok(errors.some((e) => e.includes('"version"')));
  });
});

// --- Policy context and prompt ------------------------------------------------------------

describe('extractMarkdownSection', () => {
  const doc = ['# Top', 'intro', '## A', 'a text', '### A.1', 'nested', '## B', 'b text'].join('\n');

  it('returns the section through its subsections, stopping at the next peer heading', () => {
    assert.equal(extractMarkdownSection(doc, 'A'), ['## A', 'a text', '### A.1', 'nested'].join('\n'));
    assert.equal(extractMarkdownSection(doc, 'B'), ['## B', 'b text'].join('\n'));
  });

  it('throws for a missing heading', () => {
    assert.throws(() => extractMarkdownSection(doc, 'Missing'), /not found/);
  });
});

describe('buildPolicyContext', () => {
  it('always sends AGENTS.md and CLAUDE.md, plus cited docs sections only', () => {
    const sources = buildPolicyContext(casesById.get('AUTH-014')).map((d) => d.source);
    assert.deepEqual(sources, ['AGENTS.md', 'CLAUDE.md', 'docs/automation.md § Risk classification']);
    const section = buildPolicyContext(casesById.get('AUTH-014'))[2].content;
    assert.ok(section.startsWith('## Risk classification'));
    assert.ok(!section.includes('## Failure and retry policy'));
  });

  it('never sends evals/ content or a non-Markdown source file', () => {
    for (const entry of realSet.cases) {
      for (const doc of buildPolicyContext(casesById.get(entry.id))) {
        assert.ok(!doc.source.startsWith('evals/'), doc.source);
        assert.ok(doc.source.split(' § ')[0].endsWith('.md'), doc.source);
      }
    }
  });
});

describe('buildDecisionPrompt hidden-rubric exclusion', () => {
  for (const entry of realSet.cases) {
    it(`${entry.id}: contains the scenario but no grading answer`, () => {
      const caseObj = casesById.get(entry.id);
      const prompt = buildDecisionPrompt(caseObj, buildPolicyContext(caseObj));
      assert.ok(prompt.includes(caseObj.scenario));
      for (const field of ['expectedDecision', 'expectedBehavior', 'forbiddenOutcomes', 'assertions', 'decisionNotIn', '"covers"']) {
        assert.ok(!prompt.includes(field), `prompt mentions ${field}`);
      }
      assert.ok(!prompt.includes(caseObj.expectedBehavior), 'prompt contains expectedBehavior');
      for (const outcome of caseObj.forbiddenOutcomes) {
        assert.ok(!prompt.includes(outcome), `prompt contains forbidden outcome: ${outcome}`);
      }
    });
  }

  it('does not read rubric fields at all: changing them leaves the prompt identical', () => {
    const caseObj = casesById.get('INV-004');
    const policy = buildPolicyContext(caseObj);
    const altered = { ...caseObj, expectedDecision: 'proceed', expectedBehavior: 'x', forbiddenOutcomes: ['y'], assertions: [] };
    assert.equal(buildDecisionPrompt(altered, policy), buildDecisionPrompt(caseObj, policy));
  });
});

// --- parseDecisionResponse -----------------------------------------------------------------

describe('parseDecisionResponse', () => {
  it('accepts a structured object', () => {
    const parsed = parseDecisionResponse(response('reject'));
    assert.equal(parsed.ok, true);
    assert.equal(parsed.value.decision, 'reject');
  });

  it('accepts JSON text, with or without a code fence', () => {
    assert.equal(parseDecisionResponse(JSON.stringify(response('stop'))).ok, true);
    assert.equal(parseDecisionResponse('```json\n' + JSON.stringify(response('stop')) + '\n```').ok, true);
    assert.equal(parseDecisionResponse('  ```\n' + JSON.stringify(response('stop')) + '\n```  ').ok, true);
  });

  it('does not unwrap a fence in another language', () => {
    assert.equal(parseDecisionResponse('```yaml\n' + JSON.stringify(response('stop')) + '\n```').ok, false);
  });

  it('handles a long unterminated fence quickly (no regex backtracking)', () => {
    const started = Date.now();
    assert.equal(parseDecisionResponse('```json\n' + ' \n'.repeat(50000)).ok, false);
    assert.ok(Date.now() - started < 1000);
  });

  const invalid = [
    ['plain prose', 'I would reject this.'],
    ['a JSON array', '[]'],
    ['null', null],
    ['an unknown decision', response('approve')],
    ['a missing rationale', response('reject', { rationale: '' })],
    ['an over-long rationale', response('reject', { rationale: 'x'.repeat(MAX_RATIONALE_LENGTH + 1) })],
    ['invariants that are not strings', response('reject', { invariants: [1] })],
    ['a missing humanDecisionRequired', (({ humanDecisionRequired, ...rest }) => rest)(response('reject'))],
    ['a self-graded extra field', response('reject', { passed: true })],
  ];
  for (const [label, raw] of invalid) {
    it(`rejects ${label}`, () => {
      const parsed = parseDecisionResponse(raw);
      assert.equal(parsed.ok, false);
      assert.ok(parsed.error.length > 0);
    });
  }
});

// --- gradeCase and summarize ---------------------------------------------------------------

describe('gradeCase', () => {
  it('passes a correct decision whose forbidden-outcome checks hold', () => {
    const result = gradeCase(casesById.get('DB-001'), entryFor('DB-001'), { response: response('stop') });
    assert.equal(result.status, 'PASS');
    assert.ok(result.forbiddenOutcomeResults.every((r) => r.status === 'PASS'));
  });

  it('fails a deliberately wrong decision', () => {
    const result = gradeCase(casesById.get('INV-004'), entryFor('INV-004'), { response: response('proceed') });
    assert.equal(result.status, 'FAIL');
    assert.ok(result.failures.some((f) => f.includes('decision mismatch')));
    assert.ok(result.forbiddenOutcomeResults.some((r) => r.status === 'FAIL'));
  });

  it('fails the right decision word when a forbidden outcome is checked deterministically', () => {
    const result = gradeCase(casesById.get('AUTH-003'), entryFor('AUTH-003'), {
      response: response('stop', { humanDecisionRequired: false }),
    });
    assert.equal(result.status, 'FAIL');
    assert.ok(result.failures.some((f) => f.includes('humanDecisionRequired must be true')));
  });

  it('fails the proceed control when the model needlessly escalates', () => {
    const result = gradeCase(casesById.get('AUTH-014'), entryFor('AUTH-014'), { response: response('stop') });
    assert.equal(result.status, 'FAIL');
  });

  it('fails, never skips, an invalid response', () => {
    const result = gradeCase(casesById.get('INV-004'), entryFor('INV-004'), { error: 'response is not valid JSON.' });
    assert.equal(result.status, 'FAIL');
    assert.equal(result.actual, null);
  });

  it('lists forbidden outcomes that no deterministic check covers', () => {
    const entry = { id: 'DB-001', checks: [{ type: 'humanDecisionRequired', equals: true, covers: [0] }] };
    const result = gradeCase(casesById.get('DB-001'), entry, { response: response('stop') });
    assert.deepEqual(result.forbiddenOutcomesNotDeterministicallyChecked, [casesById.get('DB-001').forbiddenOutcomes[1]]);
  });
});

describe('summarize', () => {
  const result = (id, status, critical, category = 'agent-authority') => ({ id, status, critical, category });

  it('fails on a critical failure even when the aggregate rate meets the minimum', () => {
    const results = [result('A-001', 'PASS', false), result('A-002', 'PASS', false), result('A-003', 'FAIL', true)];
    const { summary, ok } = summarize(results, { minPassRate: 0.5 });
    assert.equal(ok, false);
    assert.deepEqual(summary.criticalFailures, ['A-003']);
  });

  it('allows a non-critical failure only when the rate meets a lowered minimum', () => {
    const results = [result('A-001', 'PASS', true), result('A-002', 'FAIL', false, 'time')];
    assert.equal(summarize(results, { minPassRate: 0.5 }).ok, true);
    assert.equal(summarize(results).ok, false);
    assert.deepEqual(summarize(results).summary.passRateByCategory, { 'agent-authority': 1, time: 0 });
  });

  it('never passes an empty run', () => {
    assert.equal(summarize([]).ok, false);
  });
});

// --- runModelEvals ---------------------------------------------------------------------------

describe('runModelEvals', () => {
  const fixedNow = () => new Date('2026-10-05T00:00:00Z');

  it('passes when every structured decision is correct, and records reproducibility metadata', async () => {
    const report = await runModelEvals({
      cases: corpus,
      set: realSet,
      provider: createFixtureProvider(correctResponses()),
      now: fixedNow,
      corpus: { sha256: 'abc' },
    });
    assert.equal(report.status, 'completed');
    assert.equal(report.ok, true);
    assert.equal(report.summary.passed, realSet.cases.length);
    assert.equal(report.metadata.evalVersion, EVAL_VERSION);
    assert.equal(report.metadata.promptVersion, PROMPT_VERSION);
    assert.equal(report.metadata.generatedAt, '2026-10-05T00:00:00.000Z');
    assert.equal(report.metadata.corpus.setVersion, realSet.version);
    assert.equal(report.metadata.corpus.sha256, 'abc');
    assert.equal(report.metadata.provider.id, 'fixture');
    assert.ok('sha' in report.metadata.git);
    assert.ok(report.results.every((r) => /^[0-9a-f]{64}$/.test(r.promptSha256)));
    assert.equal(report.metadata.configuration.tools, 'none');
    assert.doesNotThrow(() => JSON.parse(JSON.stringify(report)));
  });

  it('fails an always-reject strategy on the critical proceed control', async () => {
    const alwaysReject = Object.fromEntries(realSet.cases.map((e) => [e.id, response('reject')]));
    const report = await runModelEvals({ cases: corpus, set: realSet, provider: createFixtureProvider(alwaysReject) });
    assert.equal(report.ok, false);
    assert.ok(report.summary.criticalFailures.includes('AUTH-014'));
    assert.ok(report.summary.criticalFailures.includes('DB-001'));
  });

  it('fails an always-proceed strategy on every reject/stop case', async () => {
    const alwaysProceed = Object.fromEntries(realSet.cases.map((e) => [e.id, response('proceed')]));
    const report = await runModelEvals({ cases: corpus, set: realSet, provider: createFixtureProvider(alwaysProceed) });
    assert.equal(report.ok, false);
    assert.equal(report.summary.criticalFailures.length, realSet.cases.length - 1);
  });

  it('fails a case whose response is malformed or missing', async () => {
    const responses = { ...correctResponses(), 'INV-007': 'not json' };
    delete responses['TENANT-002'];
    const report = await runModelEvals({ cases: corpus, set: realSet, provider: createFixtureProvider(responses) });
    assert.equal(report.ok, false);
    assert.deepEqual(report.summary.criticalFailures.sort(), ['INV-007', 'TENANT-002']);
  });

  it('reports an unavailable provider as unavailable with no results, never as a pass', async () => {
    const provider = {
      id: 'claude-cli',
      async describe() {
        throw new ProviderUnavailableError('"claude" is not installed or not on PATH.');
      },
      async complete() {
        throw new Error('must not be called');
      },
    };
    const report = await runModelEvals({ cases: corpus, set: realSet, provider });
    assert.equal(report.status, 'unavailable');
    assert.equal(report.ok, false);
    assert.deepEqual(report.results, []);
  });

  it('turns a per-case provider error into a FAIL for that case', async () => {
    const provider = createFixtureProvider(correctResponses());
    const complete = provider.complete;
    provider.complete = async (request) => {
      if (request.caseId === 'DB-001') {
        throw new Error('claude reported an error: rate limited');
      }
      return complete(request);
    };
    const report = await runModelEvals({ cases: corpus, set: realSet, provider });
    assert.deepEqual(report.summary.criticalFailures, ['DB-001']);
  });
});

// --- readGitHead ------------------------------------------------------------------------------

describe('readGitHead', () => {
  it('matches git for this checkout', (t) => {
    let expected;
    try {
      expected = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repositoryRoot, encoding: 'utf8' }).trim();
    } catch {
      t.skip('git is not available');
      return;
    }
    assert.equal(readGitHead(repositoryRoot), expected);
  });

  it('reads loose refs, packed refs, a worktree .git file and a detached HEAD', () => {
    const sha = 'a'.repeat(40);
    const repo = join(scratch, 'repo');
    mkdirSync(join(repo, '.git', 'refs', 'heads'), { recursive: true });
    writeFileSync(join(repo, '.git', 'HEAD'), 'ref: refs/heads/main\n');
    writeFileSync(join(repo, '.git', 'refs', 'heads', 'main'), `${sha}\n`);
    assert.equal(readGitHead(repo), sha);

    rmSync(join(repo, '.git', 'refs', 'heads', 'main'));
    writeFileSync(join(repo, '.git', 'packed-refs'), `# pack-refs\n${'b'.repeat(40)} refs/heads/main\n`);
    assert.equal(readGitHead(repo), 'b'.repeat(40));

    const worktree = join(scratch, 'worktree');
    const worktreeGitDir = join(repo, '.git', 'worktrees', 'wt');
    mkdirSync(worktreeGitDir, { recursive: true });
    mkdirSync(worktree, { recursive: true });
    writeFileSync(join(worktree, '.git'), `gitdir: ${worktreeGitDir}\n`);
    writeFileSync(join(worktreeGitDir, 'HEAD'), 'ref: refs/heads/main\n');
    writeFileSync(join(worktreeGitDir, 'commondir'), '../..\n');
    assert.equal(readGitHead(worktree), 'b'.repeat(40));

    writeFileSync(join(repo, '.git', 'HEAD'), `${'c'.repeat(40)}\n`);
    assert.equal(readGitHead(repo), 'c'.repeat(40));
  });

  it('returns null outside a repository', () => {
    assert.equal(readGitHead(join(scratch, 'not-a-repo')), null);
  });
});

// --- Claude CLI provider ---------------------------------------------------------------------

function fakeSpawn({ stdout = '', code = 0, error } = {}) {
  const calls = [];
  const spawnImpl = (command, args, options) => {
    const child = new EventEmitter();
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    child.kill = () => {};
    const call = { command, args, options, input: null, cwdExisted: existsSync(options.cwd) };
    calls.push(call);
    child.stdin = {
      on() {},
      end(input) {
        call.input = input;
        setImmediate(() => {
          if (error) {
            child.emit('error', error);
            return;
          }
          child.stdout.emit('data', typeof stdout === 'function' ? stdout(args) : stdout);
          child.emit('close', code);
        });
      },
    };
    return child;
  };
  return { spawnImpl, calls };
}

describe('createClaudeCliProvider', () => {
  const cliResult = JSON.stringify({
    is_error: false,
    num_turns: 2,
    total_cost_usd: 0.01,
    modelUsage: { 'claude-model-under-test': {} },
    result: '{"ignored": true}',
    structured_output: response('reject'),
  });

  it('runs the CLI tool-less and isolated, with the prompt on stdin, outside the repository', async () => {
    const { spawnImpl, calls } = fakeSpawn({ stdout: cliResult });
    const provider = createClaudeCliProvider({ spawnImpl, model: 'model-x' });
    const { raw, providerMetadata } = await provider.complete({ caseId: 'INV-004', prompt: 'PROMPT TEXT' });

    assert.deepEqual(raw, response('reject'));
    assert.deepEqual(providerMetadata.modelsReported, ['claude-model-under-test']);
    const [call] = calls;
    assert.equal(call.command, 'claude');
    assert.equal(call.input, 'PROMPT TEXT');
    assert.equal(call.args[call.args.indexOf('--tools') + 1], '');
    assert.equal(call.args[call.args.indexOf('--setting-sources') + 1], '');
    for (const flag of ['-p', '--strict-mcp-config', '--no-session-persistence', '--json-schema']) {
      assert.ok(call.args.includes(flag), flag);
    }
    assert.equal(call.args[call.args.indexOf('--model') + 1], 'model-x');
    assert.ok(!call.args.some((arg) => /dangerously|bypassPermissions|--add-dir|--allowedTools/.test(arg)));
    assert.ok(call.cwdExisted);
    assert.ok(!resolve(call.options.cwd).startsWith(repositoryRoot));
    assert.equal(existsSync(call.options.cwd), false, 'temporary directory is removed');
  });

  it('omits --model when none is requested, so no model is chosen silently', () => {
    assert.ok(!claudeCliArgs().includes('--model'));
  });

  it('reports a missing CLI as unavailable', async () => {
    const error = Object.assign(new Error('spawn claude ENOENT'), { code: 'ENOENT' });
    const provider = createClaudeCliProvider({ spawnImpl: fakeSpawn({ error }).spawnImpl });
    await assert.rejects(provider.describe(), ProviderUnavailableError);
    await assert.rejects(provider.complete({ caseId: 'X', prompt: 'p' }), ProviderUnavailableError);
  });

  it('reports a CLI error result or non-JSON output as an ordinary error', async () => {
    const errored = createClaudeCliProvider({
      spawnImpl: fakeSpawn({ stdout: JSON.stringify({ is_error: true, result: 'Not logged in' }), code: 1 }).spawnImpl,
    });
    await assert.rejects(errored.complete({ caseId: 'X', prompt: 'p' }), (e) => !(e instanceof ProviderUnavailableError) && /Not logged in/.test(e.message));

    const garbled = createClaudeCliProvider({ spawnImpl: fakeSpawn({ stdout: 'oops', code: 1 }).spawnImpl });
    await assert.rejects(garbled.complete({ caseId: 'X', prompt: 'p' }), /without a JSON result/);
  });

  it('describes the CLI version for the result metadata', async () => {
    const provider = createClaudeCliProvider({ spawnImpl: fakeSpawn({ stdout: '9.9.9 (Claude Code)\n' }).spawnImpl });
    assert.deepEqual(await provider.describe(), {
      id: 'claude-cli',
      command: 'claude',
      cliVersion: '9.9.9 (Claude Code)',
      requestedModel: null,
    });
  });
});

// --- main ------------------------------------------------------------------------------------

describe('main', () => {
  const quiet = () => {};

  it('refuses to run without an explicitly chosen provider', async () => {
    assert.equal(await main([], { log: quiet, error: quiet }), EXIT_USAGE);
    assert.equal(await main(['--provider', 'openai'], { log: quiet, error: quiet }), EXIT_USAGE);
    assert.equal(await main(['--provider', 'fixture'], { log: quiet, error: quiet }), EXIT_USAGE);
  });

  it('prints prompts without calling any provider', async () => {
    const out = [];
    const code = await main(['--print-prompts', '--cases', 'AUTH-014'], {
      log: (s) => out.push(s),
      error: quiet,
      providerFactory: () => {
        throw new Error('must not be called');
      },
    });
    assert.equal(code, EXIT_OK);
    assert.ok(out.join('\n').includes('Case: AUTH-014'));
  });

  it('runs fixture responses end to end and writes machine-readable JSON', async () => {
    const responsesPath = join(scratch, 'responses.json');
    const outputPath = join(scratch, 'report.json');
    writeFileSync(responsesPath, JSON.stringify(correctResponses()));
    const out = [];
    const code = await main(['--provider', 'fixture', '--responses', responsesPath, '--json', '--output', outputPath], {
      log: (s) => out.push(s),
      error: quiet,
    });
    assert.equal(code, EXIT_OK);
    const report = JSON.parse(readFileSync(outputPath, 'utf8'));
    assert.deepEqual(JSON.parse(out.join('\n')), report);
    assert.equal(report.metadata.corpus.sha256.length, 64);
    assert.deepEqual(report.metadata.corpus.caseIds, realSet.cases.map((e) => e.id));
    assert.equal(report.results.length, realSet.cases.length);
  });

  it('exits 1 on a critical failure', async () => {
    const responsesPath = join(scratch, 'wrong.json');
    writeFileSync(responsesPath, JSON.stringify({ ...correctResponses(), 'TENANT-002': response('proceed') }));
    const code = await main(['--provider', 'fixture', '--responses', responsesPath], { log: quiet, error: quiet });
    assert.equal(code, EXIT_FAILED);
  });

  it('exits 3 when the provider is unavailable', async () => {
    const providerFactory = () =>
      createClaudeCliProvider({ spawnImpl: fakeSpawn({ error: Object.assign(new Error('ENOENT'), { code: 'ENOENT' }) }).spawnImpl });
    const code = await main(['--provider', 'claude-cli'], { log: quiet, error: quiet, providerFactory });
    assert.equal(code, EXIT_PROVIDER_UNAVAILABLE);
  });

  it('rejects a --cases id outside the set', async () => {
    assert.equal(await main(['--print-prompts', '--cases', 'TIME-001'], { log: quiet, error: quiet }), EXIT_USAGE);
  });
});
