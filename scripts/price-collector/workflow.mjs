// Manual test orchestration (#929). Only this trusted parent exchanges OIDC and submits results.
// Claude gets a small work snapshot and Apify access, never the API/OIDC/GitHub credentials.
import { spawn } from 'node:child_process';
import { appendFileSync, cpSync, existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { collectorToken } from './workflow-auth.mjs';
import { JB_METRO_CAPS, TOOL_LOG_FILE } from './workflow-tool-guard.mjs';
import { runGetWork } from './get-work.mjs';
import { runSubmission } from './submit-results.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const reviewedActors = new Set(['abotapi/kmart-au-scraper', 'axlymxp/bigw-product-scraper', 'dromb/big-w-au-product-scraper']);
const json = path => JSON.parse(readFileSync(path, 'utf8'));
const ISSUED_WORK = 'issued-work.json';
const save = (path, value) => writeFileSync(path, `${JSON.stringify(value)}\n`, { mode: 0o600 });

/**
 * A production-sized run (#929 stage 2 dry run): every issued search with the production page sizes
 * in apify-limits.json, worked through by one fresh Claude session per batch so no session's context
 * grows with the whole catalogue. Each batch keeps its own turn and dollar cap; the batch count caps
 * the run's Claude spend at maxBatches x maxBudgetUsdPerBatch, and the Apify actor call cap in
 * apify-limits.json (perRun.maxActorCalls) is shared by every batch because the guard's state is the
 * run's. Searches beyond the last batch, and batches the deadline does not reach, become not_searched.
 */
export const FULL_RUN = Object.freeze({
  batchSize: 6, maxBatches: 15, maxTurnsPerBatch: 60, maxBudgetUsdPerBatch: 1, deadlineMinutes: 90, batchMinutes: 14,
});
export const SAMPLE_RUN = Object.freeze({ maxTurns: 40, maxBudgetUsd: 1 });

/** The given searches of an issued run, with only the matching inputs those searches need. */
export function selectWork(work, searches) {
  const waitingKeys = new Set(searches.filter(s => s.product.kind === 'waiting').map(s => s.product.waitingKey));
  const waitingProducts = work.matching.waitingProducts.filter(p => waitingKeys.has(p.waitingKey));
  return { ...work, searches, searchCount: searches.length,
    matching: { ...work.matching, required: waitingProducts.length > 0, waitingProducts } };
}

export function sampleWork(work, count) {
  if (!Number.isInteger(count) || count < 1 || count > 10) throw new Error('Sample size must be a whole number from 1 to 10.');
  return selectWork(work, work.searches.slice(0, count));
}

const productKey = search => (search.product.kind === 'waiting' ? `waiting:${search.product.waitingKey}` : `master:${search.product.masterProductId}`);

/**
 * The work split into batches of about batchSize searches, in issue order, never splitting one
 * product's searches: its match file is written once, so a second session could not write it again.
 */
export function workBatches(work, batchSize = FULL_RUN.batchSize) {
  const groups = new Map();
  for (const search of work.searches) {
    const key = productKey(search);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(search);
  }
  const batches = [];
  let current = [];
  for (const group of groups.values()) {
    if (current.length > 0 && current.length + group.length > batchSize) { batches.push(current); current = []; }
    current.push(...group);
  }
  if (current.length > 0) batches.push(current);
  return batches.map(searches => selectWork(work, searches));
}

/** Everything the run may reach: the first maxBatches batches. The rest becomes not_searched. */
export function fullRunWork(work) {
  return selectWork(work, workBatches(work).slice(0, FULL_RUN.maxBatches).flatMap(batch => batch.searches));
}

/**
 * The production limits unchanged, except that the reviewed retailers may be enabled for this run,
 * plus JB Metro's full-run page caps (it is not an Apify actor, so apify-limits.json has none).
 */
export function fullRunLimits(source, includeReviewed) {
  const limits = structuredClone(source);
  limits.jbMetro = { ...JB_METRO_CAPS.full };
  for (const [id, actor] of Object.entries(limits.actors)) {
    if (includeReviewed && reviewedActors.has(id) && actor.schemaVerifiedAt === '2026-10-31') actor.actorIdConfirmed = true;
  }
  return limits;
}

export function sampleLimits(source, includeReviewed) {
  const limits = structuredClone(source);
  limits.perRun.maxActorCalls = Math.min(12, limits.perRun.maxActorCalls);
  limits.jbMetro = { ...JB_METRO_CAPS.sample };
  for (const [id, actor] of Object.entries(limits.actors)) {
    actor.pageSize = Math.min(5, actor.pageSize);
    actor.maxPages = 1;
    if (includeReviewed && reviewedActors.has(id) && actor.schemaVerifiedAt === '2026-10-31') actor.actorIdConfirmed = true;
  }
  return limits;
}

export function collectorEnvironment(env, home) {
  // An allowlist avoids inheriting OIDC endpoint credentials, API tokens, shell startup scripts,
  // Node injection flags or pre-existing Claude authentication/configuration from a runner.
  // PATH is fixed: the running Node's own directory plus the system directories, never the runner's
  // inherited PATH, so a writable directory on it cannot replace node or a system command.
  return {
    PATH: [dirname(process.execPath), '/usr/bin', '/bin'].join(':'), HOME: home, TMPDIR: home, CLAUDE_CONFIG_DIR: join(home, '.claude'),
    ANTHROPIC_API_KEY: env.ANTHROPIC_API_KEY, APIFY_TOKEN: env.APIFY_TOKEN,
    PRICE_COLLECTOR_STATE_DIR: env.PRICE_COLLECTOR_STATE_DIR,
    PRICE_COLLECTOR_OUT_DIR: env.PRICE_COLLECTOR_OUT_DIR,
    PRICE_COLLECTOR_GUARD_STATE: env.PRICE_COLLECTOR_GUARD_STATE,
    DISABLE_AUTOUPDATER: '1', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
  };
}

const reportPatterns = [
  /^files: results \d+, matches \d+, skipped \d+$/,
  /^matches: auto-matched \d+, tentative \d+, rejected \d+$/,
  /^searches: complete \d+, truncated \d+, no_matches \d+, failed \d+; answered \d+, left without an outcome \d+$/,
  /^rows: accepted \d+, sent to review \d+, pending \d+, rejected \d+$/,
  /^run \d+: [a-z_ ]+; complete \d+, truncated \d+, no_matches \d+, failed \d+, not_searched \d+$/,
  /^run \d+: not completed$/,
  /^failed requests: \d+$/,
];

/**
 * A skipped file as the summary may show it: the work item number and the field the validator
 * names, never its message, which can quote a supplier product id. A match file is not named,
 * because its name carries the waiting product's key.
 */
export function publicSkipLine(line) {
  const skipped = /^ {2}skipped (\S+): (.*)$/.exec(line);
  if (!skipped) return null;
  const file = /^[1-9]\d{0,9}\.json$/.test(skipped[1]) ? skipped[1] : 'a match file';
  const field = /^(file(?:\.[A-Za-z]{1,40}|\[\d{1,4}\]){0,6})(?= )/.exec(skipped[2]);
  const reason = field ? `field ${field[1]}` : 'a file-level rule';
  return `skipped ${file}: ${reason}`;
}

/**
 * The API's own fixed reasons for a result row (PriceCollectorResultPolicy in the Domain). They are
 * constants with no product, price or link in them, so the summary may show them word for word;
 * any other reason is counted as "other reason". workflow.test.mjs checks this list against the
 * Domain file, so a reason added there must be added here.
 */
export const PUBLIC_ROW_REASONS = Object.freeze([
  'unknown work item',
  'work item belongs to another run',
  "supplier differs from the work item's supplier",
  'listing belongs to another product',
  'the search has already been answered',
  'duplicate work item and supplier product id in this batch',
  'observed before the run started',
  'observed in the future',
  "source link is not on the supplier's own site",
  "price basis differs from the supplier's published basis",
  'listing was rejected',
  'variant, individual size, barcode or pack quantity changed',
  'price is more than 50% below or 100% above the last accepted price',
  'listing is not confirmed, so the reading is held as a pending observation',
]);
const rowReasonLabels = ['sent to review', 'pending', 'rejected'];

/**
 * The row reason lines as the summary may show them: an allowlisted reason with its count, and
 * every other reason of the same outcome folded into one "other reason" count.
 */
export function publicReasonLines(out) {
  const counts = new Map();
  for (const line of String(out).split('\n')) {
    const reasoned = /^ {2}(sent to review|pending|rejected): (.+) × ([1-9]\d{0,6})$/.exec(line);
    if (!reasoned) continue;
    const reason = PUBLIC_ROW_REASONS.includes(reasoned[2]) ? reasoned[2] : 'other reason';
    const key = `${reasoned[1]}: ${reason}`;
    counts.set(key, (counts.get(key) ?? 0) + Number(reasoned[3]));
  }
  return [...counts.entries()]
    .sort(([a], [b]) => rowReasonLabels.indexOf(a.split(':')[0]) - rowReasonLabels.indexOf(b.split(':')[0]))
    .map(([key, count]) => `  ${key} × ${count}`);
}

export function publicSubmissionReport(out) {
  const lines = String(out).split('\n')
    .map(line => (reportPatterns.some(pattern => pattern.test(line)) ? line : publicSkipLine(line)))
    .filter(line => line !== null);
  // The reasons sit under the rows line they explain.
  const rows = lines.findIndex(line => line.startsWith('rows: '));
  if (rows !== -1) lines.splice(rows + 1, 0, ...publicReasonLines(out));
  return lines.join('\n');
}

/** Only files issued to this sample reach the existing independent schema/batch validator. */
function submissionFolder(stateDir, outputDir, work) {
  const dir = join(stateDir, 'submission');
  mkdirSync(dir, { mode: 0o700 });
  let refused = 0;
  for (const name of existsSync(outputDir) ? readdirSync(outputDir) : []) {
    // Accounting is not an output file and should not be reported as a malformed result.
    if (name.startsWith('.')) continue;
    try {
      const path = join(outputDir, name);
      const stat = lstatSync(path);
      if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 2 * 1024 * 1024) throw new Error('Not a regular result file.');
      const file = json(path);
      const selected = /^[1-9]\d*\.json$/.test(name)
        ? name === `${file.workItemId}.json` && work.searches.some(s => s.workItemId === file.workItemId)
        : /^match-[a-zA-Z0-9_-]+\.json$/.test(name) && work.matching.waitingProducts.some(p => p.waitingKey === file.waitingKey);
      if (file.runId !== work.runId || !selected) throw new Error('Not a result for this sample.');
      cpSync(path, join(dir, name), { errorOnExist: true, force: false });
    } catch { refused++; }
  }
  return { dir, refused };
}

