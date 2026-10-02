// Unit tests for scripts/sonar-new-issues.mjs: exact-SHA check-run matching, head binding through
// GitHub, the check-run issue count, fail-open behaviour, pagination, and the Markdown list the architecture fix stage hands to the implementing agent.
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, it } from 'node:test';
import {
  MAX_LISTED_ISSUES,
  SONAR_APP_SLUG,
  SONAR_CHECK_NAME,
  collectSonarIssues,
  expectedIssueCount,
  renderIssues,
  writeOutputs,
} from './sonar-new-issues.mjs';

const REPO = 'owner/InventoryApp';
const KEY = 'owner_InventoryApp';
const SHA = '08f3b0a527fe74efc5d0b6fb1895da3452ad5032';
const OTHER_SHA = '24ecdc6c1fc1f23e00bfea0d28b6bc41df03f56d';
// SonarCloud's automatic analysis reports the default branch's commit as a pull request analysis's revision.
const MAIN_SHA = '69c12507e2b0a30a4d943ac37d34ebf0e4188481';
const summary = (count) => `Issues\n![](passed.svg '') [${count} New ${count === 1 ? 'issue' : 'issues'}](https://sonarcloud.io/project/issues?id=${KEY}&pullRequest=285)\n![](accepted.svg '') [0 Accepted issues](https://sonarcloud.io)`;

const checkRun = (overrides = {}) => ({ name: SONAR_CHECK_NAME, status: 'completed', conclusion: 'success', head_sha: SHA, app: { slug: SONAR_APP_SLUG }, ...overrides });
const issue = (n, overrides = {}) => ({
  key: `AZ${n}`, rule: 'csharpsquid:S1172', severity: 'MAJOR', type: 'CODE_SMELL',
  component: `${KEY}:backend/Inventory.Domain/Purchases/Policy${n}.cs`, line: n, message: 'Remove this unused method parameter.',
  impacts: [{ softwareQuality: 'MAINTAINABILITY', severity: 'MEDIUM' }], ...overrides,
});

// A fake fetchJson: GitHub check-run responses and pull request heads are served in order (the last one
// repeats), and SonarCloud issue pages by page number, with the issue list switching to `laterPages`
// after `staleReads` complete reads. Every URL is recorded.
function fakeFetch({ checks = [[checkRun()]], heads = [SHA], pages = [[]], staleReads = 0, laterPages, total, sonarError } = {}) {
  const urls = [];
  let checkCall = 0;
  let headCall = 0;
  let issueReads = 0;
  const fetchJson = async (url, service) => {
    urls.push({ url, service });
    if (service === 'github' && url.includes('/git/ref/pull/')) {
      const head = heads[Math.min(headCall, heads.length - 1)];
      headCall += 1;
      return head === null ? {} : { ref: 'refs/pull/285/head', object: { sha: head, type: 'commit' } };
    }
    if (service === 'github') {
      const runs = checks[Math.min(checkCall, checks.length - 1)];
      checkCall += 1;
      return { check_runs: runs };
    }
    if (sonarError) throw new Error(sonarError);
    if (url.includes('/api/project_analyses/search')) return { analyses: [{ key: 'A1', revision: MAIN_SHA }] };
    const page = Number(new URL(url).searchParams.get('p'));
    const source = issueReads < staleReads ? pages : (laterPages ?? pages);
    const issues = source[page - 1] ?? [];
    const pageTotal = total ?? source.flat().length;
    if (page * 500 >= pageTotal || issues.length === 0) issueReads += 1;
    return { paging: { pageIndex: page, pageSize: 500, total: pageTotal }, issues };
  };
  return { fetchJson, urls };
}

const options = (fetchJson, overrides = {}) => ({
  fetchJson, repository: REPO, sha: SHA, pullRequest: '285', projectKey: KEY, waitSeconds: 60, pollSeconds: 1, wait: async () => {}, ...overrides,
});

