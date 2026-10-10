// One-way display reconciliation. Never labels, closes, starts, approves or merges work.
import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { parseReadinessLabel } from './agent-mode.mjs';

export const PROJECT = Object.freeze({ owner: 'cristhyanc', number: 4, repository: 'cristhyanc/InventoryApp' });
export const STATUSES = ['Backlog', 'Ready', 'In progress', 'In review', 'Changes requested', 'Blocked', 'Awaiting approval', 'Done'];
const MANUAL_ISSUES = new Set([383]); // Checklist epic; native parents are also left manual.

/** null means preserve human planning, rather than reset an unlabelled item to Backlog. */
export function desiredStatus(issue) {
  if (issue.parent || MANUAL_ISSUES.has(issue.number)) return null;
  const labels = new Set(issue.labels);
  const prs = issue.prs.filter(pr => pr.agent && pr.sameRepository && pr.base === 'develop');
  const managed = [...labels].some(label => parseReadinessLabel(label) ||
    ['agent-working', 'agent-review', 'agent-blocked'].includes(label)) || prs.length > 0;
  if (!managed) return null;
  if (issue.state === 'CLOSED') return issue.stateReason === 'COMPLETED' ? 'Done' : null;
  if (labels.has('agent-blocked')) return 'Blocked';
  const ready = [...labels].filter(parseReadinessLabel);
  if (ready.length > 1) return 'Blocked';
  if (ready.length === 1) {
    if (['agent-working', 'agent-review'].some(label => labels.has(label))) return 'Blocked';
    return 'Ready';
  }
  const open = prs.filter(pr => pr.state === 'OPEN');
  if (open.length > 1) return 'Blocked';
  // A merged implementation completes the issue even if its old agent-review label remains.
  if (!open.length && prs.some(pr => pr.state === 'MERGED' &&
      (!issue.reopenedAt || Date.parse(pr.mergedAt) > Date.parse(issue.reopenedAt)))) return 'Done';
  if (labels.has('agent-working')) return 'In progress';
  if (open.length === 1) {
    const pr = open[0];
    if (pr.draft) return 'In progress';
    const status = pr.statuses;
    if (['failure', 'error'].includes(status['agent-review-verdict'])) return 'Changes requested';
    if (['agent-validation', 'merge-validation'].some(key => ['failure', 'error'].includes(status[key]))) return 'Blocked';
    if (['agent-validation', 'merge-validation', 'agent-review-verdict'].every(key => status[key] === 'success')) return 'Awaiting approval';
    return 'In review';
  }
  if (issue.reopenedAt) return 'Backlog';
  if (labels.has('agent-review') || prs.some(pr => pr.state === 'CLOSED')) return 'Blocked';
  return null;
}

/**
 * Re-read before writing; a changed head, label, issue state or board value is deferred.
 * `before` is the snapshot built from the run's batched reads; without it the item is read live.
 */
export async function reconcileItem(api, item, { dryRun = true, before } = {}) {
  if (before === undefined) before = await api.read(item);
  if (!before) return 'skip';
  const desired = desiredStatus(before.issue);
  if (!desired || desired === before.current) return 'unchanged';
  if (dryRun) return `preview #${before.issue.number}: ${before.current ?? '(unset)'} → ${desired}`;
  const fresh = await api.read(item);
  if (JSON.stringify(before) !== JSON.stringify(fresh)) return `deferred #${before.issue.number}: state changed`;
  await api.write(item, desired);
  return `updated #${before.issue.number}: ${before.current ?? '(unset)'} → ${desired}`;
}

