// Deterministic tests for the documentation-impact declaration parser.
// Run with: node --test scripts/validate-documentation-impact.test.mjs
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, it } from 'node:test';

import {
  ISSUE_DECISIONS,
  ISSUE_DECISION_NOT_REQUIRED,
  ISSUE_DECISION_REQUIRED,
  PR_DECISION_NOT_REQUIRED,
  PR_DECISION_UPDATED,
  assessEvidence,
  findSections,
  main,
  validateIssueBody,
  validatePullRequestBody,
} from './validate-documentation-impact.mjs';
import { pullRequestTemplatePath, readRepositoryFile } from './validate-agent-workflows.mjs';

// --- Fixtures ---------------------------------------------------------------------------

const UPDATED_EVIDENCE = [
  '- AGENTS.md — added the documentation-impact gate to the definition of done',
  '- docs/automation.md — documented the preflight job and the validation step',
].join('\n');

const NOT_REQUIRED_EVIDENCE =
  'The change adds a unit test for GST rounding in NayaxFeeCalculatorTests and touches no runtime code, ' +
  'so no documented behaviour, contract, configuration, deployment or user workflow changes.';

const REQUIRED_DETAILS = [
  '- docs/architecture.md § Reporting — describe the new use case and adapter',
  '- AGENTS.md § Reporting dates and filters — record the new filter rule',
].join('\n');

const NOT_REQUIRED_DETAILS =
  'Not required: this task only renames the private helper BuildRows inside EfDailyReportFactsProvider ' +
  'and keeps every route, response shape and report total identical.';

function issueBody({ decision = ISSUE_DECISION_REQUIRED, details = REQUIRED_DETAILS, level = '###' } = {}) {
  return [
    `${level} Business outcome`,
    '',
    'Operators can see unmatched periods.',
    '',
    `${level} Documentation impact decision`,
    '',
    decision,
    '',
    `${level} Documentation impact details`,
    '',
    details,
    '',
    `${level} Readiness`,
    '',
    '- [x] The acceptance criteria are testable.',
    '',
  ].join('\n');
}

function pullRequestBody({ decision = PR_DECISION_UPDATED, evidence = UPDATED_EVIDENCE, section } = {}) {
  const documentationSection =
    section ??
    ['## Documentation impact', '', '<!-- guidance comment from the template -->', '', `Decision: ${decision}`, '', `Evidence: ${evidence}`].join('\n');
  return [
    '## Summary and reason',
    '',
    'Adds the gate.',
    '',
    '## Impact',
    '',
    '| Area | Impact |',
    '| --- | --- |',
    '| Deployment | Not applicable |',
    '',
    documentationSection,
    '',
    '## Known limitations and follow-up work',
    '',
    'None.',
    '',
  ].join('\n');
}

function assertInvalid(result, ...patterns) {
  assert.equal(result.valid, false, `expected invalid, got ${JSON.stringify(result)}`);
  for (const pattern of patterns) {
    assert.ok(result.errors.some((error) => pattern.test(error)), `expected an error matching ${pattern}; got ${JSON.stringify(result.errors)}`);
  }
}

// --- Issue body -------------------------------------------------------------------------

describe('issue body: valid declarations', () => {
  it('accepts "Documentation changes required" with listed documentation files', () => {
    const result = validateIssueBody(issueBody());
    assert.deepEqual(result, { valid: true, decision: ISSUE_DECISION_REQUIRED, errors: [] });
  });

  it('accepts "No documentation changes required" with a specific explanation', () => {
    const result = validateIssueBody(issueBody({ decision: ISSUE_DECISION_NOT_REQUIRED, details: NOT_REQUIRED_DETAILS }));
    assert.deepEqual(result, { valid: true, decision: ISSUE_DECISION_NOT_REQUIRED, errors: [] });
  });

  it('accepts the hand-written H2 heading style used by existing issues as well as the H3 form rendering', () => {
    for (const level of ['##', '###']) {
      assert.equal(validateIssueBody(issueBody({ level })).valid, true, level);
    }
  });

  it('tolerates CRLF line endings and HTML comments', () => {
    const body = issueBody().replaceAll('\n', '\r\n').replace('### Readiness', '<!-- comment -->\r\n### Readiness');
    assert.equal(validateIssueBody(body).valid, true);
  });
});

