// Submits what a collector run wrote, and is run by the **workflow** rather than by Claude
// (issue #926, parent #817) - after Claude finishes **or stops**, which is why every finished file
// is complete on its own.
//
//   node scripts/price-collector/submit-results.mjs [--dir price-collector-out] [--run-id n] [--batch-size 200]
//
// The order is the design:
//
//   1. **Every file is validated on its own** against ./output.schema.json. A file that is missing,
//      unreadable, misnamed, invalid or about another run is skipped and reported, and loses only
//      its own search.
//   2. **Match files first** (`POST /api/price-collector/matches`, #941), in batches of at most 200
//      proposals, because a waiting key matched earlier in the run is what lets that key's own
//      result rows attach to the master product (#937, same-run key resolution).
//   3. **Then each work item's rows** (`POST /api/price-collector/results`, #937), in batches of at
//      most 200 rows.
//   4. **Then that work item's outcome**, with its total row count - and only after **all** of its
//      batches succeeded. A failed batch deliberately leaves the work item without an outcome: the
//      API accepts an answer only while the declared total equals the rows it received, so finishing
//      a search short would record a listing as not found that nobody looked for.
//   5. **Then the run's completion** (`POST /api/price-collector/runs/{id}/complete`), once, at the
//      end, so every unanswered search becomes `not_searched` rather than silence.
//
// It prints counts and reasons only: the per-outcome search counts, the accepted, sent-to-review,
// pending and rejected row counts with the reasons the API gave, the match outcomes, and the files it
// skipped with why. No price, product name, supplier product id, base URL or token is printed.
import { fileURLToPath } from 'node:url';
import { readFile, readdir, stat } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import {
  ApiError,
  ConfigurationError,
  redactorFor,
  requestJson,
  resolveApi,
} from './collector-api.mjs';
import {
  BARCODE_SCOPES,
  MATCH_CONFIDENCES,
  PRICE_BASES,
  RUN_STATUSES,
  SEARCH_FAILURE_REASON_NUMBERS,
  SEARCH_OUTCOME_NUMBERS,
  SIZE_UNITS,
  STOCK_STATUSES,
  nameFor,
  numberFor,
} from './enums.mjs';
import { validateMatchFile, validateResultFile } from './output-schema.mjs';

/** The most rows or proposals one request may carry (`PriceCollectorResultPolicy.MaxRowsPerBatch`). */
export const MAX_BATCH = 200;

export const DEFAULT_OUTPUT_DIR = 'price-collector-out';

export const RESULTS_PATH = '/api/price-collector/results';
export const MATCHES_PATH = '/api/price-collector/matches';

export const USAGE = 'submit-results.mjs [--dir price-collector-out] [--run-id n] [--batch-size 200]';

const RESULT_FILE_NAME = /^(\d+)\.json$/;
const MATCH_FILE_NAME = /^match-.+\.json$/;

function parseFolderOption(value) {
  if (value === '') throw new ConfigurationError(`--dir needs a folder.\n${USAGE}`);
  return value;
}

function parsePositiveIntegerOption(argument, value) {
  if (!/^[1-9]\d*$/.test(value)) {
    throw new ConfigurationError(`${argument} needs a positive whole number, not "${value}".\n${USAGE}`);
  }
  return Number(value);
}

function parseArguments(argv) {
  const options = { dir: DEFAULT_OUTPUT_DIR, runId: null, batchSize: MAX_BATCH };

  for (let index = 0; index < (argv ?? []).length; index += 1) {
    const argument = String(argv[index]);
    const value = index + 1 < argv.length ? String(argv[index + 1]) : '';
    if (argument === '--dir') {
      options.dir = parseFolderOption(value);
      index += 1;
      continue;
    }
    if (argument === '--run-id' || argument === '--batch-size') {
      options[argument === '--run-id' ? 'runId' : 'batchSize'] = parsePositiveIntegerOption(argument, value);
      index += 1;
      continue;
    }
    throw new ConfigurationError(`Unknown option ${argument}.\n${USAGE}`);
  }

  if (options.batchSize > MAX_BATCH) {
    throw new ConfigurationError(`--batch-size may be at most ${MAX_BATCH}, which is all one request may carry.`);
  }
  return options;
}

