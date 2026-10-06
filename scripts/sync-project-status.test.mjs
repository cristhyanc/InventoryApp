import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { desiredStatus, reconcileItem, GitHub, loadProject, projectItems, ProjectSync, STATUSES, PROJECT } from './sync-project-status.mjs';

const issue = (changes = {}) => ({ number: 384, state: 'OPEN', stateReason: null, labels: [], parent: false, reopenedAt: null, prs: [], ...changes });
const pr = (changes = {}) => ({ number: 392, state: 'OPEN', base: 'develop', sameRepository: true, agent: true, draft: false, head: 'a'.repeat(40), statuses: {}, ...changes });
const green = { 'agent-validation': 'success', 'merge-validation': 'success', 'agent-review-verdict': 'success' };

test('manual planning and parent epics are preserved', () => {
  assert.equal(desiredStatus(issue()), null);
  assert.equal(desiredStatus(issue({ number: 383, labels: ['agent-working'] })), null);
  assert.equal(desiredStatus(issue({ parent: true, labels: ['agent-working'] })), null);
});
test('readiness routes and active labels map without starting agents', () => {
  for (const label of ['agent-ready-claude', 'agent-ready-claude-high', 'agent-ready-full-claude'])
    assert.equal(desiredStatus(issue({ labels: [label] })), 'Ready');
  assert.equal(desiredStatus(issue({ labels: ['agent-working'] })), 'In progress');
  // Copilot no longer implements: its retired readiness label does not make an issue agent-managed.
  assert.equal(desiredStatus(issue({ labels: ['agent-ready-copilot'] })), null);
  assert.equal(desiredStatus(issue({ labels: ['agent-ready-claude', 'agent-ready-full-claude'] })), 'Blocked');
  assert.equal(desiredStatus(issue({ labels: ['agent-blocked', 'agent-review'] })), 'Blocked');
});
test('review is exact-head and requires both validation gates', () => {
  assert.equal(desiredStatus(issue({ prs: [pr({ statuses: green })] })), 'Awaiting approval');
  assert.equal(desiredStatus(issue({ prs: [pr()] })), 'In review');
  assert.equal(desiredStatus(issue({ prs: [pr({ statuses: { ...green, 'agent-review-verdict': 'failure' } })] })), 'Changes requested');
  assert.equal(desiredStatus(issue({ prs: [pr({ statuses: { ...green, 'agent-validation': 'failure' } })] })), 'Blocked');
  assert.equal(desiredStatus(issue({ prs: [pr({ statuses: { ...green, 'merge-validation': 'pending' } })] })), 'In review');
  assert.equal(desiredStatus(issue({ prs: [pr({ draft: true, statuses: green })] })), 'In progress');
});
test('completion excludes abandoned PRs, other bases, forks and stale merged work', () => {
  const merged = pr({ state: 'MERGED', mergedAt: '2026-10-04T00:00:00Z' });
  assert.equal(desiredStatus(issue({ prs: [merged] })), 'Done');
  assert.equal(desiredStatus(issue({ prs: [pr({ state: 'CLOSED' })] })), 'Blocked');
  assert.equal(desiredStatus(issue({ prs: [pr({ ...merged, base: 'main' })] })), null);
  assert.equal(desiredStatus(issue({ prs: [pr({ ...merged, sameRepository: false })] })), null);
  assert.equal(desiredStatus(issue({ reopenedAt: '2026-10-05T00:00:00Z', prs: [merged] })), 'Backlog');
  assert.equal(desiredStatus(issue({ labels: ['agent-ready-claude'], prs: [merged] })), 'Ready');
  assert.equal(desiredStatus(issue({ state: 'CLOSED', stateReason: 'NOT_PLANNED', prs: [merged] })), null);
  assert.equal(desiredStatus(issue({ prs: [pr(), pr({ number: 393 })] })), 'Blocked');
});
test('dry run and already-correct items never mutate', async () => {
  const snapshot = { current: 'Backlog', issue: issue({ labels: ['agent-working'] }) };
  const writes = [];
  const api = { read: async () => snapshot, write: async (...args) => writes.push(args) };
  await reconcileItem(api, 'item', { dryRun: true });
  assert.equal(writes.length, 0);
  snapshot.current = 'In progress';
  await reconcileItem(api, 'item', { dryRun: false });
  assert.equal(writes.length, 0);
});
test('a head or board change between read and write cancels the update', async () => {
  const before = { current: 'In review', issue: issue({ prs: [pr({ statuses: green })] }) };
  for (const after of [
    { ...before, current: 'Blocked' },
    { ...before, issue: issue({ prs: [pr({ head: 'b'.repeat(40) })] }) },
  ]) {
    const reads = [before, after];
    const writes = [];
    await reconcileItem({ read: async () => reads.shift(), write: async (...args) => writes.push(args) }, 'item', { dryRun: false });
    assert.equal(writes.length, 0);
  }
});
test('stable state updates only the item status', async () => {
  const snapshot = { current: 'Backlog', issue: issue({ labels: ['agent-working'] }) };
  const writes = [];
  await reconcileItem({ read: async () => snapshot, write: async (...args) => writes.push(args) }, 'item', { dryRun: false });
  assert.deepEqual(writes, [['item', 'In progress']]);
});