/**
 * How the hook answered each tool, as counts: one line per tool label with allowed and denied
 * calls and the fixed codes of the denials and after-call records. Labels and codes are checked
 * against plain patterns again here, so nothing else from the log can reach the public summary.
 */
export function toolUseReport(stateDir) {
  const path = join(stateDir, TOOL_LOG_FILE);
  if (!existsSync(path)) return 'Tool calls: none\n';
  const tools = new Map();
  for (const line of readFileSync(path, 'utf8').split('\n')) {
    let entry;
    try { entry = JSON.parse(line); } catch { continue; }
    const tool = /^[A-Za-z0-9_:-]{1,80}$/.test(entry?.tool) ? entry.tool : 'other';
    const code = /^[a-z-]{1,40}$/.test(entry?.code) ? entry.code : 'other';
    const counts = tools.get(tool) ?? { allowed: 0, denied: 0, codes: new Map() };
    if (entry?.event === 'before' && entry.decision === 'allow') counts.allowed += 1;
    else if (entry?.event === 'before') counts.denied += 1;
    if (code !== 'ok') counts.codes.set(code, (counts.codes.get(code) ?? 0) + 1);
    tools.set(tool, counts);
  }
  if (tools.size === 0) return 'Tool calls: none\n';
  return [...tools].sort(([a], [b]) => a.localeCompare(b)).map(([tool, counts]) => {
    const codes = [...counts.codes].map(([code, count]) => `${code} ${count}`).join(', ');
    return `Tool ${tool}: allowed ${counts.allowed}, denied ${counts.denied}${codes ? `; ${codes}` : ''}\n`;
  }).join('');
}

