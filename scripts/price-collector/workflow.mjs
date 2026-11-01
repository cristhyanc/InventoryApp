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
import { appendLog, clip, logDir, readTrace, redact, secretsOf, textOf, trace } from './trace.mjs';

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

/** `prefix + value` when there is a value, otherwise nothing. */
const part = (prefix, value) => (value === null || value === undefined || value === '' ? '' : prefix + value);

/** The product a search is for, as a person reads it. */
export function productLabel(product = {}) {
  if (product.kind === 'master') {
    const name = [product.brand, product.name].filter(Boolean).join(' ');
    const size = product.sizeValue == null ? '' : ' ' + product.sizeValue + (product.sizeUnit ?? '');
    return name + size + ' (master product ' + product.masterProductId + ')';
  }
  return (product.searchName ?? '?') + part(' - ', product.unitLabel) + ' (waiting product)';
}

const readJson = path => { try { return JSON.parse(readFileSync(path, 'utf8')); } catch { return undefined; } };
const oneLine = value => String(value ?? '').replace(/\s+/g, ' ').trim();

/** What the API did with a row or match, with its reason when it gave one. */
function verdictOf(decision) {
  if (!decision) return 'no API decision';
  return decision.outcome + part(' - ', decision.reason && oneLine(decision.reason));
}

/** One row as written by Claude, with what the API decided for it. */
function rowLine(row, decision) {
  const price = row.packPrice == null ? 'no price' : '$' + row.packPrice;
  const title = oneLine(row.title) || '(no title)';
  return `  - ${title} [${oneLine(row.supplierProductId)}]: ${price}${part(' for ', row.packQuantity)}, ${oneLine(row.stockStatus)}, ${oneLine(row.sourceUrl)} -> **${verdictOf(decision)}**`;
}

/** The heading line of one search: product, supplier, route and work item. */
function searchHeading(search, index) {
  const supplier = oneLine(search.supplierName ?? 'supplier ' + search.supplierPresetId);
  const route = (search.priceConnector ?? 'no connector') + part(':', search.priceConnectorKey);
  return `${index + 1}. **${oneLine(productLabel(search.product))}** at **${supplier}** (${route}), work item ${search.workItemId}`;
}

/** The lines for one search's result file, or that Claude wrote none. */
function searchResultLines(search, outputDir, byRow) {
  const file = readJson(join(outputDir, `${search.workItemId}.json`));
  if (file === undefined) return ['  - no result file written (not_searched)'];
  const rows = Array.isArray(file.rows) ? file.rows : [];
  const notes = part('; notes: ', file.notes && oneLine(file.notes));
  return [
    `  - outcome **${oneLine(file.outcome)}**${part(' ', file.failureReason && '(' + file.failureReason + ')')}, ${rows.length} row(s)${notes}`,
    ...rows.map(row => rowLine(row, byRow.get(`${search.workItemId}|${row.supplierProductId}`))),
  ];
}

/**
 * What became of every search: the product and supplier, the file Claude wrote (or that it wrote
 * none), every row in it with the API's decision, and the API's decision on match files.
 */
export function searchDetails(work, outputDir, decisions = []) {
  const byRow = new Map(decisions.filter(d => d.workItemId != null).map(d => [`${d.workItemId}|${d.supplierProductId}`, d]));
  const lines = ['### Searches', ''];
  work.searches.forEach((search, index) => lines.push(searchHeading(search, index), ...searchResultLines(search, outputDir, byRow)));
  const matches = decisions.filter(d => d.workItemId == null);
  if (matches.length > 0) {
    lines.push('', '### Match decisions', '');
    for (const match of matches) lines.push(`- ${oneLine(match.waitingKey)} -> ${match.masterProductId ?? 'new'}: **${verdictOf(match)}**`);
  }
  return lines.join('\n') + '\n';
}