/** Splits a list into batches of at most `size`, keeping its order. */
export function batchesOf(values, size) {
  const batches = [];
  for (let index = 0; index < values.length; index += size) batches.push(values.slice(index, index + size));
  return batches;
}

const STOCK_WORDS = new Set(['in_stock', 'out_of_stock', 'unknown']);

/**
 * A row's stock answer is never a reason to drop it (owner decision, 1 November 2026): suppliers'
 * stock signals are unreliable and some publish none, so a missing, null or unrecognised value is
 * submitted as `unknown` and the row is kept.
 */
export function normaliseStockStatus(file) {
  for (const row of Array.isArray(file?.rows) ? file.rows : []) {
    if (row && typeof row === 'object' && !STOCK_WORDS.has(row.stockStatus)) row.stockStatus = 'unknown';
  }
}

/**
 * Every row's observation instant is when its file was written, never a time Claude states
 * (owner decision, 1 November 2026). Claude has no clock: on 1 November it dated JB Metro rows
 * minutes after the submission and the API refused them as observed in the future. Each run reads
 * prices live and Claude writes a search's file as soon as it has read it, so the file's
 * modification time - before the submission, after the run started - is the reading's instant.
 * Whole seconds, rounded down, so it is never later than the file itself.
 */
export function stampObservedAt(file, writtenAt) {
  const instant = new Date(Math.floor(writtenAt.getTime() / 1000) * 1000).toISOString().replace('.000Z', 'Z');
  for (const row of Array.isArray(file?.rows) ? file.rows : []) {
    if (row && typeof row === 'object') row.observedAtUtc = instant;
  }
}

/** One file, read and validated on its own: a result, a match, or a reason it is skipped. */
async function classifyOutputFile(dir, name) {
  if (!RESULT_FILE_NAME.test(name) && !MATCH_FILE_NAME.test(name)) {
    return { skip: 'it is not a result file (<work-item-id>.json) or a match file (match-<key>.json)' };
  }

  let parsed;
  try {
    parsed = JSON.parse(await readFile(join(dir, name), 'utf8'));
  } catch (error) {
    return { skip: `it could not be read as JSON (${String(error?.message ?? error).slice(0, 120)})` };
  }

  const isResult = RESULT_FILE_NAME.test(name);
  if (isResult) {
    normaliseStockStatus(parsed);
    stampObservedAt(parsed, (await stat(join(dir, name))).mtime);
  }
  const checked = isResult ? validateResultFile(parsed) : validateMatchFile(parsed);
  if (!checked.ok) return { skip: checked.problems[0] };

  if (isResult && parsed.workItemId !== Number(RESULT_FILE_NAME.exec(name)[1])) {
    return { skip: `it answers work item ${parsed.workItemId}, which is not the work item its name states` };
  }

  return { isResult, file: parsed };
}

/**
 * Reads and validates every file in the output folder, independently.
 *
 * A file is a result file (`<work-item-id>.json`), a match file (`match-<waiting-key>.json`), or
 * neither - and "neither" is skipped rather than guessed at.
 */
export async function readOutputFiles(dir) {
  const names = (await readdir(dir)).sort((left, right) => {
    const leftWorkItem = RESULT_FILE_NAME.exec(left);
    const rightWorkItem = RESULT_FILE_NAME.exec(right);
    if (leftWorkItem !== null && rightWorkItem !== null) return Number(leftWorkItem[1]) - Number(rightWorkItem[1]);
    return left.localeCompare(right);
  });

  const results = [];
  const matches = [];
  const skipped = [];

  for (const name of names) {
    const classified = await classifyOutputFile(dir, name);
    if (classified.skip !== undefined) {
      skipped.push({ name, reason: classified.skip });
      continue;
    }
    (classified.isResult ? results : matches).push({ name, file: classified.file });
  }

  return { results, matches, skipped };
}

