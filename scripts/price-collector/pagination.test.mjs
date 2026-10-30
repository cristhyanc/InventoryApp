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
  runPagination,
  searchOutcomeFor,
  shouldRequestNextPage,
} from './pagination.mjs';

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