/** One trace entry as a line of the step log. */
function timelineLine(entry, width) {
  const time = String(entry.at ?? '').slice(11, 19);
  switch (entry.event) {
    case 'call': {
      const refusal = entry.code === 'ok' ? '' : ` (${entry.code}: ${oneLine(entry.reason)})`;
      return `${time} CALL ${entry.tool} ${entry.decision}${refusal} ${clip(oneLine(entry.input), width)}`;
    }
    case 'answer': return `${time}   -> ${entry.tool} answered: ${clip(oneLine(entry.response), width)}`;
    case 'failed': return `${time}   -> ${entry.tool} FAILED (${entry.code}): ${clip(oneLine(entry.error), width)}`;
    case 'batch': return `${time} SESSION ${entry.session} of ${entry.of}: work items ${(entry.workItemIds ?? []).join(', ')}`;
    case 'session': {
      const ending = entry.subtype ?? entry.stopped ?? 'unknown';
      return `${time} SESSION ${entry.session} ended: ${ending}, ${entry.turns ?? '?'} turns, $${entry.costUsd ?? '?'}; Claude said: ${clip(oneLine(entry.result), width)}`;
    }
    default: return `${time} ${oneLine(JSON.stringify(entry))}`;
  }
}

/** Every tool call of the run in order: the input, the hook's decision and the answer, shortened. */
export function toolTimeline(stateDir, width = 600) {
  const entries = readTrace(stateDir);
  if (entries.length === 0) return 'No tool calls were traced.\n';
  return entries.map(entry => timelineLine(entry, width)).join('\n') + '\n';
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
 * against plain patterns again here, so a tampered log line cannot change the counts' shape.
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
  const errors = result.err ? `Submission errors:\n${result.err}` : '';
  const report = `Collector status: ${label}\nClaude cost USD: ${cost}\nClaude turns: ${turns}; result: ${subtype}\n${batches}${toolUseReport(stateDir)}Searches given to Claude: ${work.searches.length}\nFiles refused before submission: ${refused}\n${result.out}${errors}Submission status: ${result.exitCode === 0 && refused === 0 ? 'completed' : 'failed'}\n`;
  const details = searchDetails(work, outputDir, result.decisions ?? []);
  appendLog(stateDir, 'submission.json', JSON.stringify({ out: result.out, err: result.err ?? '', decisions: result.decisions ?? [] }, null, 2), env);
  appendLog(stateDir, 'summary.md', `${report}\n${details}`, env);
  save(join(stateDir, 'submission-status.json'), { ok: result.exitCode === 0 && refused === 0 });
  return { report, details };
}

/** The last `result` message of a stream-json transcript, or the whole text when it is one object. */
export function claudeResult(text) {
  const lines = String(text).split('\n').filter(line => line.trim() !== '');
  for (let index = lines.length - 1; index >= 0; index -= 1) {
    try {
      const message = JSON.parse(lines[index]);
      if (message?.type === 'result') return message;
    } catch { /* A partial line is not the result. */ }
  }
  try { return JSON.parse(text) ?? {}; } catch { return {}; }
}

/**
 * Runs one Claude session. Its whole transcript (stream-json: every message, tool call and answer)
 * is written, masked, to the run's log folder as claude-session-<n>.jsonl for the run artifact.
 */
