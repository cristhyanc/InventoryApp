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
//
// A work item searched with configured phrases (#1138) answers its one file from both searches with
// `--configured-phrases` (see configuredPhrasesOutcome):
//
//   node scripts/price-collector/pagination.mjs --configured-phrases truncated,0,1,1,true \
//     complete,1,1,1,false
//
// Each search is `<outcome>,<rowCount>,<pagesRead>,<pageCap>,<moreAvailable>[,<failureReason>]`.
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

const OUTCOME_VALUES = Object.freeze(Object.values(SEARCH_OUTCOMES));

function checkedPaging(paging, where) {
  if (paging === null || typeof paging !== 'object' || Array.isArray(paging)) {
    throw new Error(`${where} needs a paging record.`);
  }
  notNegativeWholeNumber(paging.pagesRead, `${where} pages-read count`);
  positiveWholeNumber(paging.pageCap, `${where} page cap`);
  if (typeof paging.moreAvailable !== 'boolean') throw new Error(`${where} paging needs moreAvailable.`);
  return { pagesRead: paging.pagesRead, pageCap: paging.pageCap, moreAvailable: paging.moreAvailable };
}

function checkedConfiguredSearch(search, index) {
  const where = `Search ${index + 1}`;
  if (search === null || typeof search !== 'object' || Array.isArray(search)) {
    throw new Error(`${where} must be an object.`);
  }
  if (!OUTCOME_VALUES.includes(search.outcome)) {
    throw new Error(`${where} outcome must be one of: ${OUTCOME_VALUES.join(', ')}.`);
  }
  notNegativeWholeNumber(search.rowCount, `${where} row count`);
  const failed = search.outcome === SEARCH_OUTCOMES.failed;
  if (failed && !FAILURE_REASON_VALUES.includes(search.failureReason)) {
    throw new Error(`${where} failure reason must be one of: ${FAILURE_REASON_VALUES.join(', ')}.`);
  }
  if ((failed || search.outcome === SEARCH_OUTCOMES.noMatches) && search.rowCount > 0) {
    throw new Error(`${where} cannot hold rows with outcome ${search.outcome}.`);
  }
  return {
    outcome: search.outcome,
    rowCount: search.rowCount,
    failureReason: failed ? search.failureReason : null,
    paging: checkedPaging(search.paging, where),
  };
}

/**
 * The one answer a work item gives when it was searched with **configured search phrases** (issue
 * #1138): up to two searches, in phrase order, each already answered on its own by
 * `searchOutcomeFor` / `internalPagingOutcome` (its `paging`, `outcome`, `failureReason` and the
 * number of real-match rows it found).
 *
 * The rule, so the file a work item writes is consistent under the existing result contract:
 *
 * - **The rows** are those of the search that found the real matches - the last search, because
 *   the collector stops at the first one that finds any. `rowsFromSearch` names it (1-based), or is
 *   `null` when no search found one.
 * - **A failed search ends the work item.** The next phrase is not searched after a failure, so a
 *   failure can only be the last search, and the item is `failed` with that reason and no rows.
 * - **`truncated` when any search was cut short**, whatever the others found: a capped first search
 *   with no match followed by a fully read fallback with a match is `truncated` and keeps the
 *   fallback's rows, because the first search may have held a listing nobody looked at.
 * - **`no_matches` only when every search was read to the end and none found a real match;**
 *   `complete` when every search was read to the end and the last one found the matches.
 * - **`paging` is the paging of the search that decided the outcome**: for `truncated`, the first
 *   search that was cut short (so a truncated item never carries a fallback's
 *   `moreAvailable: false` while its own reason is the earlier search's cap); otherwise the paging of
 *   the search whose rows are reported, or of the last search when there are none.
 */
export function configuredPhrasesOutcome(searches) {
  if (!Array.isArray(searches) || searches.length === 0 || searches.length > 2) {
    throw new Error('Configured phrases are answered from one or two searches.');
  }
  const answered = searches.map((search, index) => checkedConfiguredSearch(search, index));
  const last = answered.length - 1;

  answered.forEach((search, index) => {
    if (index < last && search.outcome === SEARCH_OUTCOMES.failed) {
      throw new Error('No phrase is searched after a failed search, so only the last search can fail.');
    }
    if (index < last && search.rowCount > 0) {
      throw new Error('The next phrase is searched only when no real match was found, so only the last search can hold rows.');
    }
  });

  const final = answered[last];
  if (final.outcome === SEARCH_OUTCOMES.failed) {
    return { outcome: SEARCH_OUTCOMES.failed, failureReason: final.failureReason, paging: final.paging, rowsFromSearch: null };
  }

  const rowsFromSearch = final.rowCount > 0 ? answered.length : null;
  const cutShort = answered.find((search) => search.outcome === SEARCH_OUTCOMES.truncated);
  if (cutShort) {
    return { outcome: SEARCH_OUTCOMES.truncated, failureReason: null, paging: cutShort.paging, rowsFromSearch };
  }

  return {
    outcome: rowsFromSearch === null ? SEARCH_OUTCOMES.noMatches : SEARCH_OUTCOMES.complete,
    failureReason: null,
    paging: final.paging,
    rowsFromSearch,
  };
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

/**
 * One search of `--configured-phrases`, written as
 * `<outcome>,<rowCount>,<pagesRead>,<pageCap>,<moreAvailable>[,<failureReason>]` - plain words and
 * numbers, so the run's command guard accepts it without quoting.
 */
function configuredSearchFromToken(token, index) {
  const parts = token.split(',');
  if (parts.length < 5 || parts.length > 6 || !['true', 'false'].includes(parts[4])) {
    throw new Error(
      `Search ${index + 1} must be written <outcome>,<rowCount>,<pagesRead>,<pageCap>,<moreAvailable>[,<failureReason>].`);
  }
  const number = (raw) => (/^\d+$/.test(raw) ? Number(raw) : Number.NaN);
  return {
    outcome: parts[0],
    rowCount: number(parts[1]),
    paging: { pagesRead: number(parts[2]), pageCap: number(parts[3]), moreAvailable: parts[4] === 'true' },
    failureReason: parts[5] ?? null,
  };
}

/** One invocation: the page decision and the outcome it implies, as JSON on stdout. */
export function runPagination(argv) {
  try {
    if (argv?.[0] === '--configured-phrases') {
      const searches = argv.slice(1).map((token, index) => configuredSearchFromToken(String(token), index));
      return { exitCode: 0, out: JSON.stringify(configuredPhrasesOutcome(searches)), err: '' };
    }
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
