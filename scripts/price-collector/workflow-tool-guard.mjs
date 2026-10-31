// Complete tool boundary for #929. The existing guard remains responsible for atomic call
// reservations and write-once files; this wrapper denies every other tool and shell command.
import { readFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runGuard } from './apify-guard.mjs';
import { recordRun, reserveRead } from './workflow-run-ledger.mjs';

export function permittedCommand(command) {
  if (typeof command !== 'string' || /[\x00-\x1f$`;&|<>\\!*?()[\]{}#]/.test(command)) return false;
  if (command === 'node scripts/price-collector/read-work.mjs') return true;
  // Each argument is either a simple bare token or a quoted string without shell expansion.
  return /^node scripts\/price-collector\/(?:jbmetro-lookup|pagination)\.mjs(?: (?:[a-zA-Z0-9_:/.,=+%-]+|"[^"']*"|'[^'"]*'))+$/.test(command);
}

const decision = (permissionDecision, permissionDecisionReason, updatedInput) => ({ hookSpecificOutput: {
  hookEventName: 'PreToolUse', permissionDecision, permissionDecisionReason,
  ...(updatedInput ? { updatedInput } : {}),
} });

function writeBelongsToSample(event, stateDir) {
  const work = JSON.parse(readFileSync(join(stateDir, 'work.json'), 'utf8'));
  const input = event.tool_input;
  const content = JSON.parse(input.content);
  if (content.runId !== work.runId) return false;
  const name = basename(input.file_path);
  if (/^[1-9]\d*\.json$/.test(name)) {
    return String(content.workItemId) + '.json' === name && work.searches.some(s => s.workItemId === content.workItemId);
  }
  return /^match-[a-zA-Z0-9_-]+\.json$/.test(name) &&
    work.matching.waitingProducts.some(p => p.waitingKey === content.waitingKey);
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
    return decision('deny', `${tool} needs the plain ${idField} an allowed actor call returned.`);
  }
  let updatedInput;
  if (tool === 'get-dataset-items') {
    // A sample call returns at most the largest page size, so a read never needs more rows than that.
    const cap = Math.max(...Object.values(limits.actors).map(actor => actor.pageSize));
    const offset = input.offset ?? 0;
    if (!Number.isInteger(offset) || offset < 0) return decision('deny', 'offset must be a whole number of items.');
    const asked = input.limit ?? cap;
    if (!Number.isInteger(asked) || asked < 1) return decision('deny', 'limit must be a positive whole number of items.');
    updatedInput = { ...input, offset, limit: Math.min(asked, cap) };
  }
  const refusal = await reserveRead(stateDir, tool, input);
  if (refusal) return decision('deny', refusal);
  return decision('allow', `${tool} follows a run this collector started.`, updatedInput);
}

/** PostToolUse: remembers the run an allowed actor call started, so its rows may be read. */
export async function recordDecision(event, env) {
  const name = event?.tool_name ?? '';
  if (!name.startsWith('mcp__apify__')) return {};
  const limits = JSON.parse(readFileSync(join(env.PRICE_COLLECTOR_STATE_DIR, 'limits.json'), 'utf8'));
  const startedRun = actorCallTools.has(name) || dedicatedActorTools(limits).includes(name);
  if (startedRun || name === 'mcp__apify__get-actor-run') {
    await recordRun(env.PRICE_COLLECTOR_STATE_DIR, { startedRun, response: event.tool_response });
  }
  return {};
}

function dedicatedActorTools(limits) {
  return Object.keys(limits.actors).flatMap(id => [
    `mcp__apify__${id.replace('/', '--')}`, `mcp__apify__${id.replace('/', '-slash-')}`,
  ]);
}

export async function toolDecision(event, env) {
  const name = event?.tool_name ?? '';
  if (name === 'Bash') {
    if (event.tool_input?.run_in_background || !permittedCommand(event.tool_input?.command)) {
      return decision('deny', 'Only foreground collector helper commands with literal arguments are allowed.');
    }
    const command = event.tool_input.command;
    // A helper's numeric flags cannot turn the small manual sample into an unbounded JB run.
    const fixed = command.startsWith('node scripts/price-collector/jbmetro-lookup.mjs ')
      ? `${command} --page-size 5 --max-pages 1 --batch-size 5 --attempts 1 --timeout-ms 15000` : command;
    return decision('allow', 'Approved collector helper.', { ...event.tool_input, command: fixed });
  }
  if (name !== 'Write' && !name.startsWith('mcp__apify__')) return decision('deny', 'This tool is outside the collector allowlist.');
  const limits = JSON.parse(readFileSync(join(env.PRICE_COLLECTOR_STATE_DIR, 'limits.json'), 'utf8'));
  const dedicated = dedicatedActorTools(limits);
  const tool = name.slice('mcp__apify__'.length);
  if (readTools.has(tool)) return readDecision(tool, event.tool_input, limits, env.PRICE_COLLECTOR_STATE_DIR);
  if (name !== 'Write' && !dedicated.includes(name) && !actorCallTools.has(name)) {
    return decision('deny', 'Only the reviewed actor execution tools are allowed, not arbitrary Apify account tools.');
  }
  if (name === 'Write' && !writeBelongsToSample(event, env.PRICE_COLLECTOR_STATE_DIR)) {
    return decision('deny', 'A result must answer this prepared sample and its run.');
  }
  // The connector's dedicated tools use --, while the original guard knows -slash-.
  // Normalize only exact, reviewed actor names; unknown tools still fail closed.
  const actor = Object.keys(limits.actors).find(id => name === `mcp__apify__${id.replace('/', '--')}`);
  const normalized = actor ? { ...event, tool_name: `mcp__apify__${actor.replace('/', '-slash-')}` } : event;
  const answer = await runGuard({ event: normalized, env, limits });
  return JSON.parse(answer.out);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    let raw = '';
    for await (const chunk of process.stdin) raw += chunk;
    const event = JSON.parse(raw);
    const answer = event.hook_event_name === 'PostToolUse' ? await recordDecision(event, process.env) : await toolDecision(event, process.env);
    process.stdout.write(JSON.stringify(answer));
  } catch {
    // Hook errors cannot become a permissive response or leak the event/credentials.
    process.stdout.write(JSON.stringify(decision('deny', 'Collector policy could not verify this call.')));
    process.exitCode = 2;
  }
}