test('Project pagination excludes archived items, PR cards and other repositories', async () => {
  const item = (id, changes = {}) => ({ id, isArchived: false, content: { __typename: 'Issue', number: 384, repository: { nameWithOwner: PROJECT.repository } }, ...changes });
  const pages = [
    { nodes: [item('one'), item('archived', { isArchived: true }), item('pr', { content: { __typename: 'PullRequest' } })], pageInfo: { hasNextPage: true, endCursor: 'next' } },
    { nodes: [item('two'), item('foreign', { content: { __typename: 'Issue', repository: { nameWithOwner: 'other/repo' } } })], pageInfo: { hasNextPage: false } },
  ];
  const calls = [];
  const items = await projectItems({ graph: async (query, vars, project) => { calls.push({ vars, project }); return { node: { items: pages.shift() } }; } }, { id: 'project' });
  assert.deepEqual(items.map(item => item.id), ['one', 'two']);
  assert.equal(calls[1].vars.cursor, 'next');
  assert.ok(calls.every(call => call.project));
});
test('missing status options and truncated fields fail without mutation', async () => {
  for (const fields of [
    { pageInfo: { hasNextPage: false }, nodes: [{ id: 'status', name: 'Status', options: [] }] },
    { pageInfo: { hasNextPage: true }, nodes: [] },
  ]) await assert.rejects(loadProject({ graph: async () => ({ user: { projectV2: { id: 'project', fields } } }) }));
});
const livePr = (changes = {}) => ({ number: 392, head: { sha: 'b'.repeat(40), ref: 'agent/issue-384-test', repo: { full_name: PROJECT.repository } }, base: { ref: 'develop', repo: { full_name: PROJECT.repository } }, state: 'open', merged_at: null, draft: false, ...changes });
const issueNode = (changes = {}) => ({ number: 384, state: 'OPEN', stateReason: null, labels: { nodes: [{ name: 'agent-review' }], pageInfo: { hasNextPage: false } }, subIssues: { totalCount: 0 }, timelineItems: { nodes: [] }, closedByPullRequestsReferences: { nodes: [], pageInfo: { hasNextPage: false } }, ...changes });
const combined = { statuses: [{ context: 'agent-review-verdict', state: 'pending' }, { context: 'agent-validation', state: 'success' }, { context: 'merge-validation', state: 'success' }, { context: 'other', state: 'failure' }] };
const repositoryClient = (pulls, calls) => ({
  repositoryToken: 'repo-fixture',
  graph: async (query, vars, project) => {
    calls.push({ query, vars, project });
    if (project) return { node: { project: { id: 'project' }, isArchived: false, fieldValueByName: { name: 'In review' }, content: { __typename: 'Issue', id: 'issue', repository: { nameWithOwner: PROJECT.repository } } } };
    return { node: issueNode() };
  },
  json: async (path, token) => {
    assert.equal(token, 'repo-fixture');
    calls.push({ path });
    if (path.includes('/commits/')) { assert.ok(path.includes(`/commits/${'b'.repeat(40)}/status`)); return combined; }
    return livePr();
  },
  restPages: async path => { calls.push({ path }); assert.ok(path.endsWith('/pulls?state=all')); return pulls; },
});
test('live read uses current head statuses; no prior SHA or event state is used', async () => {
  const calls = [];
  const snapshot = await new ProjectSync(repositoryClient([livePr({ head: { ...livePr().head, sha: 'a'.repeat(40) } })], calls), { id: 'project' }).read('item');
  assert.equal(snapshot.issue.prs[0].head, 'b'.repeat(40));
  assert.deepEqual(snapshot.issue.prs[0].statuses, { 'agent-review-verdict': 'pending', 'agent-validation': 'success', 'merge-validation': 'success' });
  assert.equal(desiredStatus(snapshot.issue), 'In review');
  assert.equal(calls[0].project, true);
  assert.equal(calls[1].project, undefined);
  assert.ok(calls.some(call => call.path?.endsWith('/pulls/392')));
});
test('batched snapshots list PRs once per run and skip REST calls for cards without agent PRs', async () => {
  const calls = [];
  const sync = new ProjectSync(repositoryClient([livePr(), livePr({ number: 10, head: { sha: 'c'.repeat(40), ref: 'feature/x', repo: { full_name: PROJECT.repository } } })], calls), { id: 'project' });
  for (let number = 1; number <= 20; number++)
    assert.equal(desiredStatus((await sync.snapshot('Backlog', issueNode({ number, labels: { nodes: [], pageInfo: { hasNextPage: false } } }))).issue), null);
  const managed = await sync.snapshot('Backlog', issueNode());
  assert.equal(desiredStatus(managed.issue), 'In review');
  assert.equal(calls.filter(call => call.path?.includes('/pulls?')).length, 1);
  assert.equal(calls.filter(call => call.path?.includes('/pulls/')).length, 0);
  assert.equal(calls.filter(call => call.path?.includes('/commits/')).length, 1);
  assert.equal(calls.filter(call => call.query).length, 0);
});
test('batched and live snapshots of an unchanged item compare equal, so updates are not deferred', async () => {
  const sync = new ProjectSync(repositoryClient([livePr()], []), { id: 'project' });
  const batched = await sync.snapshot('In review', issueNode());
  assert.equal(JSON.stringify(batched), JSON.stringify(await sync.read('item')));
});
test('only allowed mutation writes one field and verifies the response', async () => {
  const calls = [];
  const client = { graph: async (query, vars, project) => { calls.push({ query, vars, project }); return { updateProjectV2ItemFieldValue: { projectV2Item: { id: 'item' } } }; } };
  const sync = new ProjectSync(client, { id: 'project', field: 'status', options: { Blocked: 'blocked' } });
  await sync.write('item', 'Blocked');
  assert.deepEqual(calls[0].vars, { project: 'project', item: 'item', field: 'status', option: 'blocked' });
  assert.equal(calls[0].project, true);
  await assert.rejects(sync.write('item', 'Invented'));
  assert.equal(calls.length, 1);
});
test('REST pagination and API errors preserve token separation without leaking secrets', async () => {
  const calls = [];
  const github = new GitHub('project-fixture', 'repo-fixture', async (url, options) => {
    calls.push({ url, options });
    return { ok: true, json: async () => url.endsWith('page=1') ? Array.from({ length: 100 }, (_, i) => ({ i })) : [{ i: 100 }] };
  });
  assert.equal((await github.restPages('repos/cristhyanc/InventoryApp/pulls?state=all')).length, 101);
  assert.ok(calls.every(call => call.options.headers.Authorization === 'Bearer repo-fixture'));
  const broken = new GitHub('never-log-this', 'repo', async () => ({ ok: false, status: 403 }));
  await assert.rejects(broken.graph('query {}', {}, true), error => error.message.includes('HTTP 403') && !error.message.includes('never-log-this'));
});
test('privileged workflow executes trusted code only and remains opt-in', () => {
  const workflow = readFileSync(new URL('../.github/workflows/project-status-sync.yml', import.meta.url), 'utf8');
  assert.match(workflow, /ref: \$\{\{ github\.workflow_sha \}\}/);
  assert.match(workflow, /persist-credentials: false/);
  assert.match(workflow, /vars\.PROJECT_SYNC_ENABLED == 'true'/);
  assert.match(workflow, /github\.ref == 'refs\/heads\/main'/);
  const listened = workflow.match(/workflow_run:\s+workflows:\n((?:\s+- .+\n)+)/)[1];
  assert.doesNotMatch(listened, /\*|Project status sync/);
  assert.match(listened, /- Agent review\n/);
  assert.doesNotMatch(workflow, /permissions:[\s\S]*?\b(?:contents|issues|pull-requests|actions): write/);
  assert.doesNotMatch(workflow, /pull_request\.head|download-artifact|npm (?:ci|install)/);
  for (const name of ['validate.sh', 'validate.ps1'])
    assert.ok(readFileSync(new URL(name, import.meta.url), 'utf8').includes('sync-project-status.test.mjs'));
  assert.equal(STATUSES.length, 8);
});
