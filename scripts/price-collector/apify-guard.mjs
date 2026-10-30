// The collector's spending and output guard (issue #926, parent #817), as a Claude Code `PreToolUse`
// hook:
//
//   cat event.json | node scripts/price-collector/apify-guard.mjs
//
// **The caps are not Claude's to obey.** Every Apify MCP tool call passes through this process
// first, and it:
//
//   * allows only the actors `apify-limits.json` lists, resolved from the call's own actor field or
//     from a per-actor tool name - an actor it cannot read is refused rather than allowed;
//   * injects the actor's `maxTotalChargeUsd` and item limit when the call carries neither, keeps a
//     smaller request, and blocks a larger one;
//   * forces off what is billed and unread - Kmart reviews and local-stock enrichment, Varnmill paid
//     details, and the Varnmill `onlyChanged` watcher, because every listing must get a positive
//     check (#925);
//   * counts the actor calls of this run and the charge they have committed to, in its own state
//     file, and blocks the call that would pass either per-run cap. The charge counted is the
//     **ceiling each call commits to**, which is the only figure available before a call runs, so it
//     is the worst case and never an estimate of what Apify billed.
//
// **And it is what makes a result file write-once.** A `Write` (or an `Edit`, which is always a
// rewrite) is allowed only inside the output folder and only to a file that does not exist yet, so a
// finished search's file cannot be changed by a later part of the run and the guard's own accounting
// cannot be written over.
//
// Reaching a cap stops the run rather than overspending: the searches it never reached are recorded
// as `not_searched` when the workflow completes the run (#937).
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

/** Where the guard keeps this run's call count and committed charge. */
export const GUARD_STATE_ENV = 'PRICE_COLLECTOR_GUARD_STATE';

/** The only folder the collector may write into. */
export const OUTPUT_DIR_ENV = 'PRICE_COLLECTOR_OUT_DIR';

export const DEFAULT_OUTPUT_DIR = 'price-collector-out';

const here = dirname(fileURLToPath(import.meta.url));

export const LIMITS_PATH = join(here, 'apify-limits.json');
export const MCP_CONFIG_PATH = join(here, 'apify-mcp.json');

const WRITE_TOOLS = Object.freeze(['Write', 'Edit', 'MultiEdit', 'NotebookEdit']);
const PATH_FIELDS = Object.freeze(['file_path', 'notebook_path', 'path']);
const STATE_FILE_NAME = '.apify-guard-state.json';

const isPlainObject = (value) => typeof value === 'object' && value !== null && !Array.isArray(value);

/** The reviewed Apify rules, as shipped beside this module. */
export function loadLimits(path = LIMITS_PATH) {
  return JSON.parse(readFileSync(path, 'utf8'));
}

/** The MCP server document, with the actors it offers read out of its own URL. */
export function loadMcpConfig(path = MCP_CONFIG_PATH) {
  const config = JSON.parse(readFileSync(path, 'utf8'));
  const url = new URL(config.mcpServers.apify.url);
  const actors = (url.searchParams.get('actors') ?? '')
    .split(',')
    .map((entry) => entry.trim())
    .filter((entry) => entry !== '');
  return { config, actors };
}

/** One collector run's accounting, before it has made a call. */
export const emptyState = () => ({ actorCalls: 0, chargeUsd: 0, updatedAtUtc: null });

const allow = (state, { reason, updatedInput }) => ({ decision: 'allow', reason, updatedInput, state });
const deny = (reason, state) => ({ decision: 'deny', reason, updatedInput: undefined, state });
const pass = (state) => ({ decision: 'pass', reason: '', updatedInput: undefined, state });

const isApifyTool = (toolName, limits) =>
  (limits.toolNamePatterns ?? []).some((pattern) => new RegExp(pattern).test(toolName));

/** The object the actor's own input lives in: a named container, or the tool input itself. */
function inputContainer(toolInput, limits) {
  for (const field of limits.inputContainerFields ?? []) {
    if (isPlainObject(toolInput?.[field])) return { field, input: toolInput[field] };
  }
  return { field: null, input: toolInput ?? {} };
}

