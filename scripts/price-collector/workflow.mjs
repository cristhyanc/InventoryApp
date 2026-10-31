// Manual test orchestration (#929). Only this trusted parent exchanges OIDC and submits results.
// Claude gets a small work snapshot and Apify access, never the API/OIDC/GitHub credentials.
import { spawn } from 'node:child_process';
import { appendFileSync, cpSync, existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { collectorToken } from './workflow-auth.mjs';
import { runGetWork } from './get-work.mjs';
import { runSubmission } from './submit-results.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const reviewedActors = new Set(['abotapi/kmart-au-scraper', 'axlymxp/bigw-product-scraper', 'dromb/big-w-au-product-scraper']);
const json = path => JSON.parse(readFileSync(path, 'utf8'));
const save = (path, value) => writeFileSync(path, `${JSON.stringify(value)}\n`, { mode: 0o600 });

export function sampleWork(work, count) {
  if (!Number.isInteger(count) || count < 1 || count > 10) throw new Error('Sample size must be a whole number from 1 to 10.');
  const searches = work.searches.slice(0, count);
  const waitingKeys = new Set(searches.filter(s => s.product.kind === 'waiting').map(s => s.product.waitingKey));
  const waitingProducts = work.matching.waitingProducts.filter(p => waitingKeys.has(p.waitingKey));
  return { ...work, searches, searchCount: searches.length,
    matching: { ...work.matching, required: waitingProducts.length > 0, waitingProducts } };
}

export function sampleLimits(source, includeReviewed) {
  const limits = structuredClone(source);
  limits.perRun.maxActorCalls = Math.min(12, limits.perRun.maxActorCalls);
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

export function publicSubmissionReport(out) {
  return String(out).split('\n').filter(line => reportPatterns.some(pattern => pattern.test(line))).join('\n');
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

export async function finaliseSample({ stateDir, outputDir, env, token, submit = runSubmission }) {
  const work = json(join(stateDir, 'work.json'));
  const status = existsSync(join(stateDir, 'collector-status.json')) ? json(join(stateDir, 'collector-status.json')) : { status: 'not_started', costUsd: null };
  const { dir, refused } = submissionFolder(stateDir, outputDir, work);
  const result = await submit({ argv: ['--dir', dir, '--run-id', String(work.runId)],
    env: { PRICE_COLLECTOR_API_BASE_URL: env.PRICE_COLLECTOR_TEST_API_URL, PRICE_COLLECTOR_API_TOKEN: token } });
  const allowedStatuses = ['completed', 'failed', 'timeout', 'interrupted', 'output_limit', 'claude_limit', 'apify_limit', 'not_started'];
  const label = allowedStatuses.includes(status.status) ? status.status : 'failed';
  const cost = typeof status.costUsd === 'number' && Number.isFinite(status.costUsd) && status.costUsd >= 0 ? status.costUsd.toFixed(6) : 'unknown';
  const report = `Collector status: ${label}\nClaude cost USD: ${cost}\nSample searches: ${work.searches.length}\nFiles refused before submission: ${refused}\n${publicSubmissionReport(result.out)}\nSubmission status: ${result.exitCode === 0 && refused === 0 ? 'completed' : 'failed'}\n`;
  save(join(stateDir, 'submission-status.json'), { ok: result.exitCode === 0 && refused === 0 });
  return report;
}

/** No raw CLI output or transcript is sent to stdout, stderr, Actions outputs or artifacts. */
export function launchClaude({ executable, sandbox, env, timeoutMs = 14 * 60 * 1000 }) {
  // An absolute path to the job's own installation, never a `claude` looked up on PATH.
  if (typeof executable !== 'string' || !isAbsolute(executable)) {
    return Promise.reject(new Error('The collector needs the absolute path of the reviewed Claude CLI.'));
  }
  return new Promise(resolveResult => {
    const args = ['-p', '--output-format', 'json', '--no-session-persistence', '--disable-slash-commands',
      '--setting-sources', '', '--settings', 'scripts/price-collector/claude-settings.json',
      '--permission-mode', 'dontAsk', '--tools', 'Bash,Write', '--strict-mcp-config',
      '--mcp-config', 'scripts/price-collector/apify-mcp.json', '--max-turns', '40', '--max-budget-usd', '1',
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
    child.stdin.end(`Run context: bounded manual test; enabled actors: ${enabled.join(', ')}. Process the prepared test sample using read-work.mjs. Write finished files, then stop.`);
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
      resolveResult({ status, costUsd });
    };
    child.on('error', () => finish(1));
    child.on('close', finish);
  });
}

/**
 * The internal deadline. Blank means the full 14 minutes. A smaller whole number stops Claude part
 * way through for the interruption test, after some searches have finished and been written.
 */
export function collectorDeadlineMinutes(value) {
  const text = String(value ?? '').trim();
  if (text === '') return 14;
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

async function prepare(env, stateDir) {
  if (env.GITHUB_REF !== 'refs/heads/main') throw new Error('Only the reviewed main workflow may issue a run.');
  const url = new URL(env.PRICE_COLLECTOR_TEST_API_URL);
  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash) throw new Error('Configure an HTTPS test API URL.');
  const size = Number(env.PRICE_COLLECTOR_SAMPLE_SIZE);
  // Validate all local configuration before issuing a run (GET work writes a run).
  sampleWork({ searches: [], matching: { waitingProducts: [] } }, size);
  collectorDeadlineMinutes(env.PRICE_COLLECTOR_STOP_AFTER_MINUTES);
  if (!['true', 'false'].includes(env.PRICE_COLLECTOR_INCLUDE_REVIEWED)) throw new Error('Specify whether verified retailer actors may run.');
  mkdirSync(stateDir, { mode: 0o700 }); // Refuse a previous attempt's directory; never reuse output.
  const token = await collectorToken(env);
  const work = await runGetWork({ env: { PRICE_COLLECTOR_API_BASE_URL: url.href.replace(/\/$/, ''), PRICE_COLLECTOR_API_TOKEN: token } });
  if (work.exitCode !== 0) throw new Error('The test API did not issue usable work.');
  const sample = sampleWork(JSON.parse(work.out), size);
  save(join(stateDir, 'work.json'), sample);
  const limits = sampleLimits(json(join(here, 'apify-limits.json')), env.PRICE_COLLECTOR_INCLUDE_REVIEWED === 'true');
  save(join(stateDir, 'limits.json'), limits);
  const sandbox = join(stateDir, 'sandbox');
  mkdirSync(join(sandbox, 'scripts'), { recursive: true, mode: 0o700 });
  cpSync(here, join(sandbox, 'scripts/price-collector'), { recursive: true });
  mkdirSync(join(sandbox, 'price-collector-out'), { mode: 0o700 });
  mkdirSync(join(stateDir, 'home'), { mode: 0o700 });
  console.log(`Sample prepared: ${sample.searches.length} searches. Unselected work will become not_searched.`);
}

async function main(env, mode) {
  const stateDir = stateDirectory(env);
  if (mode === 'cleanup') { rmSync(stateDir, { recursive: true, force: true }); return; }
  if (mode === 'prepare') { await prepare(env, stateDir); return; }
  if (!existsSync(join(stateDir, 'work.json'))) { console.log('No issued run available for finalisation.'); return; }
  const sandbox = join(stateDir, 'sandbox');
  if (mode === 'collect') {
    save(join(stateDir, 'collector-status.json'), { status: 'interrupted', costUsd: null });
    if (!env.ANTHROPIC_API_KEY || !env.APIFY_TOKEN) throw new Error('Configure the Claude and Apify environment secrets.');
    const childEnv = collectorEnvironment({ ...env, PRICE_COLLECTOR_OUT_DIR: join(sandbox, 'price-collector-out'),
      PRICE_COLLECTOR_GUARD_STATE: join(stateDir, 'guard-state.json') }, join(stateDir, 'home'));
    const status = await launchClaude({ sandbox, env: childEnv, executable: env.PRICE_COLLECTOR_CLAUDE_BIN,
      timeoutMs: collectorDeadlineMinutes(env.PRICE_COLLECTOR_STOP_AFTER_MINUTES) * 60 * 1000 });
    save(join(stateDir, 'collector-status.json'), status);
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
