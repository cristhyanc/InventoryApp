import { createHash } from 'node:crypto';
import { appendFileSync, readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { FULL_PROVIDER_EXECUTION_ENABLED, parseReadinessLabel } from './agent-mode.mjs';

// Trusted policy: model output can select a tier, never supply a model ID or CLI arguments.
export const MODELS = Object.freeze({
  claude: { low: 'haiku', standard: 'sonnet', high: 'opus' },
  copilot: { low: 'claude-haiku-4.5', standard: 'claude-sonnet-5.5', high: 'claude-opus-5.5' },
});
// Readiness labels, routes and the full-provider gate live in the standalone agent-mode.mjs, which
// jobs without a checkout also fetch from the trusted workflow commit.
export { READINESS_LABELS, READINESS_LABEL_PATTERN, FULL_READY_LABELS, DEFAULT_READY_LABEL, ROUTES, FULL_PROVIDER_EXECUTION_ENABLED, parseReadinessLabel, STANDARD_READY_LABELS as READY_LABELS } from './agent-mode.mjs';
const activeStates = ['agent-working', 'agent-architecture-fix', 'agent-review', 'agent-blocked'];
const hasControlCharacter = text => [...text].some(char => char.charCodeAt(0) < 0x20 || char.charCodeAt(0) === 0x7f);
const fingerprint = issue => createHash('sha256').update(JSON.stringify({ title: issue.title, body: issue.body })).digest('hex');

export function prepareSelection({ provider, label, issue, attempt = 1, fullProviderEnabled = FULL_PROVIDER_EXECUTION_ENABLED }) {
  const route = parseReadinessLabel(label);
  if (!Object.hasOwn(MODELS, provider) || !route || route.implementer !== provider) throw new Error('Invalid implementation label/provider.');
  if (route.sameProviderReview && !fullProviderEnabled) throw new Error(`${label} is not enabled yet: single-provider review and repair routes are not connected. Remove ${label}; nothing was started.`);
  if (issue.state !== 'OPEN') throw new Error('Implementation requires an open issue.');
  const labels = issue.labels.map(item => typeof item === 'string' ? item : item.name);
  const ready = labels.filter(item => parseReadinessLabel(item));
  const active = labels.filter(item => activeStates.includes(item));
  const resumed = provider === 'claude' && Number.isInteger(attempt) && attempt > 1 && ready.length === 0 && active.length === 1 && active[0] === 'agent-working';
  if (!resumed && active.length) throw new Error('Task already has an active or blocked state; human must resolve it first.');
  if (!resumed && (ready.length !== 1 || ready[0] !== label)) throw new Error('Exactly one matching readiness label is required; remove conflicting or stale labels.');
  return { provider, label, mode: route.mode, reviewer: route.reviewer, sameProviderReview: route.sameProviderReview, tier: route.tier, triage: route.tier === 'default', fingerprint: fingerprint(issue), attempt };
}

export function resolveSelection(selection, triage) {
  let tier = selection.tier;
  let reason = 'Explicit human model-tier label.';
  if (selection.triage) {
    if (!triage || Object.keys(triage).sort((a, b) => a.localeCompare(b)).join(',') !== 'reason,tier' || !['low', 'standard', 'high', 'clarification-required'].includes(triage.tier) || typeof triage.reason !== 'string' || !triage.reason.trim() || triage.reason.length > 240 || hasControlCharacter(triage.reason)) throw new Error('Invalid triage output; implementation was not started.');
    if (triage.tier === 'clarification-required') throw new Error(`Human clarification required: the requirements are ambiguous, so implementation was not started. Clarify the issue, then remove and re-apply ${selection.label}. ${triage.reason}`);
    tier = triage.tier;
    reason = triage.reason.trim();
  }
  const model = MODELS[selection.provider]?.[tier];
  if (!model) throw new Error('Invalid selected model tier.');
  return { ...selection, tier, model, maxTurns: { low: 300, standard: 150, high: 250 }[tier], reason };
}

export function verifySnapshot(selection, issue) {
  prepareSelection({ provider: selection.provider, label: selection.label, attempt: selection.attempt, issue });
  if (fingerprint(issue) !== selection.fingerprint) throw new Error('Task scope changed after model selection; re-apply the readiness label.');
}

function output(values) {
  for (const [key, value] of Object.entries(values)) {
    if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `${key}=${value}\n`);
    else console.log(`${key}=${value}`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const [command, issueFile, selectionFile] = process.argv.slice(2);
    if (command === 'prepare') {
      const issue = JSON.parse(readFileSync(issueFile, 'utf8'));
      const selection = prepareSelection({ provider: process.env.PROVIDER, label: process.env.READY_LABEL, issue, attempt: Number(process.env.RUN_ATTEMPT || 1) });
      writeFileSync(selectionFile, JSON.stringify(selection));
      // Only this snapshot is exposed to triage, without credentials or instructions from comments.
      writeFileSync('.git/model-triage.json', JSON.stringify({ title: issue.title, body: issue.body }));
      output({ triage: selection.triage });
    } else if (command === 'resolve') {
      const selection = JSON.parse(readFileSync(selectionFile, 'utf8'));
      const result = resolveSelection(selection, selection.triage ? JSON.parse(process.env.TRIAGE_OUTPUT || 'null') : undefined);
      output({ model: result.model, max_turns: result.maxTurns, tier: result.tier, reason: result.reason, fingerprint: result.fingerprint, mode: result.mode });
      if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `Implementation selection: ${result.provider} / ${result.tier} / ${result.model}. ${result.reason}\n`);
    } else if (command === 'verify') {
      verifySnapshot({ provider: process.env.PROVIDER, label: process.env.READY_LABEL, fingerprint: process.env.TASK_FINGERPRINT, attempt: Number(process.env.RUN_ATTEMPT || 1) }, JSON.parse(readFileSync(issueFile, 'utf8')));
    } else throw new Error('Expected prepare, resolve or verify.');
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