describe('issue body: missing and duplicate sections', () => {
  it('rejects a body without the documentation sections', () => {
    const result = validateIssueBody('### Business outcome\n\nSomething.\n');
    assertInvalid(result, /Missing required section "Documentation impact decision"/, /Missing required section "Documentation impact details"/);
  });

  it('rejects an empty body', () => {
    assertInvalid(validateIssueBody(''), /Missing required section/);
    assertInvalid(validateIssueBody(undefined), /Missing required section/);
  });

  it('rejects a duplicated decision section', () => {
    const body = issueBody() + '\n### Documentation impact decision\n\nDocumentation changes required\n';
    assertInvalid(validateIssueBody(body), /"Documentation impact decision" appears 2 times/);
  });

  it('rejects a duplicated details section', () => {
    const body = issueBody() + '\n### Documentation impact details\n\n- docs/architecture.md — again\n';
    assertInvalid(validateIssueBody(body), /"Documentation impact details" appears 2 times/);
  });

  it('does not treat a heading inside a fenced code block as a section', () => {
    const body = issueBody() + '\n```text\n### Documentation impact decision\n```\n';
    assert.equal(validateIssueBody(body).valid, true);
  });
});

describe('issue body: decisions', () => {
  it('rejects an unsupported decision', () => {
    assertInvalid(validateIssueBody(issueBody({ decision: 'Maybe' })), /"Maybe", which is not supported/);
  });

  it('rejects a decision that differs from the option only by case or punctuation', () => {
    assertInvalid(validateIssueBody(issueBody({ decision: 'documentation changes required.' })), /not supported/);
  });

  it('rejects an empty decision and the "_No response_" rendering of an empty form field', () => {
    assertInvalid(validateIssueBody(issueBody({ decision: '' })), /"Documentation impact decision" is empty/);
    assertInvalid(validateIssueBody(issueBody({ decision: '_No response_' })), /"Documentation impact decision" is empty/);
  });

  it('rejects two decisions in one section', () => {
    assertInvalid(validateIssueBody(issueBody({ decision: ISSUE_DECISIONS.join('\n') })), /must contain exactly one decision/);
  });
});

describe('issue body: details', () => {
  it('rejects empty details', () => {
    assertInvalid(validateIssueBody(issueBody({ details: '' })), /"Documentation impact details" is empty/);
    assertInvalid(validateIssueBody(issueBody({ details: '_No response_' })), /"Documentation impact details" is empty/);
  });

  it('rejects bare None, N/A and Not applicable for either decision', () => {
    for (const decision of ISSUE_DECISIONS) {
      for (const details of ['None', 'none.', 'N/A', 'n/a', 'Not applicable', 'Not applicable.', 'NONE']) {
        assertInvalid(validateIssueBody(issueBody({ decision, details })), /bare or generic answer/);
      }
    }
  });

  it('rejects generic answers that restate the decision or the categories', () => {
    for (const details of [
      'No documentation changes required',
      'No documentation changes required.',
      'Docs updated',
      'Not needed',
      'No behaviour, contract, architecture, configuration, automation, deployment, operations or user workflow changes.',
      'Not required because this is an internal implementation fix with no change to behaviour, contracts, architecture, configuration, operations, or user workflow.',
    ]) {
      assertInvalid(validateIssueBody(issueBody({ decision: ISSUE_DECISION_NOT_REQUIRED, details })), /bare or generic answer|is generic|too short/);
    }
  });

  it('requires listed documentation files when documentation changes are required', () => {
    assertInvalid(
      validateIssueBody(issueBody({ details: 'Update the documentation to describe the new reporting behaviour in detail.' })),
      /must name at least one documentation file/,
    );
    assertInvalid(validateIssueBody(issueBody({ details: 'docs/architecture.md' })), /must say what changed/);
  });

  it('rejects unreplaced template placeholders', () => {
    assertInvalid(validateIssueBody(issueBody({ details: '- <file> — <what changed>' })), /template placeholder/);
    assertInvalid(validateIssueBody(issueBody({ details: '<!-- describe the impact' })), /template placeholder/);
  });

  it('still reports placeholder and bare details when the decision itself is invalid', () => {
    assertInvalid(validateIssueBody(issueBody({ decision: 'Unknown', details: 'None' })), /not supported/, /bare or generic answer/);
    assertInvalid(validateIssueBody(issueBody({ decision: 'Unknown', details: '' })), /not supported/, /is empty/);
  });
});