export async function finaliseSample({ stateDir, outputDir, env, token, submit = runSubmission }) {
  const work = json(join(stateDir, ISSUED_WORK));
  const status = existsSync(join(stateDir, 'collector-status.json')) ? json(join(stateDir, 'collector-status.json')) : { status: 'not_started', costUsd: null };
  const { dir, refused } = submissionFolder(stateDir, outputDir, work);
  const result = await submit({ argv: ['--dir', dir, '--run-id', String(work.runId)],
    env: { PRICE_COLLECTOR_API_BASE_URL: env.PRICE_COLLECTOR_TEST_API_URL, PRICE_COLLECTOR_API_TOKEN: token } });
  const allowedStatuses = ['completed', 'failed', 'timeout', 'interrupted', 'output_limit', 'claude_limit', 'apify_limit', 'not_started'];
  const label = allowedStatuses.includes(status.status) ? status.status : 'failed';
  const cost = typeof status.costUsd === 'number' && Number.isFinite(status.costUsd) && status.costUsd >= 0 ? status.costUsd.toFixed(6) : 'unknown';
  const turns = Number.isInteger(status.turns) ? status.turns : 'unknown';
  const batches = Number.isInteger(status.batchesRun) && Number.isInteger(status.batchCount)
    ? `Claude batches: ${status.batchesRun} of ${status.batchCount}\n` : '';
  const subtype = typeof status.subtype === 'string' && /^[a-z_]{1,40}$/.test(status.subtype) ? status.subtype : 'unknown';
  const report = `Collector status: ${label}\nClaude cost USD: ${cost}\nClaude turns: ${turns}; result: ${subtype}\n${batches}${toolUseReport(stateDir)}Searches given to Claude: ${work.searches.length}\nFiles refused before submission: ${refused}\n${publicSubmissionReport(result.out)}\nSubmission status: ${result.exitCode === 0 && refused === 0 ? 'completed' : 'failed'}\n`;
  save(join(stateDir, 'submission-status.json'), { ok: result.exitCode === 0 && refused === 0 });
  return report;
}