/** The one run being submitted: the argument, else the one run every valid file agrees on. */
function resolveRunId({ runId, results, matches, skipped }) {
  if (runId !== null) {
    for (const entry of [...results, ...matches]) {
      if (entry.file.runId !== runId) skipped.push({ name: entry.name, reason: `it belongs to run ${entry.file.runId}` });
    }
    return {
      runId,
      results: results.filter((entry) => entry.file.runId === runId),
      matches: matches.filter((entry) => entry.file.runId === runId),
    };
  }

  const declared = [...new Set([...results, ...matches].map((entry) => entry.file.runId))];
  if (declared.length === 0) {
    throw new ConfigurationError(
      'No valid file states a run id, so there is no run to complete. Pass --run-id with the run id get-work.mjs printed.',
    );
  }
  if (declared.length > 1) {
    throw new ConfigurationError(
      `The output folder holds files for ${declared.length} different run ids. One folder is one run: pass --run-id to say which.`,
    );
  }
  return { runId: declared[0], results, matches };
}

/** One result row as the API takes it, with the file's own work item and supplier stamped on it. */
function rowFor(file, row) {
  const posted = {
    workItemId: file.workItemId,
    supplierPresetId: file.supplierPresetId,
    supplierProductId: row.supplierProductId,
    packPrice: row.packPrice,
    currency: row.currency,
    priceBasis: numberFor(PRICE_BASES, row.priceBasis, 'A price basis'),
    stockStatus: numberFor(STOCK_STATUSES, row.stockStatus, 'A stock status'),
    observedAtUtc: row.observedAtUtc,
    isPromotion: row.isPromotion === true,
  };

  const copied = ['title', 'variant', 'sizeValue', 'barcode', 'packQuantity', 'regularPackPrice', 'supplierGstRate', 'promotionEndsAtUtc', 'sourceReference', 'sourceUrl'];
  for (const field of copied) {
    if (Object.hasOwn(row, field)) posted[field] = row[field];
  }
  if (Object.hasOwn(row, 'sizeUnit')) posted.sizeUnit = numberFor(SIZE_UNITS, row.sizeUnit, 'A size unit');
  if (Object.hasOwn(row, 'barcodeScope')) posted.barcodeScope = numberFor(BARCODE_SCOPES, row.barcodeScope, 'A barcode scope');

  return posted;
}

/** One match proposal as the API takes it. */
function proposalFor(file) {
  const proposal = {
    waitingKey: file.waitingKey,
    confidence: numberFor(MATCH_CONFIDENCES, file.confidence, 'A confidence'),
    reason: file.reason,
  };
  if (Object.hasOwn(file, 'masterProductId')) {
    proposal.masterProductId = file.masterProductId;
    return proposal;
  }
  proposal.suggestedBrand = file.suggestedBrand;
  proposal.suggestedName = file.suggestedName;
  proposal.suggestedSizeValue = file.suggestedSizeValue;
  proposal.suggestedSizeUnit = numberFor(SIZE_UNITS, file.suggestedSizeUnit, 'A size unit');
  return proposal;
}

/**
 * Counts one outcome list of an answer, keeping each reason and how often it was given, and - when
 * given a list - each row's own decision, so the run log can say what became of every row.
 */
function tally(into, rows, outcome, decisions) {
  for (const row of Array.isArray(rows) ? rows : []) {
    into.count += 1;
    decisions?.push({ outcome, workItemId: row?.workItemId ?? null, supplierProductId: row?.supplierProductId ?? null,
      waitingKey: row?.waitingKey ?? null, masterProductId: row?.masterProductId ?? null, reason: row?.reason ?? null });
    const reason = typeof row?.reason === 'string' && row.reason.trim() !== '' ? row.reason.trim() : null;
    if (reason === null) continue;
    into.reasons.set(reason, (into.reasons.get(reason) ?? 0) + 1);
  }
}

