// Deterministic tests for the InventoryApp Agent Evals runner.
// Run with: node --test scripts/run-agent-evals.test.mjs
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, before, describe, it } from 'node:test';

import {
  DECISIONS,
  MIN_CORPUS_SIZE,
  REQUIRED_CATEGORIES,
  buildModelEvalPrompt,
  buildReport,
  evaluateCase,
  loadCorpus,
  main,
  validateCase,
  validateCorpus,
} from './run-agent-evals.mjs';

// --- Fixtures ---------------------------------------------------------------------------

function baseCase(overrides = {}) {
  return {
    id: 'INV-001',
    category: 'inventory-costing',
    risk: 'high',
    critical: true,
    scenario: 'A scenario long enough to pass the minimum length check.',
    policyReferences: ['AGENTS.md § Somewhere'],
    expectedDecision: 'reject',
    expectedBehavior: 'The expected compliant behaviour.',
    forbiddenOutcomes: ['Doing the forbidden thing.'],
    assertions: [{ type: 'repoFileContains', file: 'policy.md', text: 'the guarded phrase' }],
    ...overrides,
  };
}

let root;

before(() => {
  root = mkdtempSync(join(tmpdir(), 'agent-evals-test-'));
  writeFileSync(join(root, 'policy.md'), 'Some preamble.\nThis file contains the guarded phrase and must keep it.\nTrailer.\n');
});

after(() => {
  rmSync(root, { recursive: true, force: true });
});

// --- validateCase -------------------------------------------------------------------------

describe('validateCase', () => {
  it('accepts a well-formed case', () => {
    const errors = validateCase(baseCase(), { root });
    assert.deepEqual(errors, []);
  });

  it('rejects a non-object case', () => {
    assert.equal(validateCase(null, { root }).length, 1);
    assert.equal(validateCase('x', { root }).length, 1);
    assert.equal(validateCase([], { root }).length, 1);
  });

  it('rejects a malformed id', () => {
    const errors = validateCase(baseCase({ id: 'inv1' }), { root });
    assert.ok(errors.some((e) => e.includes('"id"')));
  });

  it('rejects an unknown category', () => {
    const errors = validateCase(baseCase({ category: 'wrong' }), { root });
    assert.ok(errors.some((e) => e.includes('"category"')));
  });

  it('rejects an unknown risk level', () => {
    const errors = validateCase(baseCase({ risk: 'extreme' }), { root });
    assert.ok(errors.some((e) => e.includes('"risk"')));
  });

  it('rejects a non-boolean critical flag', () => {
    const errors = validateCase(baseCase({ critical: 'yes' }), { root });
    assert.ok(errors.some((e) => e.includes('"critical"')));
  });

  it('rejects a too-short scenario', () => {
    const errors = validateCase(baseCase({ scenario: 'too short' }), { root });
    assert.ok(errors.some((e) => e.includes('"scenario"')));
  });

  it('rejects empty policyReferences', () => {
    const errors = validateCase(baseCase({ policyReferences: [] }), { root });
    assert.ok(errors.some((e) => e.includes('"policyReferences"')));
  });

  it('rejects an unknown expectedDecision', () => {
    const errors = validateCase(baseCase({ expectedDecision: 'maybe' }), { root });
    assert.ok(errors.some((e) => e.includes('"expectedDecision"')));
  });

  it('accepts every documented decision value', () => {
    for (const decision of DECISIONS) {
      const overrides = { expectedDecision: decision };
      if (decision === 'proceed') {
        overrides.forbiddenOutcomes = [];
      }
      assert.deepEqual(validateCase(baseCase(overrides), { root }), []);
    }
  });

  it('requires forbiddenOutcomes to be non-empty unless the decision is proceed', () => {
    const errors = validateCase(baseCase({ expectedDecision: 'reject', forbiddenOutcomes: [] }), { root });
    assert.ok(errors.some((e) => e.includes('"forbiddenOutcomes"')));

    const okErrors = validateCase(baseCase({ expectedDecision: 'proceed', forbiddenOutcomes: [] }), { root });
    assert.deepEqual(okErrors, []);
  });

  it('rejects an empty assertions array', () => {
    const errors = validateCase(baseCase({ assertions: [] }), { root });
    assert.ok(errors.some((e) => e.includes('"assertions"')));
  });

  it('rejects an assertion with an unknown type', () => {
    const errors = validateCase(baseCase({ assertions: [{ type: 'guessDecision' }] }), { root });
    assert.ok(errors.some((e) => e.includes('assertions[0]')));
  });

  it('rejects a repoFileContains assertion missing file/text', () => {
    const errors = validateCase(baseCase({ assertions: [{ type: 'repoFileContains' }] }), { root });
    assert.ok(errors.some((e) => e.includes('"file"')));
    assert.ok(errors.some((e) => e.includes('"text"')));
  });

  it('rejects a repoFileContains assertion referencing a missing file', () => {
    const errors = validateCase(
      baseCase({ assertions: [{ type: 'repoFileContains', file: 'does-not-exist.md', text: 'x' }] }),
      { root },
    );
    assert.ok(errors.some((e) => e.includes('does not exist')));
  });

  it('accepts a lone requiresModelEvaluation assertion', () => {
    const errors = validateCase(
      baseCase({ expectedDecision: 'stop', assertions: [{ type: 'requiresModelEvaluation' }] }),
      { root },
    );
    assert.deepEqual(errors, []);
  });

  it('rejects requiresModelEvaluation combined with another assertion', () => {
    const errors = validateCase(
      baseCase({
        assertions: [
          { type: 'requiresModelEvaluation' },
          { type: 'repoFileContains', file: 'policy.md', text: 'x' },
        ],
      }),
      { root },
    );
    assert.ok(errors.some((e) => e.includes('must be the only assertion')));
  });
});

