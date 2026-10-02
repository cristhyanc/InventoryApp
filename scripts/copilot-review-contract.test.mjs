import { readFileSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import assert from 'node:assert/strict';

const workflow = readFileSync(new URL('../.github/workflows/agent-review.yml', import.meta.url), 'utf8');
const processing = workflow.slice(workflow.indexOf('          validate_copilot_output() {'), workflow.indexOf('\n          delimiter="REVIEW_'));
const sha = 'a'.repeat(40);
const valid = { reviewed_head_sha: sha, verdict: 'READY FOR HUMAN REVIEW', blockers: [], criteria: [{ criterion: 'Required behavior', status: 'met', evidence: 'Regression test passed' }], suggestions: [], validation_evidence: 'Exact-SHA validation passed', inline_comments: [] };
function run(initial, repaired) {
  const work = mkdtempSync(join(tmpdir(), 'review-contract-'));
  const marked = value => 'BEGIN_REVIEW_JSON\n' + JSON.stringify(value) + '\nEND_REVIEW_JSON\n';
  writeFileSync(join(work, 'copilot-output.md'), marked(initial));
  writeFileSync(join(work, 'repair.md'), marked(repaired));
  const setup = `
set -euo pipefail
fail() { echo "$1"; exit 1; }
git() { if [ "$1" = rev-parse ]; then echo "$HEAD_SHA"; fi; }
copilot() {
  [ "$*" = "-s --no-ask-user --deny-tool=* -p $repair_prompt" ] || exit 99
  echo retry >> "$work/retries"
  cat "$work/repair.md"
}
prompt="Contract"
`;
  const result = spawnSync('bash', ['-c', setup + processing], { encoding: 'utf8', env: { ...process.env, work, HEAD_SHA: sha } });
  const retries = (() => { try { return readFileSync(join(work, 'retries'), 'utf8').trim().split('\n').length; } catch { return 0; } })();
  rmSync(work, { recursive: true, force: true });
  return { ...result, retries };
}
test('valid review publishes without a formatting retry', () => { const r = run(valid, {}); assert.equal(r.status, 0, r.stderr); assert.equal(r.retries, 0); });
test('format-only repair accepts string evidence after array representation', () => { const r = run({ ...valid, validation_evidence: [valid.validation_evidence] }, valid); assert.equal(r.status, 0, r.stderr); assert.equal(r.retries, 1); });
for (const [name, invalid] of Object.entries({
  missingEvidence: { ...valid, validation_evidence: undefined },
  badEnum: { ...valid, verdict: 'APPROVED' },
  extraField: { ...valid, approved: true },
  invalidInlineLine: { ...valid, inline_comments: [{ path: 'file.cs', line: 0, body: 'finding' }] },
  emptyCriteria: { ...valid, criteria: [] },
})) test('fails closed after one retry: ' + name, () => { const r = run(invalid, invalid); assert.notEqual(r.status, 0); assert.equal(r.retries, 1); });