/** No raw CLI output or transcript is sent to stdout, stderr, Actions outputs or artifacts. */
export function launchClaude({ executable, sandbox, env, timeoutMs = 14 * 60 * 1000, maxTurns = SAMPLE_RUN.maxTurns, maxBudgetUsd = SAMPLE_RUN.maxBudgetUsd }) {
  // An absolute path to the job's own installation, never a `claude` looked up on PATH.
  if (typeof executable !== 'string' || !isAbsolute(executable)) {
    return Promise.reject(new Error('The collector needs the absolute path of the reviewed Claude CLI.'));
  }
  return new Promise(resolveResult => {
    const args = ['-p', '--output-format', 'json', '--no-session-persistence', '--disable-slash-commands',
      '--setting-sources', '', '--settings', 'scripts/price-collector/claude-settings.json',
      '--permission-mode', 'dontAsk', '--tools', 'Bash,Write', '--strict-mcp-config',
      '--mcp-config', 'scripts/price-collector/apify-mcp.json', '--max-turns', String(maxTurns), '--max-budget-usd', String(maxBudgetUsd),
      '--model', 'sonnet', '--system-prompt', readFileSync(join(sandbox, 'scripts/price-collector/prompt.md'), 'utf8')];
    const child = spawn(executable, args, { cwd: sandbox, env, detached: true, stdio: ['pipe', 'pipe', 'pipe'] });
    let text = '';
    let errorText = '';
    let stopped = null;
    let forceKill;
    const stop = reason => {
      if (stopped) return;
      stopped = reason;
      try { process.kill(-child.pid, 'SIGTERM'); } catch { /* Child may have already exited. */ }
      forceKill = setTimeout(() => { try { process.kill(-child.pid, 'SIGKILL'); } catch { /* Already exited. */ } }, 5000);
      forceKill.unref();
    };
    const timer = setTimeout(() => stop('timeout'), timeoutMs);
    // Cancelling a run sends SIGINT before SIGTERM. Claude runs in its own process group, so
    // both signals must stop that group, or it would keep calling Apify during finalisation.
    const interrupt = () => stop('interrupted');
    process.once('SIGINT', interrupt);
    process.once('SIGTERM', interrupt);
    const capture = (chunk, stderr) => {
      if (text.length + errorText.length + chunk.length > 2 * 1024 * 1024) { stop('output_limit'); return; }
      if (stderr) errorText += chunk.toString(); else text += chunk.toString();
    };
    child.stdout.on('data', chunk => capture(chunk, false));
    child.stderr.on('data', chunk => capture(chunk, true));
    child.stdin.on('error', () => {});
    const limitsPath = join(env.PRICE_COLLECTOR_STATE_DIR ?? sandbox, 'limits.json');
    const enabled = existsSync(limitsPath) ? Object.entries(json(limitsPath).actors).filter(([, a]) => a.actorIdConfirmed).map(([id]) => id) : [];
    child.stdin.end(`Run context: bounded manual test; enabled actors: ${enabled.join(', ')}. Process the prepared work using read-work.mjs. Write finished files, then stop.`);
    let finished = false;
    const finish = code => {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      clearTimeout(forceKill);
      process.removeListener('SIGINT', interrupt);
      process.removeListener('SIGTERM', interrupt);
      let result = {};
      try { result = JSON.parse(text); } catch { /* Failure remains generic; output stays private. */ }
      const combined = text + errorText;
      let status = stopped ?? (code === 0 && result.type === 'result' && result.is_error === false ? 'completed' : 'failed');
      if (!stopped && /enforced_spend_limit_reached|specified API usage limits|error_max_budget_usd|error_max_turns/.test(combined)) status = 'claude_limit';
      if (!stopped && /monthly-usage-hard-limit-exceeded|platform-feature-disabled/.test(combined)) status = 'apify_limit';
      const costUsd = typeof result.total_cost_usd === 'number' && Number.isFinite(result.total_cost_usd) ? result.total_cost_usd : null;
      // Counts and the CLI's own result subtype only; never the result text.
      const turns = Number.isInteger(result.num_turns) && result.num_turns >= 0 ? result.num_turns : null;
      const subtype = typeof result.subtype === 'string' && /^[a-z_]{1,40}$/.test(result.subtype) ? result.subtype : null;
      resolveResult({ status, costUsd, turns, subtype });
    };
    child.on('error', () => finish(1));
    child.on('close', finish);
  });
}