const emptyTally = () => ({ count: 0, reasons: new Map() });

function reasonLines(label, tallied) {
  return [...tallied.reasons.entries()].map(([reason, count]) => `  ${label}: ${reason} × ${count}`);
}

function report({ runId, counted, searchCounts, answered, withoutOutcome, skipped, completion, errorCount, fileCounts }) {
  const lines = [
    `files: results ${fileCounts.results}, matches ${fileCounts.matches}, skipped ${skipped.length}`,
    ...skipped.map((entry) => `  skipped ${entry.name}: ${entry.reason}`),
    `matches: auto-matched ${counted.autoMatched.count}, tentative ${counted.tentative.count}, rejected ${counted.matchesRejected.count}`,
    ...reasonLines('tentative', counted.tentative),
    ...reasonLines('match rejected', counted.matchesRejected),
    `searches: complete ${searchCounts.complete}, truncated ${searchCounts.truncated}, no_matches ${searchCounts.no_matches}, failed ${searchCounts.failed}` +
      `; answered ${answered}, left without an outcome ${withoutOutcome}`,
    `rows: accepted ${counted.accepted.count}, sent to review ${counted.sentToReview.count}, pending ${counted.pending.count}, rejected ${counted.rejected.count}`,
    ...reasonLines('sent to review', counted.sentToReview),
    ...reasonLines('pending', counted.pending),
    ...reasonLines('rejected', counted.rejected),
  ];

  if (completion === null) {
    lines.push(`run ${runId}: not completed`);
  } else {
    lines.push(
      `run ${runId}: ${nameFor(RUN_STATUSES, completion.status) ?? 'unknown status'}` +
        `; complete ${completion.completeSearchCount}, truncated ${completion.truncatedSearchCount}` +
        `, no_matches ${completion.noMatchesSearchCount}, failed ${completion.failedSearchCount}` +
        `, not_searched ${completion.notSearchedCount}`,
    );
  }

  lines.push(`failed requests: ${errorCount}`);
  return `${lines.join('\n')}\n`;
}

/** Parses, resolves the API and reads the output folder, or throws the one reason it cannot run. */
async function loadSubmission({ argv, env }) {
  const options = parseArguments(argv);
  const api = resolveApi(env);
  const dir = resolve(options.dir);

  let found;
  try {
    found = await readOutputFiles(dir);
  } catch (error) {
    throw new ConfigurationError(`The output folder ${options.dir} could not be read (${String(error?.code ?? error?.message)}).`);
  }

  const resolved = resolveRunId({ runId: options.runId, ...found, skipped: found.skipped });
  return { options, api, found, resolved };
}

/** The matches, before any result row: a key matched now is a key whose rows can attach to its
 *  master product in this same run (#937). */
async function submitMatches({ matches, runId, batchSize, post, record, counted }) {
  for (const batch of batchesOf(matches, batchSize)) {
    try {
      const { body } = await post(MATCHES_PATH, { runId, proposals: batch.map((entry) => proposalFor(entry.file)) });
      tally(counted.autoMatched, body?.autoMatched, 'auto-matched', counted.decisions);
      tally(counted.tentative, body?.tentative, 'tentative', counted.decisions);
      tally(counted.matchesRejected, body?.rejected, 'match rejected', counted.decisions);
    } catch (error) {
      record(error);
    }
  }
}

