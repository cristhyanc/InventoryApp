// Collects the open SonarCloud issues reported on one pull request at one exact head SHA, so the
// implementing agent can fix them in the architecture stage (docs/automation.md, "Sonar findings").
//
// Deterministic and read-only: it waits for the SonarCloud check run on the exact head SHA, confirms
// that this SHA is still the pull request's head, reads the pull request's open issues from the public
// SonarCloud Web API, and confirms the head again afterwards. The issue search is keyed only by pull
// request number, so it is bound to the head through GitHub, not through SonarCloud's analysis
// revision: SonarCloud's automatic analysis records the default branch's commit as the revision of a
// pull request analysis, so that revision never equals the head (pull requests #293 and #314). The
// completed check run on the head proves SonarCloud analysed it, an unchanged head proves no newer
// push (and so no newer analysis) replaced its results, and the issue count must equal the "New
// issues" count in that check run's summary, so a list that still belongs to an older analysis is
// not handed on.
// It writes `status`, `count` and a Markdown `issues` list to $GITHUB_OUTPUT and never fails the job:
// any problem (no analysis, a failed or mismatched analysis, timeout, API error) is reported as
// status=unavailable with count=0, because SonarCloud is a third party and its outage must not block
// the task. The issue text is data for the agent, not a command.
import { appendFileSync } from 'node:fs';
import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

export const SONAR_CHECK_NAME = 'SonarCloud Code Analysis';
export const SONAR_APP_SLUG = 'sonarqubecloud';
export const MAX_LISTED_ISSUES = 100;
// GitHub check-run conclusions that mean SonarCloud produced an analysis (a failed quality gate is
// still a completed analysis). Anything else (cancelled, timed_out, action_required, stale, skipped)
// means there is no trustworthy analysis of this head.
export const ANALYSED_CONCLUSIONS = new Set(['success', 'failure', 'neutral']);
const ISSUE_READ_ATTEMPTS = 3;
const MAX_MESSAGE_LENGTH = 300;

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

/** Returns the latest completed SonarCloud check run on `sha`, or null while it is missing or running. */
export async function findCompletedSonarCheck({ fetchJson, repository, sha }) {
  const query = new URLSearchParams({ check_name: SONAR_CHECK_NAME, filter: 'latest', per_page: '20' });
  const body = await fetchJson(`https://api.github.com/repos/${repository}/commits/${sha}/check-runs?${query}`, 'github');
  const runs = (body.check_runs ?? []).filter((run) => run.name === SONAR_CHECK_NAME && run.app?.slug === SONAR_APP_SLUG && run.head_sha === sha);
  const completed = runs.filter((run) => run.status === 'completed');
  return completed.length > 0 ? completed[0] : null;
}

/** Returns the current head SHA of the pull request (its refs/pull/<n>/head ref), or ''. */
export async function fetchPullRequestHead({ fetchJson, repository, pullRequest }) {
  const body = await fetchJson(`https://api.github.com/repos/${repository}/git/ref/pull/${pullRequest}/head`, 'github');
  return String(body.object?.sha ?? '').toLowerCase();
}

/** Returns the "N New issues" count from the SonarCloud check run summary, or null when it is absent. */
export function expectedIssueCount(check) {
  const match = /\[(\d+) New issues?\]/i.exec(String(check?.output?.summary ?? ''));
  return match ? Number(match[1]) : null;
}

/** Reads every open SonarCloud issue on the pull request analysis (all of them are new code). */
export async function fetchOpenPullRequestIssues({ fetchJson, projectKey, pullRequest }) {
  const issues = [];
  for (let page = 1; page <= 10; page += 1) {
    const query = new URLSearchParams({
      componentKeys: projectKey,
      pullRequest: String(pullRequest),
      resolved: 'false',
      ps: '500',
      p: String(page),
    });
    const body = await fetchJson(`https://sonarcloud.io/api/issues/search?${query}`, 'sonar');
    issues.push(...(body.issues ?? []));
    const total = body.paging?.total ?? body.total ?? issues.length;
    if (issues.length >= total || (body.issues ?? []).length === 0) break;
  }
  return issues;
}

const clean = (value, limit = MAX_MESSAGE_LENGTH) => {
  const text = String(value ?? '').replace(/[\r\n\t`]+/g, ' ').replace(/\s+/g, ' ').trim();
  return text.length > limit ? `${text.slice(0, limit - 1)}…` : text;
};

/** Renders the issues as a stable Markdown list: one bullet per issue, file and line first. */
export function renderIssues(issues, projectKey) {
  const sorted = [...issues].sort((a, b) =>
    String(a.component).localeCompare(String(b.component)) || (a.line ?? 0) - (b.line ?? 0) || String(a.key).localeCompare(String(b.key)));
  const lines = sorted.slice(0, MAX_LISTED_ISSUES).map((issue) => {
    const path = clean(String(issue.component ?? '').replace(`${projectKey}:`, ''), 200);
    const location = issue.line ? `${path}:${issue.line}` : path;
    const impacts = (issue.impacts ?? []).map((impact) => `${impact.softwareQuality}/${impact.severity}`).join(', ');
    const kind = [clean(issue.type, 40), clean(issue.severity, 40), clean(impacts, 120)].filter(Boolean).join(', ');
    return `- \`${location}\` [${clean(issue.rule, 80)}] (${kind}): ${clean(issue.message)}`;
  });
  if (sorted.length > MAX_LISTED_ISSUES) lines.push(`- …and ${sorted.length - MAX_LISTED_ISSUES} more; see the SonarCloud pull request page.`);
  return lines.join('\n');
}