// --- Pull request body ------------------------------------------------------------------

describe('pull request body: valid declarations', () => {
  it('accepts Decision: UPDATED with listed documentation files and what changed', () => {
    const result = validatePullRequestBody(pullRequestBody());
    assert.deepEqual(result, { valid: true, decision: PR_DECISION_UPDATED, errors: [] });
  });

  it('accepts Decision: NOT REQUIRED with a specific explanation', () => {
    const result = validatePullRequestBody(pullRequestBody({ decision: PR_DECISION_NOT_REQUIRED, evidence: NOT_REQUIRED_EVIDENCE }));
    assert.deepEqual(result, { valid: true, decision: PR_DECISION_NOT_REQUIRED, errors: [] });
  });

  it('accepts evidence that starts on the lines after "Evidence:"', () => {
    const section = ['## Documentation impact', '', 'Decision: UPDATED', '', 'Evidence:', UPDATED_EVIDENCE].join('\n');
    assert.equal(validatePullRequestBody(pullRequestBody({ section })).valid, true);
  });

  it('accepts bold field names and CRLF line endings', () => {
    const section = ['## Documentation impact', '', '**Decision:** NOT REQUIRED', '', `**Evidence**: ${NOT_REQUIRED_EVIDENCE}`].join('\r\n');
    assert.equal(validatePullRequestBody(pullRequestBody({ section })).valid, true);
  });
});

describe('pull request body: missing and duplicate sections and fields', () => {
  it('rejects a body without the Documentation impact section', () => {
    const body = pullRequestBody().replace('## Documentation impact', '## Something else');
    assertInvalid(validatePullRequestBody(body), /Missing required section "Documentation impact"/);
  });

  it('rejects an empty body', () => {
    assertInvalid(validatePullRequestBody(''), /Missing required section/);
    assertInvalid(validatePullRequestBody(null), /Missing required section/);
  });

  it('rejects a duplicated Documentation impact section', () => {
    const body = pullRequestBody() + '\n## Documentation impact\n\nDecision: UPDATED\n\nEvidence: AGENTS.md — documented the gate again\n';
    assertInvalid(validatePullRequestBody(body), /"Documentation impact" appears 2 times/);
  });

  it('rejects a missing Decision line', () => {
    const section = ['## Documentation impact', '', `Evidence: ${UPDATED_EVIDENCE}`].join('\n');
    assertInvalid(validatePullRequestBody(pullRequestBody({ section })), /Missing "Decision:" line/);
  });

  it('rejects a missing Evidence line', () => {
    const section = ['## Documentation impact', '', 'Decision: UPDATED', '', UPDATED_EVIDENCE].join('\n');
    assertInvalid(validatePullRequestBody(pullRequestBody({ section })), /Missing "Evidence:" line/);
  });

  it('rejects duplicate Decision and Evidence lines', () => {
    const section = ['## Documentation impact', '', 'Decision: UPDATED', 'Decision: NOT REQUIRED', '', `Evidence: ${UPDATED_EVIDENCE}`, `Evidence: ${UPDATED_EVIDENCE}`].join('\n');
    assertInvalid(validatePullRequestBody(pullRequestBody({ section })), /"Decision:" appears 2 times/, /"Evidence:" appears 2 times/);
  });

  it('only considers Decision and Evidence lines inside the Documentation impact section', () => {
    const body = pullRequestBody().replace('## Known limitations and follow-up work\n', '## Known limitations and follow-up work\n\nDecision: pending\n');
    assert.equal(validatePullRequestBody(body).valid, true);
  });
});

