// Complete tool boundary for #929. The existing guard remains responsible for atomic call
// reservations and write-once files; this wrapper denies every other tool and shell command.
import { appendFileSync, readFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runGuard } from './apify-guard.mjs';
import { recordRun, reserveRead } from './workflow-run-ledger.mjs';
import { textOf, trace } from './trace.mjs';

export function permittedCommand(command) {
  if (typeof command !== 'string' || /[\x00-\x1f$`;&|<>\\!*?()[\]{}#]/.test(command)) return false;
  if (command === 'node scripts/price-collector/read-work.mjs') return true;
  // Each argument is either a simple bare token or a quoted string without shell expansion.
  return /^node scripts\/price-collector\/(?:jbmetro-lookup|pagination)\.mjs(?: (?:[a-zA-Z0-9_:/.,=+%-]+|"[^"']*"|'[^'"]*'))+$/.test(command);
}

// `code` is a fixed, non-enumerable diagnostic label: it never reaches Claude Code's JSON, and it is
// what the summary counts per tool; the full reason is in the run's trace (trace.mjs).
const decision = (permissionDecision, permissionDecisionReason, updatedInput, code = 'ok') => Object.defineProperty(
  { hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision, permissionDecisionReason, ...(updatedInput ? { updatedInput } : {}) } },
  'code', { value: code });

export const TOOL_LOG_FILE = 'tool-log.jsonl';
const helperNames = ['read-work', 'jbmetro-lookup', 'pagination'];

/** A public-safe label for a tool call: a fixed helper name, or a tool name of plain characters. */
export function toolLabel(event) {
  const name = String(event?.tool_name ?? '');
  if (name === 'Bash') {
    const helper = helperNames.find(h => String(event.tool_input?.command ?? '').startsWith(`node scripts/price-collector/${h}.mjs`));
    return `Bash:${helper ?? 'other'}`;
  }
  return /^[A-Za-z0-9_-]{1,80}$/.test(name) ? name : 'other';
}

/** One line per hook decision: label, allow or deny, and a fixed code. No input, path or reason text. */
export function noteToolUse(stateDir, entry) {
  try {
    appendFileSync(join(stateDir, TOOL_LOG_FILE), `${JSON.stringify(entry)}\n`, { mode: 0o600 });
  } catch {
    // Diagnostics never change a decision.
  }
}

/** 'ok', or which part of the sample check a result file failed. */
function writeSampleCode(event, stateDir) {
  const work = JSON.parse(readFileSync(join(stateDir, 'work.json'), 'utf8'));
  const input = event.tool_input;
  let content;
  try { content = JSON.parse(input.content); } catch { return 'write-not-json'; }
  if (content?.runId !== work.runId) return 'write-wrong-run';
  const name = basename(String(input.file_path ?? ''));
  if (/^[1-9]\d*\.json$/.test(name)) {
    if (String(content.workItemId) + '.json' !== name) return 'write-name-mismatch';
    return work.searches.some(s => s.workItemId === content.workItemId) ? 'ok' : 'write-not-in-sample';
  }
  if (!/^match-[a-zA-Z0-9_-]+\.json$/.test(name)) return 'write-bad-name';
  return work.matching.waitingProducts.some(p => p.waitingKey === content.waitingKey) ? 'ok' : 'write-not-in-sample';
}

// Actor tools and call-actor return only the run status and storage ids; the rows are read with
// get-dataset-items, and a run that outlives waitSecs is followed with get-actor-run (Apify MCP
// server docs). These reads are free, so they are not reserved against the actor call cap, but
// each is allowed only for a run this collector started, with a bounded number of polls and reads
// (workflow-run-ledger.mjs). Aborting only reduces spend. Every other Apify account tool is denied.
const storageId = /^[a-zA-Z0-9]{1,64}$/;
const readTools = new Map([
  ['get-dataset-items', 'datasetId'], ['get-actor-run', 'runId'], ['abort-actor-run', 'runId'],
]);
const actorCallTools = new Set(['mcp__apify__call-actor', 'mcp__apify__call_actor']);

async function readDecision(tool, input, limits, stateDir) {
  const idField = readTools.get(tool);
  if (!storageId.test(String(input?.[idField] ?? ''))) {
    return decision('deny', `${tool} needs the plain ${idField} an allowed actor call returned.`, undefined, 'bad-id');
  }
  let updatedInput;
  if (tool === 'get-dataset-items') {
    // A sample call returns at most the largest page size, so a read never needs more rows than that.
    const cap = Math.max(...Object.values(limits.actors).map(actor => actor.pageSize));
    const offset = input.offset ?? 0;
    if (!Number.isInteger(offset) || offset < 0) return decision('deny', 'offset must be a whole number of items.', undefined, 'bad-offset');
    const asked = input.limit ?? cap;
    if (!Number.isInteger(asked) || asked < 1) return decision('deny', 'limit must be a positive whole number of items.', undefined, 'bad-limit');
    updatedInput = { ...input, offset, limit: Math.min(asked, cap) };
  }
  const refusal = await reserveRead(stateDir, tool, input);
  if (refusal) return decision('deny', refusal.message, undefined, refusal.code);
  return decision('allow', `${tool} follows a run this collector started.`, updatedInput);
}

/** PostToolUse: remembers the run an allowed actor call started, so its rows may be read. */
export async function recordDecision(event, env) {
  const name = event?.tool_name ?? '';
  if (!name.startsWith('mcp__apify__')) return {};
  const limits = JSON.parse(readFileSync(join(env.PRICE_COLLECTOR_STATE_DIR, 'limits.json'), 'utf8'));
  const startedRun = actorCallTools.has(name) || dedicatedActorTools(limits).includes(name);
  if (startedRun || name === 'mcp__apify__get-actor-run') {
    const recorded = await recordRun(env.PRICE_COLLECTOR_STATE_DIR, { startedRun, response: event.tool_response });
    // The shape of the answer, never its content, so a run that is not recorded can be explained.
    noteToolUse(env.PRICE_COLLECTOR_STATE_DIR, { tool: toolLabel(event), event: 'after', code: recorded ? 'recorded' : `not-recorded-${responseShape(event.tool_response)}` });
  }
  return {};
}

/**
 * A fixed label for why an Apify call failed (PostToolUseFailure). The error text itself can quote
 * the input or the actor's answer, so only these labels are kept; the order puts the most specific
 * cause first.
 */
const failurePatterns = [
  ['failed-rate-limit', /\b429\b|rate.?limit|too many requests/i],
  ['failed-limit', /\b402\b|credit|quota|usage limit|monthly limit|insufficient/i],
  ['failed-auth', /\b40[13]\b|unauthori[sz]ed|forbidden|token/i],
  ['failed-not-found', /\b404\b|not found|does not exist/i],
  ['failed-input', /input|schema|validat|invalid|required/i],
  ['failed-timeout', /time.?out|timed out/i],
];

export function failureCode(event) {
  if (event?.is_interrupt === true) return 'failed-interrupt';
  const error = String(event?.error ?? '');
  for (const [code, pattern] of failurePatterns) {
    if (pattern.test(error)) return code;
  }
  return 'failed-other';
}

/** PostToolUseFailure: notes the fixed failure label of a failed Apify call, and decides nothing. */
export function failureDecision(event, env) {
  if (!String(event?.tool_name ?? '').startsWith('mcp__apify__')) return {};
  noteToolUse(env.PRICE_COLLECTOR_STATE_DIR, { tool: toolLabel(event), event: 'failed', code: failureCode(event) });
  return {};
}

function responseShape(response) {
  if (typeof response === 'string') return 'string';
  if (Array.isArray(response)) return 'array';
  if (response && typeof response === 'object') return response.structuredContent ? 'structured' : Array.isArray(response.content) ? 'content' : 'object';
  return 'none';
}

function dedicatedActorTools(limits) {
  return Object.keys(limits.actors).flatMap(id => [
    `mcp__apify__${id.replace('/', '--')}`, `mcp__apify__${id.replace('/', '-slash-')}`,
  ]);
}

/**
 * JB Metro's caps per run type. A sample reads one page of 5; a full run reads up to 5 pages of 20
 * (100 products), with 2 attempts of 15 s per page so one call stays inside the Bash tool's
 * 2-minute limit. HARD is the ceiling a limits file can never exceed.
 */
export const JB_METRO_CAPS = Object.freeze({
  sample: Object.freeze({ pageSize: 5, maxPages: 1, batchSize: 5, attempts: 1, timeoutMs: 15000 }),
  full: Object.freeze({ pageSize: 20, maxPages: 5, batchSize: 20, attempts: 2, timeoutMs: 15000 }),
});
const JB_METRO_HARD = Object.freeze({ pageSize: 20, maxPages: 5, batchSize: 20, attempts: 2, timeoutMs: 15000 });
const JB_METRO_FLAGS = Object.freeze({ pageSize: '--page-size', maxPages: '--max-pages', batchSize: '--batch-size', attempts: '--attempts', timeoutMs: '--timeout-ms' });

/** The flags for this run's JB Metro caps; the sample caps when the limits name none or a bad one. */
export function jbMetroFlags(stateDir) {
  let caps = JB_METRO_CAPS.sample;
  try {
    const chosen = JSON.parse(readFileSync(join(stateDir, 'limits.json'), 'utf8')).jbMetro;
    const valid = chosen && Object.keys(JB_METRO_HARD).every(key => Number.isInteger(chosen[key]) && chosen[key] >= 1 && chosen[key] <= JB_METRO_HARD[key]);
    if (valid) caps = chosen;
  } catch { /* Missing or unreadable limits keep the sample caps. */ }
  return Object.entries(JB_METRO_FLAGS).map(([key, flag]) => `${flag} ${caps[key]}`).join(' ');
}

/** A Bash call: a foreground collector helper only, with the run's JB Metro caps appended. */
function bashDecision(input, env) {
  if (input?.run_in_background || !permittedCommand(input?.command)) {
    return decision('deny', 'Only foreground collector helper commands with literal arguments are allowed.', undefined, 'not-helper');
  }
  const command = input.command;
  // The run's own JB Metro caps are appended last, and the lookup takes the last value given, so a
  // flag Claude added cannot widen the run.
  const fixed = command.startsWith('node scripts/price-collector/jbmetro-lookup.mjs ')
    ? `${command} ${jbMetroFlags(env.PRICE_COLLECTOR_STATE_DIR)}` : command;
  return decision('allow', 'Approved collector helper.', { ...input, command: fixed });
}

export async function toolDecision(event, env) {
  const name = event?.tool_name ?? '';
  if (name === 'Bash') return bashDecision(event.tool_input, env);
  if (name !== 'Write' && !name.startsWith('mcp__apify__')) return decision('deny', 'This tool is outside the collector allowlist.', undefined, 'not-allowlisted');
  const limits = JSON.parse(readFileSync(join(env.PRICE_COLLECTOR_STATE_DIR, 'limits.json'), 'utf8'));
  const dedicated = dedicatedActorTools(limits);
  const tool = name.slice('mcp__apify__'.length);
  if (readTools.has(tool)) return readDecision(tool, event.tool_input, limits, env.PRICE_COLLECTOR_STATE_DIR);
  if (name !== 'Write' && !dedicated.includes(name) && !actorCallTools.has(name)) {
    return decision('deny', 'Only the reviewed actor execution tools are allowed, not arbitrary Apify account tools.', undefined, 'not-reviewed-tool');
  }
  if (name === 'Write') {
    const sample = writeSampleCode(event, env.PRICE_COLLECTOR_STATE_DIR);
    if (sample !== 'ok') return decision('deny', 'A result must answer this prepared sample and its run.', undefined, sample);
  }
  // The connector's dedicated tools use --, while the original guard knows -slash-.
  // Normalize only exact, reviewed actor names; unknown tools still fail closed.
  const actor = Object.keys(limits.actors).find(id => name === `mcp__apify__${id.replace('/', '--')}`);
  const normalized = actor ? { ...event, tool_name: `mcp__apify__${actor.replace('/', '-slash-')}` } : event;
  const answer = await runGuard({ event: normalized, env, limits });
  const parsed = JSON.parse(answer.out);
  const guardDecision = parsed.hookSpecificOutput?.permissionDecision;
  return Object.defineProperty(parsed, 'code', { value: guardDecision === 'deny' ? `guard-${name === 'Write' ? 'write' : 'actor'}` : 'ok' });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  let event;
  try {
    let raw = '';
    for await (const chunk of process.stdin) raw += chunk;
    event = JSON.parse(raw);
    const stateDir = process.env.PRICE_COLLECTOR_STATE_DIR;
    if (event.hook_event_name === 'PostToolUse') {
      trace(stateDir, { event: 'answer', tool: event.tool_name, toolUseId: event.tool_use_id, response: textOf(event.tool_response) }, process.env);
      process.stdout.write(JSON.stringify(await recordDecision(event, process.env)));
    } else if (event.hook_event_name === 'PostToolUseFailure') {
      trace(stateDir, { event: 'failed', tool: event.tool_name, toolUseId: event.tool_use_id, code: failureCode(event), error: textOf(event.error) }, process.env);
      process.stdout.write(JSON.stringify(failureDecision(event, process.env)));
    } else {
      const answer = await toolDecision(event, process.env);
      noteToolUse(stateDir, { tool: toolLabel(event), event: 'before',
        decision: answer.hookSpecificOutput?.permissionDecision, code: answer.code ?? 'ok' });
      const output = answer.hookSpecificOutput ?? {};
      trace(stateDir, { event: 'call', tool: event.tool_name, toolUseId: event.tool_use_id, decision: output.permissionDecision,
        code: answer.code ?? 'ok', reason: output.permissionDecisionReason, input: textOf(output.updatedInput ?? event.tool_input) }, process.env);
      process.stdout.write(JSON.stringify(answer));
    }
  } catch {
    // Hook errors cannot become a permissive response or leak the event/credentials.
    if (process.env.PRICE_COLLECTOR_STATE_DIR) {
      noteToolUse(process.env.PRICE_COLLECTOR_STATE_DIR, { tool: toolLabel(event), event: { PostToolUse: 'after', PostToolUseFailure: 'failed' }[event?.hook_event_name] ?? 'before', decision: 'deny', code: 'hook-error' });
    }
    process.stdout.write(JSON.stringify(decision('deny', 'Collector policy could not verify this call.')));
    process.exitCode = 2;
  }
}
