// The collector's spending and output guard (issue #926, parent #817), as a Claude Code `PreToolUse`
// hook:
//
//   cat event.json | node scripts/price-collector/apify-guard.mjs
//
// **The caps are not Claude's to obey.** Every Apify MCP tool call passes through this process
// first, and it:
//
//   * allows only the actors `apify-limits.json` lists **and marks `actorIdConfirmed: true`**,
//     resolved from the call's own actor field or from a per-actor tool name - an actor it cannot
//     read, does not list, or whose id and input field names are still to be confirmed in the
//     owner's dry run is refused rather than allowed;
//   * injects the actor's `maxTotalChargeUsd` and item limit when the call carries neither, keeps a
//     smaller request, and blocks a larger one; the same for the internal page cap of an actor that
//     pages internally (`pageLimitField`), up to its `maxPages`;
//   * forces off what is billed and unread - Kmart reviews and local-stock enrichment, Varnmill paid
//     details, and the Varnmill `onlyChanged` watcher, because every listing must get a positive
//     check (#925);
//   * counts the actor calls of this run and the charge they have committed to, in its own state
//     file, and blocks the call that would pass either per-run cap. The charge counted is the
//     **ceiling each call commits to**, which is the only figure available before a call runs, so it
//     is the worst case and never an estimate of what Apify billed.
//
// **A paid call is reserved before it is allowed, and the accounting is never assumed.** The read,
// the comparison against the per-run caps and the write are one operation inside a cross-process
// lock, so two hook invocations cannot read the same remaining allowance and both spend it. An
// accounting file that is not there yet is the one legitimate initialization: a run that has made no
// call. An accounting that cannot be read, is corrupt, cannot be locked or cannot be written
// durably refuses the paid call instead of starting the budget again - the per-call ceilings are not
// a substitute for the run's cap.
//
// **And it is what makes a result file write-once.** A `Write` (or an `Edit`, which is always a
// rewrite) is allowed only inside the output folder and only to a file that does not exist yet, so a
// finished search's file cannot be changed by a later part of the run and the guard's own accounting
// cannot be written over.
//
// Reaching a cap stops the run rather than overspending: the searches it never reached are recorded
// as `not_searched` when the workflow completes the run (#937).
import { closeSync, existsSync, fsyncSync, mkdirSync, openSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
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
const LOCK_DIR_SUFFIX = '.lock';

/** The shape of the accounting file this guard writes, and the only one it reads back. */
export const STATE_SCHEMA_VERSION = 1;

/** How long one hook invocation waits for another to finish reserving before it refuses. */
const LOCK_RETRY_DELAY_MS = 50;
const LOCK_RETRY_LIMIT = 100;

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
    const candidate = stripped.replaceAll('-slash-', '/').trim().toLowerCase();
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

/**
 * The internal page cap this call may ask for, for an actor that pages internally and names the
 * field (`pageLimitField`): a smaller positive request is kept, a missing one is set to the actor's
 * `maxPages`, and a larger, zero (which these actors read as "every page") or malformed one is refused.
 */
function pageLimit({ container, actor, actorId }) {
  const field = actor.pageLimitField;
  if (field === undefined) return { value: undefined, injected: false };
  if (!Object.hasOwn(container, field)) return { value: actor.maxPages, injected: true };

  const asked = container[field];
  if (!Number.isInteger(asked) || asked <= 0) {
    return { refusal: `${field} must be a positive whole number of pages.` };
  }
  if (asked > actor.maxPages) {
    return { refusal: `${field} may be at most ${actor.maxPages} for ${actorId}, not ${asked}.` };
  }
  return { value: asked, injected: false };
}

/** The input field a limit sets, or nothing for a limit the actor does not have. */
const limitFields = (field, limit) => (field === undefined ? {} : { [field]: limit.value });

/** How the reason names a limit: set by the guard, or the call's own accepted. */
const describeLimit = (field, limit) => (limit.injected ? `${field} set to ${limit.value}` : `${field} ${limit.value} accepted`);

/** The run's own caps, which a paid call is never allowed without. */
function perRunCaps(limits) {
  const caps = limits.perRun;
  const stated =
    isPlainObject(caps) &&
    Number.isInteger(caps.maxActorCalls) &&
    caps.maxActorCalls > 0 &&
    isPositiveNumber(caps.maxTotalChargeUsd);

  return stated
    ? { caps }
    : { refusal: 'apify-limits.json does not state this run\'s own call and charge caps, so a paid call is refused rather than allowed.' };
}

function decideApifyCall({ toolName, toolInput, limits, state }) {
  const perRun = perRunCaps(limits);
  if (perRun.refusal !== undefined) return deny(perRun.refusal, state);

  const { field: containerField, input: container } = inputContainer(toolInput, limits);
  const actorId = resolveActorId({ toolName, toolInput, container, limits });

  if (actorId === null) {
    return deny('the actor this call would run could not be read from it, so it is refused rather than allowed.', state);
  }
  const actor = limits.actors[actorId];
  if (actor === undefined) {
    return deny(`actor "${actorId}" is not one of the actors apify-limits.json allows.`, state);
  }
  // Configured is not confirmed. An actor id and its input field names that nobody has verified
  // against the real Apify actor are a guess, and a guess is not something to spend money on: the
  // call is refused, consuming none of this run's budget, until the owner's dry run confirms them.
  if (actor.actorIdConfirmed !== true) {
    return deny(
      `actor "${actorId}" is configured but its id and input fields are not confirmed (actorIdConfirmed is not true), so it is refused until they are.`,
      state,
    );
  }

  if (state.actorCalls >= perRun.caps.maxActorCalls) {
    return deny(`this run has already made ${state.actorCalls} actor calls, which is its cap.`, state);
  }

  const charge = chargeCeiling({ container, limits, actor, actorId });
  if (charge.refusal !== undefined) return deny(charge.refusal, state);

  const items = itemLimit({ container, actor, actorId });
  if (items.refusal !== undefined) return deny(items.refusal, state);

  const pages = pageLimit({ container, actor, actorId });
  if (pages.refusal !== undefined) return deny(pages.refusal, state);

  if (state.chargeUsd + charge.value > perRun.caps.maxTotalChargeUsd) {
    return deny(
      `this call would commit this run to more than its charge cap of ${perRun.caps.maxTotalChargeUsd} US dollars.`,
      state,
    );
  }

  const fixed = {
    ...container,
    [limits.chargeField]: charge.value,
    [actor.itemLimitField]: items.value,
    ...limitFields(actor.pageLimitField, pages),
    // Forced, not defaulted: an enrichment nothing reads must be off however the call asked for it.
    ...actor.forcedInput,
  };
  const updatedInput =
    containerField === null ? { ...toolInput, ...fixed } : { ...toolInput, [containerField]: fixed };

  const reasons = [
    describeLimit(limits.chargeField, charge),
    describeLimit(actor.itemLimitField, items),
    ...(actor.pageLimitField === undefined ? [] : [describeLimit(actor.pageLimitField, pages)]),
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

const problemOf = (error) => (typeof error?.code === 'string' ? error.code : 'unreadable');

/**
 * This run's accounting as it is stored, or why it cannot be trusted. A file that is not there yet
 * is the one legitimate initialization - a run that has made no call. An accounting that cannot be
 * read, is not JSON, or is not of the shape this guard writes is **corrupt**, and corrupt is never
 * read as an empty budget: the caller refuses the paid call instead.
 */
function readState(path) {
  let body = null;
  try {
    body = readFileSync(path, 'utf8');
  } catch (error) {
    if (problemOf(error) === 'ENOENT') return { status: 'initial', state: emptyState() };
    return { status: 'unverifiable', problem: `it could not be read (${problemOf(error)})` };
  }

  let stored = null;
  try {
    stored = JSON.parse(body);
  } catch {
    return { status: 'unverifiable', problem: 'what it holds is not JSON' };
  }

  if (!isPlainObject(stored) || stored.schemaVersion !== STATE_SCHEMA_VERSION) {
    return { status: 'unverifiable', problem: 'what it holds is not the accounting this guard writes' };
  }
  if (!Number.isInteger(stored.actorCalls) || stored.actorCalls < 0) {
    return { status: 'unverifiable', problem: 'the call count it holds is not a whole number of calls' };
  }
  if (!Number.isFinite(stored.chargeUsd) || stored.chargeUsd < 0) {
    return { status: 'unverifiable', problem: 'the committed charge it holds is not an amount' };
  }

  return {
    status: 'stored',
    state: {
      actorCalls: stored.actorCalls,
      chargeUsd: stored.chargeUsd,
      updatedAtUtc: typeof stored.updatedAtUtc === 'string' ? stored.updatedAtUtc : null,
    },
  };
}

/** The reservation, flushed to the disk before the call it pays for is allowed. */
function writeState(path, state, now) {
  mkdirSync(dirname(path), { recursive: true });
  const body = `${JSON.stringify(
    {
      schemaVersion: STATE_SCHEMA_VERSION,
      actorCalls: state.actorCalls,
      chargeUsd: state.chargeUsd,
      updatedAtUtc: now().toISOString(),
    },
    null,
    2,
  )}\n`;

  const handle = openSync(path, 'w');
  try {
    writeFileSync(handle, body, 'utf8');
    fsyncSync(handle);
  } finally {
    closeSync(handle);
  }
}

const sleepFor = (ms) => new Promise((done) => { setTimeout(done, ms); });

/**
 * The cross-process lock the accounting is read, compared and written inside. Creating a directory
 * is one atomic step: exactly one invocation gets it and every other one is told it already exists.
 * A lock that cannot be taken is a refusal, never a reason to proceed unaccounted.
 */
async function acquireLock(path, { sleep = sleepFor, retryLimit = LOCK_RETRY_LIMIT, retryDelayMs = LOCK_RETRY_DELAY_MS } = {}) {
  const lockPath = `${path}${LOCK_DIR_SUFFIX}`;
  try {
    mkdirSync(dirname(path), { recursive: true });
  } catch (error) {
    return { acquired: false, problem: problemOf(error) };
  }

  for (let attempt = 0; ; attempt += 1) {
    try {
      mkdirSync(lockPath);
      return { acquired: true, lockPath };
    } catch (error) {
      if (problemOf(error) !== 'EEXIST') return { acquired: false, problem: problemOf(error) };
      if (attempt >= retryLimit) return { acquired: false, problem: 'another hook invocation is still holding it' };
      await sleep(retryDelayMs);
    }
  }
}

function releaseLock({ lockPath }) {
  try {
    rmSync(lockPath, { recursive: true, force: true });
  } catch {
    // A lock left behind refuses the next paid call, which is the safe direction: it can stop a run
    // and can never let one spend past its cap.
  }
}

/** The run's accounting as it is really kept: one lock, one verified read, one durable write. */
export const fileStateStorage = Object.freeze({ acquireLock, releaseLock, readState, writeState });

/** What this event is, as far as the run's accounting is concerned. */
function classify(event, limits) {
  if (!isPlainObject(event)) return 'unreadable';

  const toolName = String(event.tool_name ?? '');
  if (toolName === '') return 'unreadable';
  if (WRITE_TOOLS.includes(toolName)) return 'write';
  return isApifyTool(toolName, limits) ? 'apify' : 'other';
}

/**
 * One paid call, decided inside the lock on this run's accounting and reserved before it is
 * allowed. Nothing is written for a refusal: a call that was never made consumes no budget.
 */
async function decidePaidCall({ event, limits, statePath, outputDir, now, sleep, storage }) {
  const lock = await storage.acquireLock(statePath, { sleep });
  if (!lock.acquired) {
    return deny(
      `this run's spending accounting could not be locked (${lock.problem}), so this call is refused rather than allowed.`,
      emptyState(),
    );
  }

  try {
    const accounting = storage.readState(statePath);
    if (accounting.status === 'unverifiable') {
      return deny(
        `this run's spending accounting could not be verified (${accounting.problem}), so this call is refused rather than allowed.`,
        emptyState(),
      );
    }

    const answered = decide({ event, limits, state: accounting.state, outputDir });
    if (answered.decision !== 'allow') return answered;

    try {
      storage.writeState(statePath, answered.state, now);
    } catch (error) {
      return deny(
        `this call could not be reserved against this run's caps (${problemOf(error)}), so it is refused rather than allowed.`,
        accounting.state,
      );
    }

    return answered;
  } finally {
    storage.releaseLock(lock);
  }
}

function respond(answered) {
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

/**
 * One hook invocation. A paid Apify call is decided inside the cross-process lock above and
 * reserved durably before the allow is returned; a write, an unreadable event and every other tool
 * need no accounting at all, so a broken accounting file can never make the write-once folder
 * permissive.
 */
export async function runGuard({
  event,
  env = {},
  limits = loadLimits(),
  now = () => new Date(),
  sleep = sleepFor,
  storage = fileStateStorage,
}) {
  const outputDir = resolve(env[OUTPUT_DIR_ENV] ?? DEFAULT_OUTPUT_DIR);
  const statePath = env[GUARD_STATE_ENV] ?? join(outputDir, STATE_FILE_NAME);

  if (classify(event, limits) !== 'apify') {
    return respond(decide({ event, limits, state: emptyState(), outputDir }));
  }

  return respond(await decidePaidCall({ event, limits, statePath, outputDir, now, sleep, storage }));
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