/**
 * Waits for the exact-SHA analysis, then returns { status, count, markdown }.
 * status is `analysed` (count may be 0) or `unavailable` (reason in markdown, count 0).
 */
export async function collectSonarIssues({ fetchJson, repository, sha, pullRequest, projectKey, waitSeconds = 900, pollSeconds = 20, wait = sleep }) {
  if (!/^[0-9a-f]{40}$/.test(sha)) throw new Error('A full 40-character lowercase head SHA is required.');
  if (!/^[1-9][0-9]*$/.test(String(pullRequest))) throw new Error('A valid pull request number is required.');

  const deadline = Date.now() + waitSeconds * 1000;
  let check = await findCompletedSonarCheck({ fetchJson, repository, sha });
  while (!check && Date.now() < deadline) {
    await wait(pollSeconds * 1000);
    check = await findCompletedSonarCheck({ fetchJson, repository, sha });
  }
  const unavailable = (why) => ({ status: 'unavailable', count: 0, markdown: why });
  if (!check) return unavailable(`No completed ${SONAR_CHECK_NAME} check run on ${sha} within ${waitSeconds} seconds.`);
  if (!ANALYSED_CONCLUSIONS.has(check.conclusion)) {
    return unavailable(`The ${SONAR_CHECK_NAME} check run on ${sha} did not produce an analysis (conclusion: ${clean(check.conclusion || 'none', 40)}).`);
  }

  // A newer push starts a newer analysis that replaces the pull request's issues, so the head must
  // still be this SHA before the read and after it.
  const headBefore = await fetchPullRequestHead({ fetchJson, repository, pullRequest });
  if (headBefore !== sha) {
    return unavailable(`Pull request #${pullRequest} head is ${clean(headBefore || 'unknown', 40)}, not ${sha}; a newer push owns SonarCloud's results.`);
  }

  // The check run can be posted a moment before the issues are queryable, so allow a short wait for the
  // count to match the check run's summary; a different count after that is an older or newer analysis.
  const expected = expectedIssueCount(check);
  let issues = await fetchOpenPullRequestIssues({ fetchJson, projectKey, pullRequest });
  for (let attempt = 1; attempt < ISSUE_READ_ATTEMPTS; attempt += 1) {
    if (expected === null || issues.length === expected) break;
    await wait(pollSeconds * 1000);
    issues = await fetchOpenPullRequestIssues({ fetchJson, projectKey, pullRequest });
  }
  if (expected !== null && issues.length !== expected) {
    return unavailable(`SonarCloud returned ${issues.length} open issues for pull request #${pullRequest}, but its check run on ${sha} reports ${expected}.`);
  }

  const headAfter = await fetchPullRequestHead({ fetchJson, repository, pullRequest });
  if (headAfter !== sha) {
    return unavailable(`Pull request #${pullRequest} head changed from ${sha} to ${clean(headAfter || 'unknown', 40)} while SonarCloud's issues were read.`);
  }

  return { status: 'analysed', count: issues.length, markdown: issues.length > 0 ? renderIssues(issues, projectKey) : 'No open SonarCloud issues.' };
}

function makeFetchJson({ githubToken, sonarToken }) {
  return async (url, service) => {
    const headers = { Accept: 'application/json', 'User-Agent': 'inventoryapp-agent-sonar' };
    if (service === 'github' && githubToken) headers.Authorization = `Bearer ${githubToken}`;
    if (service === 'sonar' && sonarToken) headers.Authorization = `Bearer ${sonarToken}`;
    const response = await fetch(url, { headers, signal: AbortSignal.timeout(30000) });
    if (!response.ok) throw new Error(`${service} API answered HTTP ${response.status}`);
    return response.json();
  };
}

export function writeOutputs(outputPath, { status, count, markdown }) {
  const delimiter = `SONAR_ISSUES_${randomBytes(16).toString('hex')}`;
  appendFileSync(outputPath, `status=${status}\ncount=${count}\nissues<<${delimiter}\n${markdown}\n${delimiter}\n`);
}

async function main() {
  const env = process.env;
  const repository = env.GITHUB_REPOSITORY ?? '';
  const result = await collectSonarIssues({
    fetchJson: makeFetchJson({ githubToken: env.GH_TOKEN, sonarToken: env.SONAR_TOKEN }),
    repository,
    sha: env.HEAD_SHA ?? '',
    pullRequest: env.PR_NUMBER ?? '',
    projectKey: env.SONAR_PROJECT_KEY || repository.replace('/', '_'),
    waitSeconds: Number(env.SONAR_WAIT_SECONDS || 900),
  }).catch((error) => ({ status: 'unavailable', count: 0, markdown: `SonarCloud issues could not be read: ${clean(error.message)}` }));

  if (env.GITHUB_OUTPUT) writeOutputs(env.GITHUB_OUTPUT, result);
  const log = result.status === 'analysed' ? '::notice::' : '::warning::';
  console.log(`${log}SonarCloud issues at ${env.HEAD_SHA}: status=${result.status}, count=${result.count}`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