describe('SonarCloud issue collection', () => {
  it('reads the open issues of the pull request once the exact-SHA analysis has completed', async () => {
    const { fetchJson, urls } = fakeFetch({ pages: [[issue(1), issue(2)]] });
    const result = await collectSonarIssues(options(fetchJson));
    assert.equal(result.status, 'analysed');
    assert.equal(result.count, 2);
    assert.match(result.markdown, /Policy1\.cs:1/);
    const sonarUrl = new URL(urls.find((u) => u.url.includes('/api/issues/search')).url);
    assert.equal(sonarUrl.origin, 'https://sonarcloud.io');
    assert.equal(sonarUrl.searchParams.get('componentKeys'), KEY);
    assert.equal(sonarUrl.searchParams.get('pullRequest'), '285');
    assert.equal(sonarUrl.searchParams.get('resolved'), 'false');
    assert.match(urls[0].url, new RegExp(`/repos/${REPO}/commits/${SHA}/check-runs\\?`));
  });

  it('waits while the analysis is missing or still running, then reads issues', async () => {
    const { fetchJson, urls } = fakeFetch({ checks: [[], [checkRun({ status: 'in_progress' })], [checkRun()]], pages: [[issue(1)]] });
    const result = await collectSonarIssues(options(fetchJson));
    assert.equal(result.count, 1);
    assert.equal(urls.filter((u) => u.url.includes('/check-runs?')).length, 3);
  });

  it('ignores check runs for another SHA, another name, or another app', async () => {
    for (const run of [checkRun({ head_sha: OTHER_SHA }), checkRun({ name: 'Other' }), checkRun({ app: { slug: 'impostor' } })]) {
      const { fetchJson, urls } = fakeFetch({ checks: [[run]] });
      const result = await collectSonarIssues(options(fetchJson, { waitSeconds: 0 }));
      assert.equal(result.status, 'unavailable');
      assert.equal(result.count, 0);
      assert.ok(!urls.some((u) => u.service === 'sonar'), 'issues must not be read without an exact-SHA analysis');
    }
  });

  it('binds the read to the pull request head before and after it, ignoring SonarCloud\'s analysis revision', async () => {
    const { fetchJson, urls } = fakeFetch({ pages: [[issue(1)]] });
    const result = await collectSonarIssues(options(fetchJson));
    assert.equal(result.status, 'analysed');
    const order = urls.slice(1).map((u) => new URL(u.url).pathname);
    assert.deepEqual(order, [`/repos/${REPO}/git/ref/pull/285/head`, '/api/issues/search', `/repos/${REPO}/git/ref/pull/285/head`]);
    assert.ok(!urls.some((u) => u.url.includes('/api/project_analyses/search')), 'the analysis revision is the default branch commit, not the head');
  });

  it('treats a pull request whose head is no longer this SHA as unavailable and never reads its issues', async () => {
    for (const heads of [[OTHER_SHA], [null]]) {
      const { fetchJson, urls } = fakeFetch({ heads, pages: [[issue(1)]] });
      const result = await collectSonarIssues(options(fetchJson));
      assert.equal(result.status, 'unavailable');
      assert.equal(result.count, 0);
      assert.match(result.markdown, new RegExp(`not ${SHA}`));
      assert.ok(!urls.some((u) => u.url.includes('/api/issues/search')), 'issues of another head must not be read');
    }
  });

  it('treats a head that moved while the issues were read as unavailable', async () => {
    const { fetchJson } = fakeFetch({ heads: [SHA, OTHER_SHA], pages: [[issue(1)]] });
    const result = await collectSonarIssues(options(fetchJson));
    assert.equal(result.status, 'unavailable');
    assert.equal(result.count, 0);
    assert.match(result.markdown, /changed from/);
    assert.ok(!result.markdown.includes('Policy1'), 'issues read during the change must not be handed on');
  });

  it('requires the issue count to match the check run summary, waiting briefly for it to catch up', async () => {
    const { fetchJson } = fakeFetch({ checks: [[checkRun({ output: { summary: summary(2) } })]], staleReads: 1, pages: [[issue(9)]], laterPages: [[issue(1), issue(2)]] });
    const result = await collectSonarIssues(options(fetchJson));
    assert.equal(result.status, 'analysed');
    assert.equal(result.count, 2);
    assert.ok(!result.markdown.includes('Policy9'));
  });

  it('treats an issue count that never matches the check run summary as unavailable', async () => {
    const { fetchJson } = fakeFetch({ checks: [[checkRun({ output: { summary: summary(4) } })]], pages: [[issue(1)]] });
    const result = await collectSonarIssues(options(fetchJson));
    assert.equal(result.status, 'unavailable');
    assert.equal(result.count, 0);
    assert.match(result.markdown, /returned 1 open issues .* reports 4/);
  });

  it('reads the "New issues" count from the SonarCloud check run summary', () => {
    assert.equal(expectedIssueCount(checkRun({ output: { summary: summary(4) } })), 4);
    assert.equal(expectedIssueCount(checkRun({ output: { summary: summary(1) } })), 1);
    assert.equal(expectedIssueCount(checkRun({ output: { summary: summary(0) } })), 0);
    assert.equal(expectedIssueCount(checkRun()), null);
    assert.equal(expectedIssueCount(checkRun({ output: { summary: 'Quality Gate failed' } })), null);
  });

  it('treats a check run that produced no analysis as unavailable', async () => {
    for (const conclusion of ['cancelled', 'timed_out', 'action_required', 'stale', 'skipped', null]) {
      const { fetchJson, urls } = fakeFetch({ checks: [[checkRun({ conclusion })]] });
      const result = await collectSonarIssues(options(fetchJson));
      assert.equal(result.status, 'unavailable', String(conclusion));
      assert.ok(!urls.some((u) => u.service === 'sonar'));
    }
  });

  it('accepts a failed quality gate as a completed analysis', async () => {
    const { fetchJson } = fakeFetch({ checks: [[checkRun({ conclusion: 'failure' })]], pages: [[issue(1)]] });
    assert.equal((await collectSonarIssues(options(fetchJson))).count, 1);
  });

  it('reports a clean analysis as analysed with zero issues', async () => {
    const { fetchJson } = fakeFetch();
    assert.deepEqual(await collectSonarIssues(options(fetchJson)), { status: 'analysed', count: 0, markdown: 'No open SonarCloud issues.' });
  });

  it('follows pagination until every issue is read', async () => {
    const first = Array.from({ length: 500 }, (_, i) => issue(i + 1));
    const { fetchJson } = fakeFetch({ pages: [first, [issue(501)]] });
    const result = await collectSonarIssues(options(fetchJson));
    assert.equal(result.count, 501);
  });

  it('rejects malformed inputs before calling any API', async () => {
    const { fetchJson, urls } = fakeFetch();
    await assert.rejects(collectSonarIssues(options(fetchJson, { sha: 'main' })));
    await assert.rejects(collectSonarIssues(options(fetchJson, { pullRequest: '0' })));
    assert.equal(urls.length, 0);
  });

  it('propagates a SonarCloud API failure so the caller reports it as unavailable', async () => {
    const { fetchJson } = fakeFetch({ sonarError: 'sonar API answered HTTP 503' });
    await assert.rejects(collectSonarIssues(options(fetchJson)), /HTTP 503/);
  });
});

