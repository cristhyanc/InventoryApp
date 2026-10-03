// Test fixtures for the provider-mode resolver (#339). Not used by any workflow.

/**
 * The issue label events (GitHub REST shape) of one claim: a person applies `label`, then the
 * claiming workflow step swaps it for agent-working as github-actions[bot].
 * Kept free of imports and closures so the fake gh scripts can inline it with toString().
 */
export function claimEvents(label, claimedAt = '2026-10-03T01:00:00Z', firstId = 1) {
  const at = new Date(claimedAt).getTime();
  const iso = (offsetMs) => new Date(at + offsetMs).toISOString().replace('.000Z', 'Z');
  return [
    { id: firstId, event: 'labeled', actor: { login: 'cristhyanc', type: 'User' }, label: { name: label }, created_at: iso(-60000) },
    { id: firstId + 1, event: 'unlabeled', actor: { login: 'github-actions[bot]', type: 'Bot' }, label: { name: label }, created_at: iso(0) },
    { id: firstId + 2, event: 'labeled', actor: { login: 'github-actions[bot]', type: 'Bot' }, label: { name: 'agent-working' }, created_at: iso(1000) },
  ];
}
