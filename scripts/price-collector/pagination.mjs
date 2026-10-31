// The two search-completeness rules of issue #926 (parent #817), as functions rather than as a
// paragraph of a prompt:
//
//   1. **Is there another page?** A search asks the actor for at most the configured page size. If
//      the actor filled that page, or reports more results than it has returned, there is more to
//      read - and the next page is requested, up to the configured page cap.
//   2. **What is this search's outcome?** A search the page cap stopped is `truncated`, never
//      `complete` and never `no_matches`, because the backend records a known listing as not found
//      only from a search that saw everything (#937). A search that failed is `failed` with one of
//      three declared reasons, whatever its pages say.
//
// It stores nothing, calls nothing and reads no configuration: the page size and the page cap are
// the caller's, from `apify-limits.json`. The collector prompt cites this file, and a run may ask it
// directly:
//
//   node scripts/price-collector/pagination.mjs --returned 50 --page-size 50 --items-so-far 200 \
//     --pages-read 4 --max-pages 4 --row-count 6
//
// An actor that pages internally (Kmart, Axlymxp BIG W) is one call per search, answered with
// `--internal-paging`; it is complete only with `--completeness-proven` (see internalPagingOutcome):
//
//   node scripts/price-collector/pagination.mjs --internal-paging --returned 12 --item-limit 50 \
//     --row-count 2
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

/** The four answers a collector may give for one search, as they are written in an output file. */
export const SEARCH_OUTCOMES = Object.freeze({
  complete: 'complete',
  truncated: 'truncated',
  noMatches: 'no_matches',
  failed: 'failed',
});

/** The three reasons a failed search may carry, as they are written in an output file. */
export const SEARCH_FAILURE_REASONS = Object.freeze({
  actorError: 'actor_error',
  providerLimit: 'provider_limit',
  timeout: 'timeout',
});

const FAILURE_REASON_VALUES = Object.freeze(Object.values(SEARCH_FAILURE_REASONS));

function positiveWholeNumber(value, what) {
  if (!Number.isInteger(value) || value <= 0) throw new Error(`The ${what} must be a positive whole number.`);
  return value;
}

function notNegativeWholeNumber(value, what) {
  if (!Number.isInteger(value) || value < 0) throw new Error(`The ${what} must be a whole number of zero or more.`);
  return value;
}

/**
 * Whether one more page of this search is to be requested, and whether the actor has more to give.
 *
 * `moreAvailable` is deliberately reported separately from `requestNextPage`: at the page cap they
 * differ, and that difference is exactly what makes the search `truncated` rather than `complete`.
 */
export function shouldRequestNextPage({ returned, pageSize, reportedTotal = null, itemsSoFar, pagesRead, maxPages }) {
  positiveWholeNumber(pageSize, 'page size');
  positiveWholeNumber(maxPages, 'page cap');
  notNegativeWholeNumber(returned, 'returned count');
  notNegativeWholeNumber(itemsSoFar, 'items-so-far count');
  notNegativeWholeNumber(pagesRead, 'pages-read count');
  if (reportedTotal !== null) notNegativeWholeNumber(reportedTotal, 'reported total');

  const filledThePage = returned >= pageSize;
  const totalSaysMore = reportedTotal !== null && itemsSoFar < reportedTotal;
  const moreAvailable = filledThePage || totalSaysMore;
  const requestNextPage = moreAvailable && pagesRead < maxPages;

  let reason = 'every result the actor reported has been read';
  if (moreAvailable) {
    reason = requestNextPage ? 'the actor has more results to give' : 'the page cap stopped this search';
  }

  return { moreAvailable, requestNextPage, reason };
}

/**
 * The outcome one finished search is reported with. A failure wins; then a search stopped at the cap
 * is truncated; then a fully read search with no match is `no_matches`; otherwise it is `complete`.
 */
export function searchOutcomeFor({ pagesRead, maxPages, moreAvailable, rowCount, failure = null }) {
  positiveWholeNumber(maxPages, 'page cap');
  notNegativeWholeNumber(pagesRead, 'pages-read count');
  notNegativeWholeNumber(rowCount, 'row count');

  if (failure !== null && failure !== undefined) {
    if (!FAILURE_REASON_VALUES.includes(failure)) {
      throw new Error(`The failure reason must be one of: ${FAILURE_REASON_VALUES.join(', ')}.`);
    }
    return { outcome: SEARCH_OUTCOMES.failed, failureReason: failure };
  }

  if (moreAvailable === true && pagesRead >= maxPages) {
    return { outcome: SEARCH_OUTCOMES.truncated, failureReason: null };
  }

  return {
    outcome: rowCount === 0 ? SEARCH_OUTCOMES.noMatches : SEARCH_OUTCOMES.complete,
    failureReason: null,
  };
}