describe('pull request body: decisions', () => {
  it('rejects anything other than exactly UPDATED or NOT REQUIRED', () => {
    for (const decision of ['Updated', 'updated', 'NOT-REQUIRED', 'Not required', 'NOT REQUIRED.', 'REQUIRED', 'N/A', 'UPDATED / NOT REQUIRED']) {
      assertInvalid(validatePullRequestBody(pullRequestBody({ decision, evidence: UPDATED_EVIDENCE })), /is not supported\. Use exactly "UPDATED" or "NOT REQUIRED"/);
    }
  });

  it('rejects an empty decision', () => {
    assertInvalid(validatePullRequestBody(pullRequestBody({ decision: '' })), /"Decision:" is empty/);
  });
});

describe('pull request body: evidence', () => {
  it('rejects empty evidence for both decisions', () => {
    for (const decision of [PR_DECISION_UPDATED, PR_DECISION_NOT_REQUIRED]) {
      assertInvalid(validatePullRequestBody(pullRequestBody({ decision, evidence: '' })), /"Evidence" is empty/);
    }
  });

  it('rejects bare None, N/A and Not applicable', () => {
    for (const decision of [PR_DECISION_UPDATED, PR_DECISION_NOT_REQUIRED]) {
      for (const evidence of ['None', 'N/A', 'Not applicable', 'not applicable.']) {
        assertInvalid(validatePullRequestBody(pullRequestBody({ decision, evidence })), /bare or generic answer/);
      }
    }
  });

  it('rejects the HTML template placeholders and unreplaced placeholder tokens', () => {
    const template = readRepositoryFile(pullRequestTemplatePath);
    assertInvalid(validatePullRequestBody(template), /"Decision: <UPDATED or NOT REQUIRED>" is not supported/, /template placeholder text: <meaningful evidence>/);
    assertInvalid(validatePullRequestBody(pullRequestBody({ evidence: '<meaningful evidence>' })), /template placeholder/);
    assertInvalid(validatePullRequestBody(pullRequestBody({ evidence: '- <file>: <what changed>' })), /template placeholder/);
  });

  it('does not treat generic type parameters as placeholders', () => {
    const evidence = 'AGENTS.md — documented that IRepository<T> abstractions stay forbidden and why the narrow ports are preferred';
    assert.equal(validatePullRequestBody(pullRequestBody({ evidence })).valid, true);
  });

  it('rejects generic answers', () => {
    for (const evidence of [
      'No documentation changes required',
      'No documentation changes required.',
      'Documentation not affected',
      'No impact',
      'No behaviour, contract, architecture, configuration, automation, deployment, operations or user workflow changes.',
      'This change does not affect behaviour, contracts, architecture, configuration, automation, deployment, operations or user workflows.',
      'Internal refactor only; no public API changes.',
    ]) {
      assertInvalid(validatePullRequestBody(pullRequestBody({ decision: PR_DECISION_NOT_REQUIRED, evidence })), /bare or generic answer|is generic|too short/);
    }
    for (const evidence of ['Docs updated', 'Updated documentation', 'Updated the docs accordingly']) {
      assertInvalid(validatePullRequestBody(pullRequestBody({ decision: PR_DECISION_UPDATED, evidence })), /bare or generic answer|must name at least one documentation file/);
    }
  });

  it('requires UPDATED evidence to name documentation files and what changed', () => {
    assertInvalid(validatePullRequestBody(pullRequestBody({ evidence: 'Updated the reporting guide to describe the new filter semantics.' })), /must name at least one documentation file/);
    assertInvalid(validatePullRequestBody(pullRequestBody({ evidence: 'docs/automation.md, AGENTS.md' })), /must say what changed/);
  });

  it('requires NOT REQUIRED evidence to be a specific explanation rather than a short label', () => {
    assertInvalid(validatePullRequestBody(pullRequestBody({ decision: PR_DECISION_NOT_REQUIRED, evidence: 'Only tests changed.' })), /too short/);
  });

  it('never inspects the diff: identical evidence is judged the same regardless of which files changed', () => {
    const body = pullRequestBody({ decision: PR_DECISION_NOT_REQUIRED, evidence: NOT_REQUIRED_EVIDENCE });
    assert.deepEqual(validatePullRequestBody(body), validatePullRequestBody(body + '\n<!-- changed: docs/automation.md -->\n'));
  });
});