export function launchClaude({ executable, sandbox, env, timeoutMs = 14 * 60 * 1000, maxTurns = SAMPLE_RUN.maxTurns, maxBudgetUsd = SAMPLE_RUN.maxBudgetUsd, session = 1 }) {
  // An absolute path to the job's own installation, never a `claude` looked up on PATH.
  if (typeof executable !== 'string' || !isAbsolute(executable)) {
    return Promise.reject(new Error('The collector needs the absolute path of the reviewed Claude CLI.'));
  }
  return new Promise(resolveResult => {
    const args = ['-p', '--output-format', 'stream-json', '--verbose', '--no-session-persistence', '--disable-slash-commands',
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
    const stateDir = env.PRICE_COLLECTOR_STATE_DIR;
    const capture = (chunk, stderr) => {
      if (text.length + errorText.length + chunk.length > 40 * 1024 * 1024) { stop('output_limit'); return; }
      if (stderr) errorText += chunk.toString(); else text += chunk.toString();
    };
    child.stdout.on('data', chunk => capture(chunk, false));
    child.stderr.on('data', chunk => capture(chunk, true));
    child.stdin.on('error', () => {});
    const limitsPath = join(env.PRICE_COLLECTOR_STATE_DIR ?? sandbox, 'limits.json');
    const enabled = existsSync(limitsPath) ? Object.entries(json(limitsPath).actors).filter(([, a]) => a.actorIdConfirmed).map(([id]) => id) : [];
    child.stdin.end(`Run context: bounded manual test; enabled Apify actors: ${enabled.join(', ')}. JB Metro searches use jbmetro-lookup.mjs, which is always enabled. Process the prepared work using read-work.mjs. Write finished files, then stop.`);
    let finished = false;
    const finish = code => {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      clearTimeout(forceKill);
      process.removeListener('SIGINT', interrupt);
      process.removeListener('SIGTERM', interrupt);
      const result = claudeResult(text);
      appendLog(stateDir, `claude-session-${session}.jsonl`, text.trimEnd(), env);
      if (errorText.trim() !== '') appendLog(stateDir, `claude-session-${session}.stderr.txt`, errorText.trimEnd(), env);
      trace(stateDir, { event: 'session', session, exitCode: code, stopped, subtype: result.subtype ?? null, turns: result.num_turns ?? null,
        costUsd: result.total_cost_usd ?? null, result: textOf(result.result), stderr: clip(errorText, 4000) }, env);
      const combined = text + errorText;
      let status = stopped ?? (code === 0 && result.type === 'result' && result.is_error === false ? 'completed' : 'failed');
      if (!stopped && /enforced_spend_limit_reached|specified API usage limits|error_max_budget_usd|error_max_turns/.test(combined)) status = 'claude_limit';
      if (!stopped && /monthly-usage-hard-limit-exceeded|platform-feature-disabled/.test(combined)) status = 'apify_limit';
      const costUsd = typeof result.total_cost_usd === 'number' && Number.isFinite(result.total_cost_usd) ? result.total_cost_usd : null;
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
  // The whole work list the API issued, including the searches a sample leaves unsearched.
  appendLog(stateDir, 'issued-work.json', JSON.stringify(issued, null, 2), env);
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
  for (const [index, batch] of batches.entries()) {
    const remainingMs = endsAt - now();
    if (remainingMs < 60 * 1000) { total.status = 'timeout'; break; }
    save(join(stateDir, 'work.json'), batch);
    trace(stateDir, { event: 'batch', session: index + 1, of: batches.length, workItemIds: batch.searches.map(search => search.workItemId) });
    const result = await launch({ ...sessionOptions(full, remainingMs), session: index + 1 });
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

const issuedWork = stateDir => json(join(stateDir, ISSUED_WORK));

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
    const outputDir = join(sandbox, 'price-collector-out');
    const { report, details } = await finaliseSample({ stateDir, outputDir, env, token });
    // Keep Claude's result files with the run's logs, so the artifact holds exactly what was submitted.
    if (existsSync(outputDir)) cpSync(outputDir, join(logDir(stateDir), 'result-files'), { recursive: true });
    const secrets = secretsOf({ ...env, PRICE_COLLECTOR_API_TOKEN: token });
    console.log(redact(`${report}\n${details}\n### Tool calls\n${toolTimeline(stateDir)}`, secrets));
    if (env.GITHUB_STEP_SUMMARY) {
      appendFileSync(env.GITHUB_STEP_SUMMARY, redact(`## Price collector run ${issuedWork(stateDir).runId}\n\n\`\`\`\n${report}\`\`\`\n\n${details}\nThe full log (every tool call with its input and answer, and each Claude session's transcript) is in this run's **price-collector-logs** artifact; the tool calls are also in the finalise step's log.\n`, secrets));
    }
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
