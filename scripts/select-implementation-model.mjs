import { createHash } from 'node:crypto';
import { appendFileSync, readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

// Trusted policy: model output can select a tier, never supply a model ID or CLI arguments.
export const MODELS = Object.freeze({
  claude: { low: 'haiku', standard: 'sonnet', high: 'opus' },
  copilot: { low: 'claude-haiku-4.5', standard: 'claude-sonnet-5.5', high: 'claude-opus-5.5' },
});
export const READY_LABELS = Object.keys(MODELS).flatMap(provider => [`agent-ready-${provider}`, `agent-ready-${provider}-low`, `agent-ready-${provider}-high`]);
const activeStates = ['agent-working', 'agent-architecture-fix', 'agent-review', 'agent-blocked'];
const fingerprint = issue => createHash('sha256').update(JSON.stringify({ title: issue.title, body: issue.body })).digest('hex');

export function prepareSelection({ provider, label, issue, attempt = 1 }) {
  if (!Object.hasOwn(MODELS, provider) || !READY_LABELS.includes(label) || !label.startsWith(`agent-ready-${provider}`)) throw new Error('Invalid implementation label/provider.');
  if (issue.state !== 'OPEN') throw new Error('Implementation requires an open issue.');
  const labels = issue.labels.map(item => typeof item === 'string' ? item : item.name);
  const ready = labels.filter(item => READY_LABELS.includes(item));
  const active = labels.filter(item => activeStates.includes(item));
  const resumed = provider === 'claude' && Number.isInteger(attempt) && attempt > 1 && ready.length === 0 && active.length === 1 && active[0] === 'agent-working';
  if (!resumed && active.length) throw new Error('Task already has an active or blocked state; human must resolve it first.');
  if (!resumed && (ready.length !== 1 || ready[0] !== label)) throw new Error('Exactly one matching readiness label is required; remove conflicting or stale labels.');
  const tier = label.endsWith('-low') ? 'low' : label.endsWith('-high') ? 'high' : 'default';
  return { provider, label, tier, triage: tier === 'default', fingerprint: fingerprint(issue), attempt };
}

export function resolveSelection(selection, triage) {
  let tier = selection.tier;
  let reason = 'Explicit human model-tier label.';
  if (selection.triage) {
    if (!triage || Object.keys(triage).sort().join(',') !== 'reason,tier' || !['low', 'standard', 'high-required'].includes(triage.tier) || typeof triage.reason !== 'string' || !triage.reason.trim() || triage.reason.length > 240 || /[\x00-\x1f\x7f]/.test(triage.reason)) throw new Error('Invalid triage output; implementation was not started.');
    if (triage.tier === 'high-required') throw new Error(`Human decision required: remove ${selection.label} and apply ${selection.label}-high to authorize a high model. ${triage.reason}`);
    tier = triage.tier;
    reason = triage.reason.trim();
  }
  const model = MODELS[selection.provider]?.[tier];
  if (!model) throw new Error('Invalid selected model tier.');
  return { ...selection, tier, model, maxTurns: { low: 80, standard: 150, high: 250 }[tier], reason };
}

export function verifySnapshot(selection, issue) {
  prepareSelection({ ...selection, issue });
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
      output({ model: result.model, max_turns: result.maxTurns, tier: result.tier, reason: result.reason, fingerprint: result.fingerprint });
      if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `Implementation selection: ${result.provider} / ${result.tier} / ${result.model}. ${result.reason}\n`);
    } else if (command === 'verify') {
      verifySnapshot({ provider: process.env.PROVIDER, label: process.env.READY_LABEL, fingerprint: process.env.TASK_FINGERPRINT, attempt: Number(process.env.RUN_ATTEMPT || 1) }, JSON.parse(readFileSync(issueFile, 'utf8')));
    } else throw new Error('Expected prepare, resolve or verify.');
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
