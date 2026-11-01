// Contract for the deliberately manual, test-only first stage of #929.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runnerPlacementViolations, workflowJobs } from '../validate-runner-placement.mjs';

export const workflowPath = '.github/workflows/price-collector.yml';
const UPLOAD_ARTIFACT = 'actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4.6.2';
export const RUN_LOG_STEP = `      - name: Upload the run log
        if: always()
        uses: ${UPLOAD_ARTIFACT}
        with:
          name: price-collector-logs
          path: \${{ runner.temp }}/price-collector-\${{ github.run_id }}-\${{ github.run_attempt }}/logs
          retention-days: 7
          if-no-files-found: ignore
`;
export function verifyCollectorWorkflow(text) {
  const problems = runnerPlacementViolations(text, workflowPath);
  const requireText = (part, reason) => { if (!text.includes(part)) problems.push(reason); };
  requireText('  workflow_dispatch:', 'The collector must have a manual entry point.');
  if (/^ {2}(?:schedule|push|pull_request|pull_request_target|workflow_run|workflow_call):/m.test(text)) problems.push('Stage 1 must not start automatically.');
  requireText('group: price-collector\n  cancel-in-progress: false', 'Collector runs must not overlap or cancel one another.');
  // Reviewed main or develop only (develop to test against the hosted testing API); a feature branch
  // never receives the collector's credentials, and the refusal runs before the credentialed job.
  requireText("    if: (github.ref == 'refs/heads/main' || github.ref == 'refs/heads/develop') && (", 'Only reviewed main or develop may receive credentials.');
  requireText('            refs/heads/main|refs/heads/develop) ;;\n            *) echo', 'The gate must refuse every other ref.');
  if (/refs\/heads\/(?!(?:main|develop)\b)/.test(text)) problems.push('Only main and develop may be named as collector refs.');
  requireText('environment: price-collector', 'The collector needs its dedicated protected environment.');
  requireText('PRICE_COLLECTOR_TEST_API_URL:', 'Stage 1 requires the dedicated test API setting.');
  requireText('          persist-credentials: false', 'Checkout must not leave a GitHub credential.');
  const jobs = workflowJobs(text, workflowPath);
  const collect = jobs.collect ?? '';
  if (!collect.includes('    permissions:\n      contents: read\n      id-token: write\n')) problems.push('Collector permissions must be read-only plus OIDC.');
  if (/(?:contents|actions|issues|pull-requests|deployments): write|permissions: write-all/.test(text)) problems.push('The collector cannot write GitHub resources.');
  if (/azure\/login|webapps-deploy|--dangerously-skip-permissions|PRICE_COLLECTOR_API_TOKEN:/.test(text)) problems.push('No deployments, permission bypass or stored API token.');
  // The one artifact is the run's masked log folder (owner decision, 31 October 2026), pinned and
  // kept for 7 days. It must be uploaded before the cleanup step removes the state directory.
  const artifact = text.match(/uses: actions\/upload-artifact@[^\n]*/g) ?? [];
  if (artifact.length > 1 || (artifact.length === 1 && artifact[0] !== `uses: ${UPLOAD_ARTIFACT}`)) problems.push('Only the pinned run-log artifact may be uploaded.');
  requireText(RUN_LOG_STEP, 'The run log must be uploaded, masked, for 7 days, after failure too.');
  if (text.indexOf(RUN_LOG_STEP) > text.indexOf('      - name: Remove private work, files and CLI installation')) problems.push('The run log must be uploaded before cleanup.');
  requireText('      - name: Submit finished files and complete the issued run\n        if: always()\n        timeout-minutes: 15', 'Partial-result finalisation must run after failure with its own time budget.');
  requireText('      - name: Remove private work, files and CLI installation\n        if: always()', 'Private files must be cleaned after failure too.');
  if (!collect.includes('    timeout-minutes: 130\n') || !collect.includes('        timeout-minutes: 100\n')) problems.push('The job must leave at least 15 minutes after Claude stops.');
  if (problems.length) throw new Error(problems.join('\n'));
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  verifyCollectorWorkflow(readFileSync(workflowPath, 'utf8'));
  console.log('Manual test collector workflow contract passed.');
}