/** The actor id this call would run: its own actor field, or the per-actor tool name. */
function resolveActorId({ toolName, toolInput, container, limits }) {
  for (const source of [toolInput, container]) {
    for (const field of limits.actorInputFields ?? []) {
      const value = source?.[field];
      if (typeof value === 'string' && value.trim() !== '') return value.trim().toLowerCase();
    }
  }

  for (const pattern of limits.toolNamePatterns ?? []) {
    const stripped = toolName.replace(new RegExp(pattern), '');
    const candidate = stripped.replace(/-slash-/g, '/').trim().toLowerCase();
    if (candidate !== '' && Object.hasOwn(limits.actors, candidate)) return candidate;
  }

  return null;
}

const isPositiveNumber = (value) => typeof value === 'number' && Number.isFinite(value) && value > 0;

/** The charge ceiling this call may commit to: its own if it asked for less, else the actor's. */
function chargeCeiling({ container, limits, actor, actorId }) {
  const field = limits.chargeField;
  if (!Object.hasOwn(container, field)) return { value: actor.maxTotalChargeUsd, injected: true };

  const asked = container[field];
  if (!isPositiveNumber(asked)) {
    return { refusal: `${field} must be a positive number of US dollars.` };
  }
  if (asked > actor.maxTotalChargeUsd) {
    return { refusal: `${field} may be at most ${actor.maxTotalChargeUsd} for ${actorId}, not ${asked}.` };
  }
  return { value: asked, injected: false };
}

/** The item limit this call may ask for: its own if it asked for less, else the actor's page size. */
function itemLimit({ container, actor, actorId }) {
  const field = actor.itemLimitField;
  if (!Object.hasOwn(container, field)) return { value: actor.pageSize, injected: true };

  const asked = container[field];
  if (!Number.isInteger(asked) || asked <= 0) {
    return { refusal: `${field} must be a positive whole number of items.` };
  }
  if (asked > actor.pageSize) {
    return { refusal: `${field} may be at most ${actor.pageSize} for ${actorId}, not ${asked}. Read the next page instead.` };
  }
  return { value: asked, injected: false };
}

function decideApifyCall({ toolName, toolInput, limits, state }) {
  const { field: containerField, input: container } = inputContainer(toolInput, limits);
  const actorId = resolveActorId({ toolName, toolInput, container, limits });

  if (actorId === null) {
    return deny('the actor this call would run could not be read from it, so it is refused rather than allowed.', state);
  }
  const actor = limits.actors[actorId];
  if (actor === undefined) {
    return deny(`actor "${actorId}" is not one of the actors apify-limits.json allows.`, state);
  }

  if (state.actorCalls >= limits.perRun.maxActorCalls) {
    return deny(`this run has already made ${state.actorCalls} actor calls, which is its cap.`, state);
  }

  const charge = chargeCeiling({ container, limits, actor, actorId });
  if (charge.refusal !== undefined) return deny(charge.refusal, state);

  const items = itemLimit({ container, actor, actorId });
  if (items.refusal !== undefined) return deny(items.refusal, state);

  if (state.chargeUsd + charge.value > limits.perRun.maxTotalChargeUsd) {
    return deny(
      `this call would commit this run to more than its charge cap of ${limits.perRun.maxTotalChargeUsd} US dollars.`,
      state,
    );
  }

  const fixed = {
    ...container,
    [limits.chargeField]: charge.value,
    [actor.itemLimitField]: items.value,
    // Forced, not defaulted: an enrichment nothing reads must be off however the call asked for it.
    ...(actor.forcedInput ?? {}),
  };
  const updatedInput =
    containerField === null ? { ...toolInput, ...fixed } : { ...toolInput, [containerField]: fixed };

  const reasons = [
    charge.injected ? `${limits.chargeField} set to ${charge.value}` : `${limits.chargeField} ${charge.value} accepted`,
    items.injected ? `${actor.itemLimitField} set to ${items.value}` : `${actor.itemLimitField} ${items.value} accepted`,
  ];

  return allow(
    {
      actorCalls: state.actorCalls + 1,
      chargeUsd: state.chargeUsd + charge.value,
      updatedAtUtc: state.updatedAtUtc,
    },
    { reason: `${actorId}: ${reasons.join(', ')}.`, updatedInput },
  );
}