/**
 * The internal deadline for the whole Claude phase. Blank means 14 minutes for a sample and
 * FULL_RUN.deadlineMinutes for a full run. A whole number from 1 to 14 stops Claude part way through
 * for the interruption test, after some searches have finished and been written.
 */
export function collectorDeadlineMinutes(value, fullRun = false) {
  const text = String(value ?? '').trim();
  if (text === '') return fullRun ? FULL_RUN.deadlineMinutes : 14;
  if (!/^(?:[1-9]|1[0-4])$/.test(text)) throw new Error('Stop after must be a whole number of minutes from 1 to 14.');
  return Number(text);
}

function stateDirectory(env) {
  const root = resolve(env.RUNNER_TEMP ?? '');
  const dir = resolve(env.PRICE_COLLECTOR_STATE_DIR ?? '');
  if (!env.RUNNER_TEMP || !env.PRICE_COLLECTOR_STATE_DIR || !dir.startsWith(`${root}${sep}price-collector-`)) {
    throw new Error('Use a dedicated price-collector directory under RUNNER_TEMP.');
  }
  return dir;
}

// The production API origin, from the production frontend's own runtime configuration. The
// collector is a test-only stage: it must never issue work from, or submit results to, production.
export function productionApiOrigin(configPath = join(here, '../../frontend/inventory-app/src/assets/config.json')) {
  return new URL(json(configPath).apiBaseUrl).origin;
}