/**
 * The paging record and outcome of one search on an actor that pages **internally** (Kmart,
 * Axlymxp BIG W): one call, whose own page cap the collector cannot see past. Fewer products than
 * the item limit is not evidence that the search finished - the internal page cap, filtering or a
 * failed detail request can all return fewer rows from an unfinished search. So the search counts
 * as finished only when the actor's output or run metadata positively shows it read every result
 * (`completenessProven`) and it returned fewer products than the item limit; anything else, unknown
 * included, is `truncated`, so a missing listing is never treated as gone.
 */
export function internalPagingOutcome({ returned, itemLimit, completenessProven = false, rowCount, failure = null }) {
  positiveWholeNumber(itemLimit, 'item limit');
  notNegativeWholeNumber(returned, 'returned count');

  const moreAvailable = completenessProven !== true || returned >= itemLimit;
  const answered = searchOutcomeFor({ pagesRead: 1, maxPages: 1, moreAvailable, rowCount, failure });
  return { paging: { pagesRead: 1, pageCap: 1, moreAvailable }, ...answered };
}

const NUMERIC_OPTIONS = Object.freeze({
  '--returned': 'returned',
  '--page-size': 'pageSize',
  '--reported-total': 'reportedTotal',
  '--items-so-far': 'itemsSoFar',
  '--pages-read': 'pagesRead',
  '--max-pages': 'maxPages',
  '--row-count': 'rowCount',
  '--item-limit': 'itemLimit',
});

const FLAG_OPTIONS = Object.freeze({
  '--internal-paging': 'internalPaging',
  '--completeness-proven': 'completenessProven',
});

/** The argument after an option, or an empty string when the option is the last argument. */
const valueAfter = (argv, index) => (index + 1 < argv.length ? String(argv[index + 1]) : '');

/** The options each mode cannot answer without. */
function requireModeOptions(values, flags) {
  if (flags.internalPaging) {
    if (values.itemLimit === null) throw new Error('--internal-paging needs --item-limit.');
  } else if (values.pageSize === null || values.maxPages === null) {
    throw new Error('--page-size and --max-pages are required.');
  }
}

function parsePaginationArguments(argv) {
  const values = { returned: 0, pageSize: null, reportedTotal: null, itemsSoFar: 0, pagesRead: 0, maxPages: null, rowCount: 0, itemLimit: null };
  const flags = { internalPaging: false, completenessProven: false };
  let failure = null;

  for (let index = 0; index < argv.length; index += 1) {
    const argument = String(argv[index]);
    if (Object.hasOwn(FLAG_OPTIONS, argument)) {
      flags[FLAG_OPTIONS[argument]] = true;
      continue;
    }
    if (argument === '--failure') {
      failure = valueAfter(argv, index);
      index += 1;
      continue;
    }
    if (!Object.hasOwn(NUMERIC_OPTIONS, argument)) throw new Error(`Unknown option ${argument}.`);
    const raw = valueAfter(argv, index);
    if (!/^\d+$/.test(raw)) throw new Error(`${argument} needs a whole number, not "${raw}".`);
    values[NUMERIC_OPTIONS[argument]] = Number(raw);
    index += 1;
  }

  requireModeOptions(values, flags);
  return { values, flags, failure };
}

function answerInternalPaging(values, flags, failure) {
  const answered = internalPagingOutcome({ ...values, completenessProven: flags.completenessProven, failure });
  return {
    moreAvailable: answered.paging.moreAvailable,
    requestNextPage: false,
    paging: answered.paging,
    outcome: answered.outcome,
    failureReason: answered.failureReason,
  };
}

/** One invocation: the page decision and the outcome it implies, as JSON on stdout. */
export function runPagination(argv) {
  try {
    const { values, flags, failure } = parsePaginationArguments(argv ?? []);
    if (flags.internalPaging) {
      return { exitCode: 0, out: JSON.stringify(answerInternalPaging(values, flags, failure)), err: '' };
    }
    const paging = shouldRequestNextPage(values);
    const answered = searchOutcomeFor({ ...values, moreAvailable: paging.moreAvailable, failure });

    return {
      exitCode: 0,
      out: JSON.stringify({
        moreAvailable: paging.moreAvailable,
        requestNextPage: paging.requestNextPage,
        outcome: answered.outcome,
        failureReason: answered.failureReason,
      }),
      err: '',
    };
  } catch (error) {
    return { exitCode: 2, out: '', err: String(error?.message ?? error) };
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { exitCode, out, err } = runPagination(process.argv.slice(2));
  if (out !== '') process.stdout.write(`${out}\n`);
  if (err !== '') process.stderr.write(`${err}\n`);
  process.exitCode = exitCode;
}