// Explicit API boundaries allow fixture-based tests without credentials or network access.
export class GitHub {
  constructor(projectToken, repositoryToken, request = fetch) {
    this.projectToken = projectToken;
    this.repositoryToken = repositoryToken;
    this.request = request;
  }
  async json(path, token, body) {
    const response = await this.request(`https://api.github.com/${path}`, {
      method: body ? 'POST' : 'GET',
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'Content-Type': 'application/json', 'X-GitHub-Api-Version': '2022-11-28' },
      ...(body ? { body: JSON.stringify(body) } : {}),
      signal: AbortSignal.timeout(30_000),
    });
    // Do not print response bodies, request headers, or tokens on failures.
    if (!response.ok) throw new Error(`GitHub ${path.split('?')[0]} returned HTTP ${response.status}`);
    const result = await response.json();
    if (result.errors?.length) throw new Error('GitHub GraphQL rejected the query; verify token permissions and schema.');
    return result;
  }
  async graph(query, variables, project = false) {
    return (await this.json('graphql', project ? this.projectToken : this.repositoryToken, { query, variables })).data;
  }
  async restPages(path) {
    const rows = [];
    for (let page = 1; ; page++) {
      const batch = await this.json(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`, this.repositoryToken);
      if (!Array.isArray(batch)) throw new Error('Expected a GitHub collection.');
      rows.push(...batch);
      if (batch.length < 100) return rows;
    }
  }
}

function complete(connection, description) {
  if (!connection || connection.pageInfo.hasNextPage) throw new Error(`${description} is incomplete; refusing a partial decision.`);
  return connection.nodes;
}

export async function loadProject(client) {
  const data = await client.graph(`query($owner: String!, $number: Int!) {
    user(login: $owner) { projectV2(number: $number) {
      id fields(first: 100) { pageInfo { hasNextPage } nodes {
        ... on ProjectV2SingleSelectField { id name options { id name } }
      } }
    } }
  }`, { owner: PROJECT.owner, number: PROJECT.number }, true);
  const project = data?.user?.projectV2;
  if (!project) throw new Error('Project #4 is not accessible.');
  const fields = complete(project.fields, 'Project fields');
  const field = fields.find(field => field.name === 'Status');
  if (!field || STATUSES.some(name => field.options.filter(option => option.name === name).length !== 1))
    throw new Error(`Status must contain exactly one of each: ${STATUSES.join(', ')}`);
  return { id: project.id, field: field.id, options: Object.fromEntries(field.options.map(option => [option.name, option.id])) };
}

// Same issue fields for the batched Project read and the live re-read, so snapshots compare equal.
const ISSUE_FIELDS = `number state stateReason
  labels(first: 50) { pageInfo { hasNextPage } nodes { name } }
  subIssues(first: 1) { totalCount }
  timelineItems(last: 1, itemTypes: [REOPENED_EVENT]) { nodes { ... on ReopenedEvent { createdAt } } }
  closedByPullRequestsReferences(first: 20, includeClosedPrs: true) {
    pageInfo { hasNextPage } nodes { number repository { nameWithOwner } }
  }`;
const STATUS_CONTEXTS = ['agent-validation', 'merge-validation', 'agent-review-verdict'];

export async function projectItems(client, project) {
  const items = [];
  let cursor = null;
  do {
    // Issue data comes with the item page, so unchanged cards cost no repository API calls.
    const data = await client.graph(`query($id: ID!, $cursor: String) {
      node(id: $id) { ... on ProjectV2 { items(first: 50, after: $cursor) {
        pageInfo { hasNextPage endCursor } nodes { id isArchived
          fieldValueByName(name: "Status") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
          content { __typename ... on Issue { id repository { nameWithOwner } ${ISSUE_FIELDS} } }
        }
      } } }
    }`, { id: project.id, cursor }, true);
    const connection = data?.node?.items;
    if (!connection) throw new Error('Project items could not be read.');
    items.push(...connection.nodes.filter(item => !item.isArchived && item.content?.__typename === 'Issue' && item.content.repository.nameWithOwner === PROJECT.repository));
    cursor = connection.pageInfo.hasNextPage ? connection.pageInfo.endCursor : null;
    if (connection.pageInfo.hasNextPage && !cursor) throw new Error('Missing project pagination cursor.');
  } while (cursor);
  return items;
}

export class ProjectSync {
  constructor(client, project) { this.client = client; this.project = project; this.pullList = null; }
  // The repository token allows 1,000 REST requests per hour shared with the agent pipeline,
  // so the PR list is fetched once per run rather than once per card.
  async pulls() {
    this.pullList ??= await this.client.restPages(`repos/${PROJECT.repository}/pulls?state=all`);
    return this.pullList;
  }
  async read(itemId) {
    // Check membership live as well as status. Removed/archived/replaced cards are never recreated.
    const data = await this.client.graph(`query($id: ID!) {
      node(id: $id) { ... on ProjectV2Item { isArchived project { id }
        fieldValueByName(name: "Status") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
        content { __typename ... on Issue { id number repository { nameWithOwner } } }
      } }
    }`, { id: itemId }, true);
    const item = data?.node;
    if (!item || item.isArchived || item.project.id !== this.project.id || item.content?.__typename !== 'Issue' || item.content.repository.nameWithOwner !== PROJECT.repository) return null;
    const result = await this.client.graph(`query($id: ID!) {
      node(id: $id) { ... on Issue { ${ISSUE_FIELDS} } }
    }`, { id: item.content.id });
    return this.snapshot(item.fieldValueByName?.name ?? null, result?.node, { live: true });
  }
  /** Builds the decision input. Batched snapshots use the cached PR list; live ones re-fetch each PR. */
  async snapshot(current, issue, { live = false } = {}) {
    if (!issue?.number) throw new Error('Issue could not be read.');
    const labels = complete(issue.labels, 'Issue labels').map(label => label.name).sort((a, b) => a.localeCompare(b));
    const parent = issue.subIssues.totalCount > 0;
    if (parent || MANUAL_ISSUES.has(issue.number)) return null;
    const linked = complete(issue.closedByPullRequestsReferences, 'Linked pull requests')
      .filter(pr => pr.repository.nameWithOwner === PROJECT.repository).map(pr => pr.number);
    // Closing-keyword links may be absent while a PR targets non-default develop. The exact
    // Claude branch prefix provides a fallback without trusting comments or parsing PR prose.
    const cached = new Map((await this.pulls()).map(pr => [pr.number, pr]));
    const numbers = new Set([...linked, ...[...cached.values()]
      .filter(pr => pr.head.ref.startsWith(`agent/issue-${issue.number}-`)).map(pr => pr.number)]);
    const prs = [];
    for (const number of numbers) {
      const pr = live || !cached.has(number)
        ? await this.client.json(`repos/${PROJECT.repository}/pulls/${number}`, this.client.repositoryToken)
        : cached.get(number);
      const sameRepository = pr.head.repo?.full_name === PROJECT.repository && pr.base.repo?.full_name === PROJECT.repository;
      const agent = pr.head.ref.startsWith(`agent/issue-${issue.number}-`);
      if (!sameRepository || !agent || pr.base.ref !== 'develop') continue;
      const statuses = {};
      if (pr.state === 'open') {
        // The combined status holds the latest state of each context in one request.
        const combined = await this.client.json(`repos/${PROJECT.repository}/commits/${pr.head.sha}/status?per_page=100`, this.client.repositoryToken);
        for (const status of combined.statuses ?? [])
          if (STATUS_CONTEXTS.includes(status.context)) statuses[status.context] = status.state;
      }
      prs.push({ number: pr.number, head: pr.head.sha, state: pr.merged_at ? 'MERGED' : pr.state.toUpperCase(),
        mergedAt: pr.merged_at, base: pr.base.ref, sameRepository, agent, draft: pr.draft,
        statuses: Object.fromEntries(Object.entries(statuses).sort(([a], [b]) => a.localeCompare(b))) });
    }
    return { current, issue: {
      number: issue.number, state: issue.state, stateReason: issue.stateReason, labels, parent,
      reopenedAt: issue.timelineItems.nodes[0]?.createdAt ?? null, prs: prs.sort((a, b) => a.number - b.number),
    } };
  }
  async write(item, status) {
    const option = this.project.options[status];
    if (!option) throw new Error('Unknown target Status option.');
    const data = await this.client.graph(`mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
      updateProjectV2ItemFieldValue(input: { projectId: $project, itemId: $item, fieldId: $field,
        value: { singleSelectOptionId: $option } }) { projectV2Item { id } }
    }`, { project: this.project.id, item, field: this.project.field, option }, true);
    if (data?.updateProjectV2ItemFieldValue?.projectV2Item?.id !== item) throw new Error('Project update was not confirmed.');
  }
}

async function main() {
  if (process.env.GITHUB_REPOSITORY !== PROJECT.repository) throw new Error('This workflow is scoped to cristhyanc/InventoryApp.');
  if (!process.env.PROJECT_SYNC_TOKEN || !process.env.GITHUB_TOKEN) throw new Error('PROJECT_SYNC_TOKEN and GITHUB_TOKEN are required; see docs/project-status-sync.md.');
  const dryRun = process.env.PROJECT_SYNC_APPLY !== 'true';
  const client = new GitHub(process.env.PROJECT_SYNC_TOKEN, process.env.GITHUB_TOKEN);
  const project = await loadProject(client);
  const api = new ProjectSync(client, project);
  const lines = [`Project #4 status sync (${dryRun ? 'preview' : 'apply'})`];
  for (const item of await projectItems(client, project)) {
    const before = await api.snapshot(item.fieldValueByName?.name ?? null, item.content);
    const result = await reconcileItem(api, item.id, { dryRun, before });
    if (!['skip', 'unchanged'].includes(result)) lines.push(result);
  }
  if (lines.length === 1) lines.push('No status changes.');
  console.log(lines.join('\n'));
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${lines.join('\n\n')}\n`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  main().catch(error => { console.error(error.message); process.exitCode = 1; });