// --- validateCorpus -----------------------------------------------------------------------

function fullCorpus() {
  const cases = [];
  let counter = 1;
  for (const category of REQUIRED_CATEGORIES) {
    for (const decision of ['proceed', 'reject']) {
      cases.push(
        baseCase({
          id: `CAT-${String(counter).padStart(3, '0')}`,
          category,
          expectedDecision: decision,
          forbiddenOutcomes: decision === 'proceed' ? [] : ['Something forbidden.'],
          source: `cat-${counter}.json`,
        }),
      );
      counter += 1;
    }
  }
  return cases;
}

describe('validateCorpus', () => {
  it('accepts a corpus covering every required category with mixed decisions', () => {
    assert.deepEqual(validateCorpus(fullCorpus()), []);
  });

  it('rejects a corpus smaller than the minimum size', () => {
    const errors = validateCorpus([{ ...baseCase(), source: 'a.json' }]);
    assert.ok(errors.some((e) => e.includes(`at least ${MIN_CORPUS_SIZE}`)));
  });

  it('rejects duplicate case ids', () => {
    const cases = fullCorpus();
    cases.push({ ...cases[0], source: 'duplicate.json' });
    const errors = validateCorpus(cases);
    assert.ok(errors.some((e) => e.includes(`Duplicate case id "${cases[0].id}"`)));
  });

  it('rejects a corpus missing a required category', () => {
    const cases = fullCorpus().filter((c) => c.category !== 'nayax');
    const errors = validateCorpus(cases);
    assert.ok(errors.some((e) => e.includes('"nayax"')));
  });

  it('rejects a corpus where every case shares one expectedDecision', () => {
    const cases = fullCorpus().map((c) => ({ ...c, expectedDecision: 'proceed', forbiddenOutcomes: [] }));
    const errors = validateCorpus(cases);
    assert.ok(errors.some((e) => e.includes('more than one expectedDecision')));
  });
});

// --- loadCorpus -----------------------------------------------------------------------------

