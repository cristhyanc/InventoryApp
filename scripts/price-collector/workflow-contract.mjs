// Contract for the price collector workflow (#929, #1110): a manual test target, a main-only
// production target, and one daily run started from outside GitHub (an Azure Logic App at 05:00
// Sydney time, owner decision 8 November 2026) through the dispatch entry point with scheduled_run
// true, switched off behind an unprotected, credential-free gate job.
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
export const COLLECT_ENVIRONMENT = "    environment: ${{ needs.gate.outputs.target == 'production' && 'price-collector-production' || 'price-collector' }}\n";
export const COLLECT_CONDITION = "    if: needs.gate.outputs.proceed == 'true' && (github.ref == 'refs/heads/main' || github.ref == 'refs/heads/develop') && (needs.gate.outputs.target == 'test' || (needs.gate.outputs.target == 'production' && github.ref == 'refs/heads/main')) && (";
// GitHub's cron started the daily run hours late, so the daily start comes from Azure through the
// dispatch entry point. The only trigger is workflow_dispatch; scheduled_run marks the daily run.
export const SCHEDULE_TRIGGER = "on:\n  workflow_dispatch:\n    inputs:\n      scheduled_run:\n";
export const SCHEDULE_SWITCH = `            true)
              if [ "$SCHEDULE_ENABLED" != 'true' ]; then`;
export const MANUAL_CONFIRMATION = "        if: github.event_name == 'workflow_dispatch' && inputs.scheduled_run != true\n";
export const SCHEDULED_SETTINGS = "            TARGET=production SCHEDULE=due SAMPLE_SIZE=3 FULL_RUN=true INCLUDE_RETAILERS=true STOP_AFTER_MINUTES=''\n";
export const FINALISE_STEP = "      - name: Submit finished files and complete the issued run\n        if: always() && steps.prepare.outputs.run_issued != 'false'\n        timeout-minutes: 15";
export const GATE_PRODUCTION = `            production)
              if [ "$RUN_REF" != 'refs/heads/main' ]; then
                echo '::error::Only the reviewed main workflow may run against production.'
                exit 1
              fi
              if [ "$CONFIRM_PRODUCTION_API" != 'true' ] || [ "$CONFIRM_TEST_API" != 'false' ]; then`;
