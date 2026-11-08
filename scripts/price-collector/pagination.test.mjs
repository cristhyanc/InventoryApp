// Unit tests for scripts/price-collector/pagination.mjs: whether a search has another page to read,
// and which outcome a finished search is reported with (issue #926, parent #817).
//
// No test makes a live call: these are the two rules a run follows, stated as functions so a page
// cap can never be read as "this is everything the supplier sells" (#937).
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
  SEARCH_FAILURE_REASONS,
  SEARCH_OUTCOMES,
  configuredPhrasesOutcome,
  internalPagingOutcome,
  runPagination,
  searchOutcomeFor,
  shouldRequestNextPage,
} from './pagination.mjs';
import { validateResultFile } from './output-schema.mjs';

describe('shouldRequestNextPage', () => {
  it('asks for another page when the actor filled the page', () => {
    const decision = shouldRequestNextPage({ returned: 50, pageSize: 50, itemsSoFar: 50, pagesRead: 1, maxPages: 4 });

    assert.equal(decision.moreAvailable, true);
    assert.equal(decision.requestNextPage, true);
  });

  it('asks for another page when the actor reports more results than it returned', () => {
    const decision = shouldRequestNextPage({
      returned: 20,
      pageSize: 50,
      reportedTotal: 120,
      itemsSoFar: 20,
      pagesRead: 1,
      maxPages: 4,
    });

    assert.equal(decision.moreAvailable, true);
    assert.equal(decision.requestNextPage, true);
  });

  it('stops when a short page accounts for everything the actor reported', () => {
    const decision = shouldRequestNextPage({
      returned: 12,
      pageSize: 50,
      reportedTotal: 12,
      itemsSoFar: 12,
      pagesRead: 1,
      maxPages: 4,
    });

    assert.equal(decision.moreAvailable, false);
    assert.equal(decision.requestNextPage, false);
  });

  it('stops when a short page is all the actor returned and it reported no total', () => {
    const decision = shouldRequestNextPage({ returned: 3, pageSize: 50, itemsSoFar: 3, pagesRead: 1, maxPages: 4 });

    assert.equal(decision.moreAvailable, false);
    assert.equal(decision.requestNextPage, false);
  });

  it('keeps moreAvailable true at the page cap, and asks for no further page', () => {
    const decision = shouldRequestNextPage({ returned: 50, pageSize: 50, itemsSoFar: 200, pagesRead: 4, maxPages: 4 });

    assert.equal(decision.moreAvailable, true);
    assert.equal(decision.requestNextPage, false);
  });

  it('refuses a page size or page cap that is not a positive whole number', () => {
    assert.throws(() => shouldRequestNextPage({ returned: 1, pageSize: 0, itemsSoFar: 1, pagesRead: 1, maxPages: 4 }), /page size/i);
    assert.throws(() => shouldRequestNextPage({ returned: 1, pageSize: 50, itemsSoFar: 1, pagesRead: 1, maxPages: 0 }), /page cap/i);
  });
});

describe('searchOutcomeFor', () => {
  it('reports a search the page cap stopped as truncated', () => {
    const answer = searchOutcomeFor({ pagesRead: 4, maxPages: 4, moreAvailable: true, rowCount: 7 });

    assert.equal(answer.outcome, SEARCH_OUTCOMES.truncated);
    assert.equal(answer.failureReason, null);
  });

  it('reports a fully read search with matches as complete', () => {
    const answer = searchOutcomeFor({ pagesRead: 2, maxPages: 4, moreAvailable: false, rowCount: 3 });

    assert.equal(answer.outcome, SEARCH_OUTCOMES.complete);
  });

  it('reports a fully read search with no match as no_matches', () => {
    const answer = searchOutcomeFor({ pagesRead: 1, maxPages: 4, moreAvailable: false, rowCount: 0 });

    assert.equal(answer.outcome, SEARCH_OUTCOMES.noMatches);
  });

  it('never reports no_matches for a search that stopped at the cap', () => {
    const answer = searchOutcomeFor({ pagesRead: 4, maxPages: 4, moreAvailable: true, rowCount: 0 });

    assert.equal(answer.outcome, SEARCH_OUTCOMES.truncated);
  });

  it('reports a failure with its declared reason, whatever the pages say', () => {
    const answer = searchOutcomeFor({
      pagesRead: 1,
      maxPages: 4,
      moreAvailable: true,
      rowCount: 2,
      failure: SEARCH_FAILURE_REASONS.timeout,
    });

    assert.equal(answer.outcome, SEARCH_OUTCOMES.failed);
    assert.equal(answer.failureReason, SEARCH_FAILURE_REASONS.timeout);
  });

  it('refuses a failure reason that is not one of the three declared ones', () => {
    assert.throws(
      () => searchOutcomeFor({ pagesRead: 1, maxPages: 4, moreAvailable: false, rowCount: 0, failure: 'site_was_rude' }),
      /failure reason/i,
    );
  });
});