// Reviewed main or develop only (develop to run against the hosted testing API), an HTTPS test API
// URL without credentials, query or fragment, and never the production API.
export function collectorTarget(env, production = productionApiOrigin()) {
  if (!['refs/heads/main', 'refs/heads/develop'].includes(env.GITHUB_REF)) throw new Error('Only the reviewed main or develop workflow may issue a run.');
  const url = new URL(env.PRICE_COLLECTOR_TEST_API_URL);
  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash) throw new Error('Configure an HTTPS test API URL.');
  if (url.origin === new URL(production).origin) throw new Error('The collector test API must not be the production API.');
  return url;
}

/** Whether this is a production-sized run; anything but an explicit true or false is refused. */
export function fullRunRequested(value) {
  if (!['true', 'false'].includes(value)) throw new Error('Specify whether this is a full run.');
  return value === 'true';
}

async function prepare(env, stateDir) {
  const url = collectorTarget(env);
  const full = fullRunRequested(env.PRICE_COLLECTOR_FULL_RUN);
  const size = Number(env.PRICE_COLLECTOR_SAMPLE_SIZE);
  // Validate all local configuration before issuing a run (GET work writes a run). A full run
  // ignores the sample size.
  if (!full) sampleWork({ searches: [], matching: { waitingProducts: [] } }, size);
  collectorDeadlineMinutes(env.PRICE_COLLECTOR_STOP_AFTER_MINUTES, full);
  if (!['true', 'false'].includes(env.PRICE_COLLECTOR_INCLUDE_REVIEWED)) throw new Error('Specify whether verified retailer actors may run.');
  mkdirSync(stateDir, { mode: 0o700 }); // Refuse a previous attempt's directory; never reuse output.
  const token = await collectorToken(env);
  const work = await runGetWork({ env: { PRICE_COLLECTOR_API_BASE_URL: url.href.replace(/\/$/, ''), PRICE_COLLECTOR_API_TOKEN: token } });
  if (work.exitCode !== 0) throw new Error('The test API did not issue usable work.');
  const issued = JSON.parse(work.out);
  const sample = full ? fullRunWork(issued) : sampleWork(issued, size);
  save(join(stateDir, ISSUED_WORK), { ...sample, fullRun: full });
  const source = json(join(here, 'apify-limits.json'));
  const includeReviewed = env.PRICE_COLLECTOR_INCLUDE_REVIEWED === 'true';
  const limits = full ? fullRunLimits(source, includeReviewed) : sampleLimits(source, includeReviewed);
  save(join(stateDir, 'limits.json'), limits);
  const sandbox = join(stateDir, 'sandbox');
  mkdirSync(join(sandbox, 'scripts'), { recursive: true, mode: 0o700 });
  cpSync(here, join(sandbox, 'scripts/price-collector'), { recursive: true });
  mkdirSync(join(sandbox, 'price-collector-out'), { mode: 0o700 });
  mkdirSync(join(stateDir, 'home'), { mode: 0o700 });
  console.log(full
    ? `Full run prepared: ${sample.searches.length} of ${issued.searches.length} issued searches in ${workBatches(sample).length} batches. The rest will become not_searched.`
    : `Sample prepared: ${sample.searches.length} searches. Unselected work will become not_searched.`);
}

/**
 * Runs Claude once for a sample, or once per batch for a full run, until the work is done, a session
 * does not complete or the deadline passes. work.json holds the current batch only: read-work.mjs
 * gives Claude that batch and the write guard accepts result files for it alone. The status is saved
 * after every batch, so finalisation reports the batches that ran if the job is stopped part way.
 */
