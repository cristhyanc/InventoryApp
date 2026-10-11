// Tests for the fixed outcome table (scripts/agent-outcomes.mjs) that every agent review and
// validation status uses. Run with: node --test scripts/agent-outcomes.test.mjs
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { describe, it } from 'node:test';
import { OUTCOMES, REVIEW_CONTEXT, VALIDATION_CONTEXTS, classify } from './agent-outcomes.mjs';

const script = new URL('./agent-outcomes.mjs', import.meta.url).pathname;

describe('agent outcome table', () => {
  it('has the six review outcomes and three validation outcomes the spec names', () => {
    assert.deepEqual(Object.keys(OUTCOMES).filter((name) => OUTCOMES[name].contexts.includes(REVIEW_CONTEXT)).sort(),
      ['changes-requested', 'not-eligible', 'provider-failed', 'publication-failed', 'ready', 'superseded']);
    assert.deepEqual(Object.keys(OUTCOMES).filter((name) => OUTCOMES[name].contexts === VALIDATION_CONTEXTS).sort(),
      ['validation-cancelled', 'validation-failed', 'validation-passed']);
  });

  it('uses a distinct prefix for every outcome, so none can be read as another', () => {
    const prefixes = Object.values(OUTCOMES).map((outcome) => outcome.prefix);
    assert.equal(new Set(prefixes).size, prefixes.length);
    for (const a of prefixes) for (const b of prefixes) if (a !== b) assert.ok(!a.startsWith(`${b}: `), `${a} must not start with ${b}`);
  });

  it('keeps only a real review result out of the error state', () => {
    assert.equal(OUTCOMES.ready.state, 'success');
    assert.equal(OUTCOMES['changes-requested'].state, 'failure');
    for (const name of ['superseded', 'provider-failed', 'publication-failed', 'not-eligible']) assert.equal(OUTCOMES[name].state, 'error', name);
    assert.equal(OUTCOMES['validation-passed'].state, 'success');
    // A cancelled validation still blocks the merge: only a passed validation is success.
    assert.equal(OUTCOMES['validation-failed'].state, 'failure');
    assert.equal(OUTCOMES['validation-cancelled'].state, 'failure');
  });

  it('is frozen, so no caller can redefine an outcome', () => {
    assert.ok(Object.isFrozen(OUTCOMES));
    for (const outcome of Object.values(OUTCOMES)) assert.ok(Object.isFrozen(outcome));
    assert.ok(Object.isFrozen(VALIDATION_CONTEXTS));
  });

  it('classifies a status by context, state and description prefix together', () => {
    assert.equal(classify(REVIEW_CONTEXT, 'success', 'Ready for human review: advisory'), 'ready');
    assert.equal(classify(REVIEW_CONTEXT, 'failure', 'Changes requested: see the review'), 'changes-requested');
    assert.equal(classify(REVIEW_CONTEXT, 'error', 'Superseded: stale: the head moved'), 'superseded');
    assert.equal(classify(REVIEW_CONTEXT, 'error', 'AI provider failed: the review job ended with failure'), 'provider-failed');
    assert.equal(classify(REVIEW_CONTEXT, 'error', 'Publication failed: missing'), 'publication-failed');
    assert.equal(classify(REVIEW_CONTEXT, 'error', 'Not eligible: draft'), 'not-eligible');
    for (const context of VALIDATION_CONTEXTS) {
      assert.equal(classify(context, 'success', 'Validation passed'), 'validation-passed');
      assert.equal(classify(context, 'failure', 'Validation failed: failure'), 'validation-failed');
      assert.equal(classify(context, 'failure', 'Validation cancelled: replaced'), 'validation-cancelled');
    }
  });

  it('never classifies a mismatched state, context or prefix', () => {
    // A "ready" description with an error state, or a validation description on the review context.
    assert.equal(classify(REVIEW_CONTEXT, 'error', 'Ready for human review: advisory'), null);
    assert.equal(classify(REVIEW_CONTEXT, 'success', 'Validation passed'), null);
    assert.equal(classify('agent-validation', 'success', 'Ready for human review'), null);
    assert.equal(classify('agent-validation', 'success', 'Validation passedx'), null);
    assert.equal(classify('agent-validation', 'failure', 'Validation did not pass (failure)'), null, 'the old wording is not an outcome');
    assert.equal(classify(REVIEW_CONTEXT, 'error', 'Superseded or not published: x'), null, 'the old wording is not an outcome');
    assert.equal(classify('other', 'success', 'Validation passed'), null);
    assert.equal(classify(REVIEW_CONTEXT, 'error', undefined), null);
  });

  it('lists and classifies from the command line', () => {
    const listed = spawnSync(process.execPath, [script, 'list'], { encoding: 'utf8' });
    assert.equal(listed.status, 0);
    assert.equal(listed.stdout.trim().split('\n').length, Object.keys(OUTCOMES).length);
    const classified = spawnSync(process.execPath, [script, 'classify', REVIEW_CONTEXT, 'error', 'Not', 'eligible:', 'draft'], { encoding: 'utf8' });
    assert.equal(classified.stdout, 'not-eligible\n');
    const unknown = spawnSync(process.execPath, [script, 'classify', REVIEW_CONTEXT, 'error', 'whatever'], { encoding: 'utf8' });
    assert.equal(unknown.stdout, 'unknown\n');
    assert.equal(spawnSync(process.execPath, [script], { encoding: 'utf8' }).status, 2);
  });
});