/** One work item's rows, then its outcome - the outcome only after every batch of it succeeded. */
async function submitResultFile({ file, runId, batchSize, post, record, counted, searchCounts }) {
  searchCounts[file.outcome] += 1;

  for (const batch of batchesOf(file.rows, batchSize)) {
    try {
      const { body } = await post(RESULTS_PATH, { runId, rows: batch.map((row) => rowFor(file, row)) });
      tally(counted.accepted, body?.accepted, 'accepted', counted.decisions);
      tally(counted.sentToReview, body?.sentToReview, 'sent to review', counted.decisions);
      tally(counted.pending, body?.pending, 'pending', counted.decisions);
      tally(counted.rejected, body?.rejected, 'rejected', counted.decisions);
    } catch (error) {
      record(error);
      return false;
    }
  }

  try {
    await post(`/api/price-collector/work-items/${file.workItemId}/outcome`, {
      runId,
      outcome: numberFor(SEARCH_OUTCOME_NUMBERS, file.outcome, 'A search outcome'),
      failureReason: Object.hasOwn(file, 'failureReason')
        ? numberFor(SEARCH_FAILURE_REASON_NUMBERS, file.failureReason, 'A failure reason')
        : null,
      // Distinct supplier product ids: the schema refuses a file that reports one twice.
      submittedRowCount: file.rows.length,
    });
    return true;
  } catch (error) {
    record(error);
    return false;
  }
}

/** Each work item's rows, then its outcome - and the outcome only after every batch of it
 *  succeeded. */
async function submitResults({ results, runId, batchSize, post, record, counted, searchCounts }) {
  let answered = 0;
  let withoutOutcome = 0;
  for (const entry of results) {
    const wasAnswered = await submitResultFile({ file: entry.file, runId, batchSize, post, record, counted, searchCounts });
    if (wasAnswered) answered += 1;
    else withoutOutcome += 1;
  }
  return { answered, withoutOutcome };
}

/** One invocation. Exits 0 only when every file was submitted and every request succeeded. */
export async function runSubmission({ argv = [], env = {}, fetchImpl = fetch } = {}) {
  let options;
  let api;
  let found;
  let resolved;
  try {
    ({ options, api, found, resolved } = await loadSubmission({ argv, env }));
  } catch (error) {
    return { exitCode: 2, out: '', err: `${String(error?.message ?? error)}\n` };
  }

  const redact = redactorFor(api);
  const { runId } = resolved;
  const counted = {
    accepted: emptyTally(),
    sentToReview: emptyTally(),
    pending: emptyTally(),
    rejected: emptyTally(),
    autoMatched: emptyTally(),
    tentative: emptyTally(),
    matchesRejected: emptyTally(),
    decisions: [],
  };
  const searchCounts = { complete: 0, truncated: 0, no_matches: 0, failed: 0 };
  const errors = [];

  const post = (path, body) => requestJson({ api, method: 'POST', path, body, fetchImpl });
  const record = (error) => {
    errors.push(error instanceof ApiError ? error.message : redact(String(error?.message ?? error)));
  };

  // 1. The matches, before any result row.
  await submitMatches({ matches: resolved.matches, runId, batchSize: options.batchSize, post, record, counted });

  // 2. Each work item's rows, then its outcome.
  const { answered, withoutOutcome } = await submitResults({
    results: resolved.results,
    runId,
    batchSize: options.batchSize,
    post,
    record,
    counted,
    searchCounts,
  });

  // 3. The run, once, whatever happened above: an unanswered search is `not_searched`, and silence
  //    would leave the run open until it expired.
  let completion = null;
  try {
    const { body } = await post(`/api/price-collector/runs/${runId}/complete`, null);
    completion = body;
  } catch (error) {
    record(error);
  }

  const out = report({
    runId,
    counted,
    searchCounts,
    answered,
    withoutOutcome,
    skipped: found.skipped,
    completion,
    errorCount: errors.length,
    fileCounts: { results: resolved.results.length, matches: resolved.matches.length },
  });

  return {
    exitCode: errors.length === 0 && found.skipped.length === 0 ? 0 : 1,
    out,
    err: errors.length === 0 ? '' : `${errors.join('\n')}\n`,
    // Every row and match the API decided, for the run log; the CLI does not print it.
    decisions: counted.decisions,
  };
}

async function main() {
  const { exitCode, out, err } = await runSubmission({ argv: process.argv.slice(2), env: process.env });
  if (out !== '') process.stdout.write(out);
  if (err !== '') process.stderr.write(err);
  process.exitCode = exitCode;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