/** A running total that becomes unknown (null) once any part is unknown, never an undercount. */
const addKnown = (total, value, known) => (total === null || !known ? null : total + value);

/** One Claude session's limits: a full-run batch keeps its own caps; a sample keeps the defaults. */
const sessionOptions = (full, remainingMs) => (full
  ? { timeoutMs: Math.min(remainingMs, FULL_RUN.batchMinutes * 60 * 1000), maxTurns: FULL_RUN.maxTurnsPerBatch, maxBudgetUsd: FULL_RUN.maxBudgetUsdPerBatch }
  : { timeoutMs: remainingMs });

export async function collectBatches({ stateDir, issued, deadlineMs, launch, now = Date.now }) {
  const full = issued.fullRun === true;
  const batches = full ? workBatches(issued) : [issued];
  const endsAt = now() + deadlineMs;
  const total = { status: 'completed', costUsd: 0, turns: 0, subtype: null };
  if (full) Object.assign(total, { batchesRun: 0, batchCount: batches.length });
  for (const batch of batches) {
    const remainingMs = endsAt - now();
    if (remainingMs < 60 * 1000) { total.status = 'timeout'; break; }
    save(join(stateDir, 'work.json'), batch);
    const result = await launch(sessionOptions(full, remainingMs));
    if (full) total.batchesRun += 1;
    total.costUsd = addKnown(total.costUsd, result.costUsd, typeof result.costUsd === 'number');
    total.turns = addKnown(total.turns, result.turns, Number.isInteger(result.turns));
    total.subtype = result.subtype ?? null;
    total.status = result.status;
    save(join(stateDir, 'collector-status.json'), total);
    if (result.status !== 'completed') break;
  }
  save(join(stateDir, 'collector-status.json'), total);
  return total;
}

async function main(env, mode) {
  const stateDir = stateDirectory(env);
  if (mode === 'cleanup') { rmSync(stateDir, { recursive: true, force: true }); return; }
  if (mode === 'prepare') { await prepare(env, stateDir); return; }
  if (!existsSync(join(stateDir, ISSUED_WORK))) { console.log('No issued run available for finalisation.'); return; }
  const sandbox = join(stateDir, 'sandbox');
  if (mode === 'collect') {
    save(join(stateDir, 'collector-status.json'), { status: 'interrupted', costUsd: null });
    if (!env.ANTHROPIC_API_KEY || !env.APIFY_TOKEN) throw new Error('Configure the Claude and Apify environment secrets.');
    const childEnv = collectorEnvironment({ ...env, PRICE_COLLECTOR_OUT_DIR: join(sandbox, 'price-collector-out'),
      PRICE_COLLECTOR_GUARD_STATE: join(stateDir, 'guard-state.json') }, join(stateDir, 'home'));
    const issued = json(join(stateDir, ISSUED_WORK));
    const deadlineMs = collectorDeadlineMinutes(env.PRICE_COLLECTOR_STOP_AFTER_MINUTES, issued.fullRun === true) * 60 * 1000;
    const status = await collectBatches({ stateDir, issued, deadlineMs,
      launch: options => launchClaude({ sandbox, env: childEnv, executable: env.PRICE_COLLECTOR_CLAUDE_BIN, ...options }) });
    console.log(`Collector status: ${status.status}`);
    if (status.status !== 'completed') process.exitCode = 1;
    return;
  }
  if (mode === 'finalize') {
    // Always fetch a new short-lived token; no bearer is persisted between steps.
    const token = await collectorToken(env);
    const report = await finaliseSample({ stateDir, outputDir: join(sandbox, 'price-collector-out'), env, token });
    console.log(report);
    if (env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, report);
    if (!json(join(stateDir, 'submission-status.json')).ok) process.exitCode = 1;
    return;
  }
  throw new Error('Unknown collector workflow operation.');
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    await main(process.env, process.argv[2]);
  } catch {
    // Never print thrown provider bodies, filesystem names, work text or credential values.
    console.error('Collector workflow step failed. Check environment configuration and private provider run status; no automatic retry was made.');
    process.exitCode = 1;
  }
}