describe('loadCorpus', () => {
  it('parses every JSON file and attaches its source filename', () => {
    const dir = mkdtempSync(join(tmpdir(), 'agent-evals-corpus-'));
    try {
      writeFileSync(join(dir, 'a.json'), JSON.stringify(baseCase({ id: 'AAA-001' })));
      writeFileSync(join(dir, 'b.json'), JSON.stringify(baseCase({ id: 'AAA-002' })));
      writeFileSync(join(dir, 'ignore.txt'), 'not json');
      const { cases, malformed } = loadCorpus(dir);
      assert.deepEqual(malformed, []);
      assert.equal(cases.length, 2);
      assert.deepEqual(
        cases.map((c) => c.source),
        ['a.json', 'b.json'],
      );
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it('reports malformed JSON instead of throwing', () => {
    const dir = mkdtempSync(join(tmpdir(), 'agent-evals-corpus-'));
    try {
      writeFileSync(join(dir, 'broken.json'), '{ not valid json');
      const { cases, malformed } = loadCorpus(dir);
      assert.equal(cases.length, 0);
      assert.equal(malformed.length, 1);
      assert.equal(malformed[0].file, 'broken.json');
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

// --- evaluateCase / buildReport -------------------------------------------------------------

describe('evaluateCase', () => {
  it('passes when the referenced text is present', () => {
    const result = evaluateCase(baseCase(), { root });
    assert.equal(result.status, 'PASS');
    assert.deepEqual(result.failures, []);
  });

  it('fails repoFileContains when the text is missing', () => {
    const result = evaluateCase(
      baseCase({ assertions: [{ type: 'repoFileContains', file: 'policy.md', text: 'not present anywhere' }] }),
      { root },
    );
    assert.equal(result.status, 'FAIL');
    assert.equal(result.failures.length, 1);
  });

  it('fails repoFileNotContains when the forbidden text is present', () => {
    const result = evaluateCase(
      baseCase({ assertions: [{ type: 'repoFileNotContains', file: 'policy.md', text: 'guarded phrase' }] }),
      { root },
    );
    assert.equal(result.status, 'FAIL');
  });

  it('passes repoFileNotContains when the forbidden text is absent', () => {
    const result = evaluateCase(
      baseCase({ assertions: [{ type: 'repoFileNotContains', file: 'policy.md', text: 'nowhere to be found' }] }),
      { root },
    );
    assert.equal(result.status, 'PASS');
  });

  it('marks a requiresModelEvaluation case as SKIPPED', () => {
    const result = evaluateCase(
      baseCase({ expectedDecision: 'stop', assertions: [{ type: 'requiresModelEvaluation' }] }),
      { root },
    );
    assert.equal(result.status, 'SKIPPED');
  });
});

describe('buildReport', () => {
  it('computes category pass rates, critical failures and skip counts, excluding skips from the rate', () => {
    const passing = baseCase({ id: 'CAT-001', category: 'nayax', critical: true, source: 'p.json' });
    const failing = baseCase({
      id: 'CAT-002',
      category: 'nayax',
      critical: true,
      assertions: [{ type: 'repoFileContains', file: 'policy.md', text: 'missing text' }],
      source: 'f.json',
    });
    const skipped = baseCase({
      id: 'CAT-003',
      category: 'agent-authority',
      expectedDecision: 'stop',
      assertions: [{ type: 'requiresModelEvaluation' }],
      source: 's.json',
    });

    const report = buildReport([passing, failing, skipped], { root });

    assert.equal(report.corpus.totalCases, 3);
    assert.equal(report.summary.passed, 1);
    assert.equal(report.summary.failed, 1);
    assert.equal(report.summary.skipped, 1);
    assert.equal(report.summary.passRateByCategory.nayax, 0.5);
    assert.deepEqual(report.summary.criticalFailures, ['CAT-002']);
    assert.deepEqual(report.summary.modelEvaluationRequired, ['CAT-003']);
    assert.equal(report.ok, false);
  });

  it('is ok when nothing fails, even with a non-critical mix', () => {
    const passing = baseCase({ id: 'CAT-001', critical: false, source: 'p.json' });
    const report = buildReport([passing], { root });
    assert.equal(report.ok, true);
    assert.equal(report.summary.criticalFailures.length, 0);
  });
});

// --- buildModelEvalPrompt -------------------------------------------------------------------

describe('buildModelEvalPrompt', () => {
  it('includes the scenario and policy references but never the grading rubric fields', () => {
    const caseObj = baseCase({
      scenario: 'UNIQUE_SCENARIO_TEXT',
      policyReferences: ['UNIQUE_POLICY_REF'],
      expectedBehavior: 'UNIQUE_EXPECTED_BEHAVIOUR',
      forbiddenOutcomes: ['UNIQUE_FORBIDDEN_OUTCOME'],
      expectedDecision: 'reject',
    });
    const prompt = buildModelEvalPrompt(caseObj);
    assert.ok(prompt.includes('UNIQUE_SCENARIO_TEXT'));
    assert.ok(prompt.includes('UNIQUE_POLICY_REF'));
    assert.ok(!prompt.includes('UNIQUE_EXPECTED_BEHAVIOUR'));
    assert.ok(!prompt.includes('UNIQUE_FORBIDDEN_OUTCOME'));
    // The prompt legitimately lists all three answer words as the expected response format;
    // it must not single out this case's actual expectedDecision anywhere else.
    assert.equal(prompt.match(/reject/g)?.length ?? 0, 1);
  });
});

// --- main (CLI integration) -----------------------------------------------------------------

describe('main', () => {
  it('exits 1 and reports malformed JSON without evaluating anything', () => {
    const dir = mkdtempSync(join(tmpdir(), 'agent-evals-main-'));
    try {
      writeFileSync(join(dir, 'broken.json'), '{ nope');
      const originalError = console.error;
      const originalLog = console.log;
      const logged = [];
      console.error = (...args) => logged.push(args.join(' '));
      console.log = (...args) => logged.push(args.join(' '));
      let code;
      try {
        code = main(['--cases-dir', dir]);
      } finally {
        console.error = originalError;
        console.log = originalLog;
      }
      assert.equal(code, 1);
      assert.ok(logged.some((line) => line.includes('invalid JSON')));
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it('exits 0 for a fully valid, passing corpus', () => {
    const dir = mkdtempSync(join(tmpdir(), 'agent-evals-main-'));
    try {
      const cases = fullCorpus();
      for (const c of cases) {
        const { source, ...body } = c;
        writeFileSync(join(dir, source), JSON.stringify(body));
      }
      const originalLog = console.log;
      console.log = () => {};
      let code;
      try {
        code = main(['--cases-dir', dir, '--root', root, '--json']);
      } finally {
        console.log = originalLog;
      }
      assert.equal(code, 0);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it('rejects an unknown --mode', () => {
    const originalError = console.error;
    console.error = () => {};
    let code;
    try {
      code = main(['--mode', 'nonsense']);
    } finally {
      console.error = originalError;
    }
    assert.equal(code, 2);
  });
});