describe('assessEvidence', () => {
  it('distinguishes the two evidence kinds', () => {
    assert.deepEqual(assessEvidence(UPDATED_EVIDENCE, { kind: 'documentation-updated', field: 'X' }), []);
    assert.deepEqual(assessEvidence(NOT_REQUIRED_EVIDENCE, { kind: 'documentation-not-required', field: 'X' }), []);
    assert.ok(assessEvidence(NOT_REQUIRED_EVIDENCE, { kind: 'documentation-updated', field: 'X' }).length > 0);
  });
});

describe('findSections', () => {
  it('ends a section at the next heading of the same or higher level and matches labels case-insensitively', () => {
    const sections = findSections('## Documentation Impact\n\nDecision: UPDATED\n\n#### Sub\n\nmore\n\n## Next\n\nno', 'Documentation impact');
    assert.equal(sections.length, 1);
    assert.match(sections[0].content, /Decision: UPDATED/);
    assert.match(sections[0].content, /more/);
    assert.doesNotMatch(sections[0].content, /no$/);
  });
});

// --- Command line -----------------------------------------------------------------------

describe('command line', () => {
  function withBodyFile(content, callback) {
    const directory = mkdtempSync(join(tmpdir(), 'documentation-impact-'));
    try {
      const file = join(directory, 'body.md');
      writeFileSync(file, content);
      return callback(file);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  }

  function capture(callback) {
    const out = [];
    const err = [];
    const originalLog = console.log;
    const originalError = console.error;
    console.log = (...args) => out.push(args.join(' '));
    console.error = (...args) => err.push(args.join(' '));
    try {
      return { code: callback(), out, err };
    } finally {
      console.log = originalLog;
      console.error = originalError;
    }
  }

  it('exits 0 for a valid pull request body and 1 for an invalid one', () => {
    assert.equal(withBodyFile(pullRequestBody(), (file) => capture(() => main(['--pr-body', file])).code), 0);
    const invalid = withBodyFile(pullRequestBody({ evidence: 'None' }), (file) => capture(() => main(['--pr-body', file])));
    assert.equal(invalid.code, 1);
    assert.ok(invalid.err.some((line) => /bare or generic answer/.test(line)));
  });

  it('exits 0 for a valid issue body and 1 for an invalid one', () => {
    assert.equal(withBodyFile(issueBody(), (file) => capture(() => main(['--issue-body', file])).code), 0);
    assert.equal(withBodyFile('### Nothing\n', (file) => capture(() => main(['--issue-body', file])).code), 1);
  });

  it('exits 2 on usage errors and unreadable files', () => {
    assert.equal(capture(() => main([])).code, 2);
    assert.equal(capture(() => main(['--diff', 'x'])).code, 2);
    assert.equal(capture(() => main(['--pr-body', join(tmpdir(), 'does-not-exist-documentation-impact.md')])).code, 2);
  });
});