describe('SonarCloud issue rendering', () => {
  it('lists issues sorted by file and line, without project key, newlines or backticks', () => {
    const text = renderIssues([issue(2), issue(1, { message: 'Line one\nline `two`' })], KEY);
    const lines = text.split('\n');
    assert.equal(lines.length, 2);
    assert.match(lines[0], /^- `backend\/Inventory\.Domain\/Purchases\/Policy1\.cs:1` \[csharpsquid:S1172\] \(CODE_SMELL, MAJOR, MAINTAINABILITY\/MEDIUM\): Line one line two$/);
    assert.ok(!text.includes(`${KEY}:`));
  });

  it('caps the list and says how many more exist', () => {
    const many = Array.from({ length: MAX_LISTED_ISSUES + 3 }, (_, i) => issue(i + 1));
    const lines = renderIssues(many, KEY).split('\n');
    assert.equal(lines.length, MAX_LISTED_ISSUES + 1);
    assert.match(lines.at(-1), /and 3 more/);
  });
});

describe('GitHub output', () => {
  it('writes status, count and a delimited multi-line issue list', () => {
    const dir = mkdtempSync(join(tmpdir(), 'sonar-output-'));
    try {
      const path = join(dir, 'output');
      writeOutputs(path, { status: 'analysed', count: 2, markdown: '- a\n- b' });
      const text = readFileSync(path, 'utf8');
      assert.match(text, /^status=analysed\ncount=2\nissues<<(SONAR_ISSUES_[0-9a-f]{32})\n- a\n- b\n\1\n$/);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