function decideWrite({ toolInput, state, outputDir, exists }) {
  const field = PATH_FIELDS.find((candidate) => typeof toolInput?.[candidate] === 'string' && toolInput[candidate] !== '');
  if (field === undefined) {
    return deny('the file this call would write could not be read from it, so it is refused rather than allowed.', state);
  }

  const target = resolve(toolInput[field]);
  const folder = resolve(outputDir);
  if (!target.startsWith(`${folder}${sep}`)) {
    return deny(`the collector may write only inside ${folder}, and not to ${basename(target)}.`, state);
  }
  if (basename(target).startsWith('.')) {
    return deny(`${basename(target)} is the guard's own accounting or a hidden file, which the collector may not write.`, state);
  }
  if (exists(target)) {
    return deny(`${basename(target)} already exists, and a finished file is written once and never rewritten.`, state);
  }

  return allow(state, { reason: `${basename(target)} is a new file in the output folder.`, updatedInput: undefined });
}

/**
 * The decision for one `PreToolUse` event. `pass` means the hook has no opinion, and the ordinary
 * permission rules decide; `allow` and `deny` are the hook's own answers. It never fails open: an
 * event it cannot read is denied.
 */
export function decide({ event, limits, state, outputDir, exists = existsSync }) {
  if (!isPlainObject(event)) return deny('the hook could not read the tool call, so it is refused rather than allowed.', state);

  const toolName = String(event.tool_name ?? '');
  const toolInput = isPlainObject(event.tool_input) ? event.tool_input : {};

  if (toolName === '') return deny('the tool call names no tool, so it is refused rather than allowed.', state);
  if (WRITE_TOOLS.includes(toolName)) return decideWrite({ toolInput, state, outputDir, exists });
  if (isApifyTool(toolName, limits)) return decideApifyCall({ toolName, toolInput, limits, state });
  return pass(state);
}

function readState(path) {
  try {
    const stored = JSON.parse(readFileSync(path, 'utf8'));
    return {
      actorCalls: Number.isInteger(stored?.actorCalls) && stored.actorCalls >= 0 ? stored.actorCalls : 0,
      chargeUsd: typeof stored?.chargeUsd === 'number' && stored.chargeUsd >= 0 ? stored.chargeUsd : 0,
      updatedAtUtc: typeof stored?.updatedAtUtc === 'string' ? stored.updatedAtUtc : null,
    };
  } catch {
    // No state file yet, or an unreadable one: this run has committed to nothing it can prove.
    return emptyState();
  }
}

function writeState(path, state, now) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, `${JSON.stringify({ ...state, updatedAtUtc: now().toISOString() }, null, 2)}\n`, 'utf8');
}

/** One hook invocation, with the run's accounting read and written around the decision. */
export async function runGuard({ event, env = {}, limits = loadLimits(), now = () => new Date() }) {
  const outputDir = resolve(env[OUTPUT_DIR_ENV] ?? DEFAULT_OUTPUT_DIR);
  const statePath = env[GUARD_STATE_ENV] ?? join(outputDir, STATE_FILE_NAME);
  const state = readState(statePath);

  const answered = decide({ event, limits, state, outputDir });

  if (answered.decision !== 'pass') {
    try {
      writeState(statePath, answered.state, now);
    } catch {
      // The accounting could not be kept. The decision still stands; a run whose state cannot be
      // written starts from zero on the next call, which the per-call ceilings still bound.
    }
  }

  if (answered.decision === 'pass') return { exitCode: 0, out: '{}' };

  return {
    exitCode: 0,
    out: JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        permissionDecision: answered.decision,
        permissionDecisionReason: answered.reason,
        ...(answered.updatedInput === undefined ? {} : { updatedInput: answered.updatedInput }),
      },
    }),
  };
}

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString('utf8');
}

async function main() {
  let event = null;
  try {
    event = JSON.parse(await readStdin());
  } catch {
    event = null;
  }

  const { exitCode, out } = await runGuard({ event, env: process.env });
  process.stdout.write(`${out}\n`);
  process.exitCode = exitCode;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