describe('internalPagingOutcome (Kmart and Axlymxp BIG W page internally)', () => {
  it('reports a below-limit search without completeness evidence as truncated, not no_matches', () => {
    // The internal page cap, filtering or failed detail requests can return fewer rows than maxItems
    // from a search that did not finish; a short answer alone proves nothing.
    const answer = internalPagingOutcome({ returned: 12, itemLimit: 50, rowCount: 0 });
    assert.deepEqual(answer.paging, { pagesRead: 1, pageCap: 1, moreAvailable: true });
    assert.equal(answer.outcome, SEARCH_OUTCOMES.truncated);
  });

  it('reports a below-limit search with matches but unknown completeness as truncated, not complete', () => {
    const answer = internalPagingOutcome({ returned: 30, itemLimit: 50, completenessProven: false, rowCount: 3 });
    assert.equal(answer.outcome, SEARCH_OUTCOMES.truncated);
  });

  it('reports a search that reached the item limit as truncated even with completeness evidence', () => {
    const answer = internalPagingOutcome({ returned: 50, itemLimit: 50, completenessProven: true, rowCount: 4 });
    assert.equal(answer.paging.moreAvailable, true);
    assert.equal(answer.outcome, SEARCH_OUTCOMES.truncated);
  });

  it('reports complete or no_matches only with positive completeness evidence below the item limit', () => {
    assert.equal(internalPagingOutcome({ returned: 12, itemLimit: 50, completenessProven: true, rowCount: 2 }).outcome, SEARCH_OUTCOMES.complete);
    assert.equal(internalPagingOutcome({ returned: 12, itemLimit: 50, completenessProven: true, rowCount: 0 }).outcome, SEARCH_OUTCOMES.noMatches);
  });

  it('lets a failure win, whatever the paging says', () => {
    const answer = internalPagingOutcome({ returned: 0, itemLimit: 50, rowCount: 0, failure: SEARCH_FAILURE_REASONS.timeout });
    assert.equal(answer.outcome, SEARCH_OUTCOMES.failed);
  });

  it('answers on the command line, treating completeness as unproven unless the flag says otherwise', () => {
    const unproven = runPagination(['--internal-paging', '--returned', '12', '--item-limit', '50', '--row-count', '0']);
    assert.equal(unproven.exitCode, 0);
    assert.equal(JSON.parse(unproven.out).outcome, 'truncated');

    const proven = runPagination(['--internal-paging', '--completeness-proven', '--returned', '12', '--item-limit', '50', '--row-count', '0']);
    assert.equal(JSON.parse(proven.out).outcome, 'no_matches');

    assert.equal(runPagination(['--internal-paging', '--returned', '12']).exitCode, 2);
  });
});

describe('the command line', () => {
  it('answers one page decision and the outcome it implies as JSON', () => {
    const answer = runPagination([
      '--returned', '50',
      '--page-size', '50',
      '--items-so-far', '200',
      '--pages-read', '4',
      '--max-pages', '4',
      '--row-count', '6',
    ]);

    assert.equal(answer.exitCode, 0);
    assert.deepEqual(JSON.parse(answer.out), {
      moreAvailable: true,
      requestNextPage: false,
      outcome: 'truncated',
      failureReason: null,
    });
  });

  it('refuses an unknown option without answering a decision', () => {
    const answer = runPagination(['--guess']);

    assert.equal(answer.exitCode, 2);
    assert.equal(answer.out, '');
    assert.match(answer.err, /--guess/);
  });
});