export function verifyCollectorWorkflow(text) {
  const problems = runnerPlacementViolations(text, workflowPath);
  const requireText = (part, reason) => { if (!text.includes(part)) problems.push(reason); };
  requireText('  workflow_dispatch:', 'The collector must have a manual entry point.');
  requireText(SCHEDULE_TRIGGER, 'The only start is the dispatch entry point, whose scheduled_run input marks the daily run.');
  if (/^ {2}schedule:|^ {4}- cron:/m.test(text)) problems.push('The daily run comes from the Azure schedule, not a GitHub cron.');
  if (/^ {2}(?:push|pull_request|pull_request_target|workflow_run|workflow_call|repository_dispatch):/m.test(text)) problems.push('Nothing but the dispatch entry point may start the collector.');
  requireText("          SCHEDULED_RUN: ${{ inputs.scheduled_run }}\n          SCHEDULE_ENABLED: ${{ vars.PRICE_COLLECTOR_SCHEDULE_ENABLED }}\n", 'The switch must read the scheduled_run input.');
  requireText("          SCHEDULED_RUN: ${{ inputs.scheduled_run }}\n          ENABLED: ${{ steps.switch.outputs.enabled }}\n", 'The run settings must read the scheduled_run input.');
  // A delayed scheduled run still does its work; the backend's due rule (#1108) makes repeats safe.
  if (/date\s+[+-]|\$\(date|TZ=/.test(text)) problems.push('No runtime check of the hour or date may discard a delayed scheduled run.');
  // The switch is checked in the gate job, which names no environment and holds no credential, so a
  // switched-off schedule never requests the protected environment or its reviewer's approval.
  requireText("          SCHEDULE_ENABLED: ${{ vars.PRICE_COLLECTOR_SCHEDULE_ENABLED }}\n", 'The schedule must be switched by its repository variable.');
  requireText(SCHEDULE_SWITCH, 'A scheduled run must stop unless PRICE_COLLECTOR_SCHEDULE_ENABLED is exactly true.');
  requireText("              if [ \"$RUN_REF\" != 'refs/heads/main' ]; then\n                echo '::error::A scheduled collector run must be on main.'", 'A scheduled run must be on main.');
  requireText(SCHEDULED_SETTINGS, 'A scheduled run is a full production run of the due work only.');
  requireText(MANUAL_CONFIRMATION, "The manual confirmation gate must apply to every manual run.");
  requireText("    needs: gate\n", 'The credentialed job must wait for the gate.');
  requireText('group: price-collector\n  cancel-in-progress: false', 'Collector runs must not overlap or cancel one another.');
  // Reviewed main or develop only (develop to test against the hosted testing API); a feature branch
  // never receives the collector's credentials, and the refusal runs before the credentialed job.
  // Production (owner decision, 1 November 2026) is main only, refused by the gate and again by the
  // credentialed job's condition; its environment adds a main-only branch rule and a reviewer.
  requireText(COLLECT_CONDITION, 'Only reviewed main or develop may receive credentials, and only main the production target.');
  requireText(GATE_PRODUCTION, 'The gate must refuse a production run from anything but main, or without its confirmation.');
  requireText("            *) echo '::error::Choose the test or production target.'; exit 1 ;;", 'The gate must refuse an unknown target.');
  requireText('            refs/heads/main|refs/heads/develop) ;;\n            *) echo', 'The gate must refuse every other ref.');
  if (/refs\/heads\/(?!(?:main|develop)\b)/.test(text)) problems.push('Only main and develop may be named as collector refs.');
  requireText(COLLECT_ENVIRONMENT, 'Each target needs its dedicated protected environment.');
  if ((text.match(/^ {4}environment:/gm) ?? []).length !== 1) problems.push('Only the collect job may name an environment.');
  requireText('      PRICE_COLLECTOR_TARGET: ${{ needs.gate.outputs.target }}\n', 'The scripts must check the chosen target.');
  requireText('          PRICE_COLLECTOR_SCHEDULE: ${{ needs.gate.outputs.schedule }}\n', 'The work call must be told which schedule was chosen.');
  // A due run with nothing due issues no run (#1108): nothing to search, nothing to finalise.
  requireText("        id: claude\n        if: steps.prepare.outputs.run_issued == 'true'\n", 'Claude must not start when no run was issued.');
  requireText('      PRICE_COLLECTOR_TEST_API_URL: ${{ vars.PRICE_COLLECTOR_TEST_API_URL }}\n', 'The test target requires the dedicated test API setting.');
  requireText('      PRICE_COLLECTOR_PRODUCTION_API_URL: ${{ vars.PRICE_COLLECTOR_PRODUCTION_API_URL }}\n', 'The production target requires its own API setting.');
  requireText('          persist-credentials: false', 'Checkout must not leave a GitHub credential.');
  const jobs = workflowJobs(text, workflowPath);
  const collect = jobs.collect ?? '';
  const gate = jobs.gate ?? '';
  if (!gate.includes('    permissions: {}\n') || /^\s*environment:|secrets\.|^\s*id-token:/m.test(gate.replace(/^\s*#.*$/gm, ''))) problems.push('The gate job must name no environment and hold no secret or OIDC permission.');
  if (!collect.includes('    permissions:\n      contents: read\n      id-token: write\n')) problems.push('Collector permissions must be read-only plus OIDC.');
  if (/(?:contents|actions|issues|pull-requests|deployments): write|permissions: write-all/.test(text)) problems.push('The collector cannot write GitHub resources.');
  if (/azure\/login|webapps-deploy|--dangerously-skip-permissions|PRICE_COLLECTOR_API_TOKEN:/.test(text)) problems.push('No deployments, permission bypass or stored API token.');
  // The one artifact is the run's masked log folder (owner decision, 31 October 2026), pinned and
  // kept for 7 days. It must be uploaded before the cleanup step removes the state directory.
  const artifact = text.match(/uses: actions\/upload-artifact@[^\n]*/g) ?? [];
  if (artifact.length > 1 || (artifact.length === 1 && artifact[0] !== `uses: ${UPLOAD_ARTIFACT}`)) problems.push('Only the pinned run-log artifact may be uploaded.');
  requireText(RUN_LOG_STEP, 'The run log must be uploaded, masked, for 7 days, after failure too.');
  if (text.indexOf(RUN_LOG_STEP) > text.indexOf('      - name: Remove private work, files and CLI installation')) problems.push('The run log must be uploaded before cleanup.');
  requireText(FINALISE_STEP, 'Partial-result finalisation must run after failure with its own time budget, whenever a run was issued.');
  requireText('      - name: Remove private work, files and CLI installation\n        if: always()', 'Private files must be cleaned after failure too.');
  if (!collect.includes('    timeout-minutes: 160\n') || !collect.includes('        timeout-minutes: 130\n')) problems.push('The job must leave at least 15 minutes after Claude stops.');
  if (problems.length) throw new Error(problems.join('\n'));
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  verifyCollectorWorkflow(readFileSync(workflowPath, 'utf8'));
  console.log('Collector workflow contract passed.');
}