describe('configuredPhrasesOutcome (issue #1138)', () => {
  const capped = { outcome: 'truncated', rowCount: 0, paging: { pagesRead: 1, pageCap: 1, moreAvailable: true } };
  const exhaustedEmpty = { outcome: 'no_matches', rowCount: 0, paging: { pagesRead: 1, pageCap: 1, moreAvailable: false } };
  const exhaustedMatch = { outcome: 'complete', rowCount: 1, paging: { pagesRead: 1, pageCap: 1, moreAvailable: false } };

  const fileFor = (answer, rowCount) => ({
    runId: 41,
    workItemId: 7001,
    supplierPresetId: 3,
    outcome: answer.outcome,
    ...(answer.failureReason ? { failureReason: answer.failureReason } : {}),
    paging: answer.paging,
    rows: Array.from({ length: rowCount }, (_, index) => ({
      supplierProductId: `jbmetro:${index + 1}`,
      packPrice: 2.5,
      currency: 'AUD',
      priceBasis: 'exclusive_of_gst',
      stockStatus: 'in_stock',
    })),
  });

  it('keeps a successful fallback\'s rows but reports the item truncated, with the capped search\'s paging', () => {
    const answer = configuredPhrasesOutcome([capped, exhaustedMatch]);
    assert.deepEqual(answer, {
      outcome: SEARCH_OUTCOMES.truncated,
      failureReason: null,
      paging: capped.paging,
      rowsFromSearch: 2,
    });
    // The file it describes is valid under the existing result contract.
    const checked = validateResultFile(fileFor(answer, 1));
    assert.equal(checked.ok, true, checked.problems?.join(' | '));
  });

  it('is no_matches only when every search was read to the end and none matched', () => {
    assert.equal(configuredPhrasesOutcome([exhaustedEmpty, exhaustedEmpty]).outcome, SEARCH_OUTCOMES.noMatches);
    assert.equal(configuredPhrasesOutcome([capped, exhaustedEmpty]).outcome, SEARCH_OUTCOMES.truncated);
    assert.equal(configuredPhrasesOutcome([exhaustedEmpty, capped]).outcome, SEARCH_OUTCOMES.truncated);
    assert.deepEqual(configuredPhrasesOutcome([exhaustedEmpty, capped]).paging, capped.paging);
  });

  it('is complete, with the matching search\'s paging, when every search was read to the end', () => {
    const answer = configuredPhrasesOutcome([exhaustedEmpty, exhaustedMatch]);
    assert.equal(answer.outcome, SEARCH_OUTCOMES.complete);
    assert.deepEqual(answer.paging, exhaustedMatch.paging);
    assert.equal(answer.rowsFromSearch, 2);
    assert.equal(configuredPhrasesOutcome([exhaustedMatch]).outcome, SEARCH_OUTCOMES.complete);
  });

  it('ends the item at a failed search and refuses a search after a failure or a match', () => {
    const failed = { outcome: 'failed', failureReason: 'timeout', rowCount: 0, paging: { pagesRead: 0, pageCap: 1, moreAvailable: false } };
    const answer = configuredPhrasesOutcome([capped, failed]);
    assert.equal(answer.outcome, SEARCH_OUTCOMES.failed);
    assert.equal(answer.failureReason, 'timeout');
    assert.equal(answer.rowsFromSearch, null);
    assert.throws(() => configuredPhrasesOutcome([failed, exhaustedMatch]), /only the last search can fail/);
    assert.throws(() => configuredPhrasesOutcome([exhaustedMatch, exhaustedMatch]), /only the last search can hold rows/);
    assert.throws(() => configuredPhrasesOutcome([]), /one or two searches/);
    assert.throws(() => configuredPhrasesOutcome([exhaustedEmpty, exhaustedEmpty, exhaustedEmpty]), /one or two searches/);
    assert.throws(() => configuredPhrasesOutcome([{ ...exhaustedEmpty, rowCount: 2 }]), /cannot hold rows/);
  });

  it('answers the same rule from the command line, in words the run guard accepts', () => {
    const answer = runPagination(['--configured-phrases', 'truncated,0,1,1,true', 'complete,1,1,1,false']);
    assert.equal(answer.exitCode, 0);
    assert.deepEqual(JSON.parse(answer.out), {
      outcome: 'truncated',
      failureReason: null,
      paging: { pagesRead: 1, pageCap: 1, moreAvailable: true },
      rowsFromSearch: 2,
    });
    assert.equal(runPagination(['--configured-phrases', 'complete,1,1,1,maybe']).exitCode, 2);
    assert.equal(runPagination(['--configured-phrases']).exitCode, 2);
  });
});
