// The Apify runs this collector started (#929). Actor tools and call-actor return only a run's
// status and storage ids; the rows are read with get-dataset-items, and a run that outlives
// waitSecs is followed with get-actor-run. A read is allowed only for a run recorded here, and
// only the trusted PostToolUse hook records one, from the answer to a call the guard allowed.
// Claude cannot write this file: its Write tool is confined to the output folder.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileStateStorage } from './apify-guard.mjs';

export const LEDGER_FILE = 'apify-runs.json';
/** get-actor-run waits up to 45 s per call, so 6 polls follow one run for at most 4.5 minutes. */
export const MAX_POLLS_PER_RUN = 6;
/** A sample call returns at most 5 rows, so a few reads per dataset are enough. */
export const MAX_READS_PER_DATASET = 3;
const storageId = /^[a-zA-Z0-9]{1,64}$/;

const ledgerPath = stateDir => join(stateDir, LEDGER_FILE);

function readLedger(stateDir) {
  if (!existsSync(ledgerPath(stateDir))) return { runs: {} };
  const ledger = JSON.parse(readFileSync(ledgerPath(stateDir), 'utf8'));
  if (ledger === null || typeof ledger.runs !== 'object' || Array.isArray(ledger.runs)) {
    throw new Error('The run ledger is not the shape this hook writes.');
  }
  return ledger;
}

/** Every read and change happens inside the guard's own cross-process lock. */
async function withLedger(stateDir, change) {
  const lock = await fileStateStorage.acquireLock(ledgerPath(stateDir));
  if (!lock.acquired) throw new Error('The run ledger could not be locked.');
  try {
    const ledger = readLedger(stateDir);
    const answer = change(ledger);
    writeFileSync(ledgerPath(stateDir), `${JSON.stringify(ledger)}\n`, { mode: 0o600 });
    return answer;
  } finally {
    fileStateStorage.releaseLock(lock);
  }
}

/**
 * The run an Apify answer describes, read only at the server's fixed paths (`runId` and
 * `storages.datasets.*.id` of the RunResponse). Nothing deeper is searched, so text an actor put
 * in its own output cannot name a run or dataset.
 */
export function runFromResponse(response) {
  let value = response;
  if (Array.isArray(value)) value = { content: value };
  if (typeof value === 'string') value = { content: [{ type: 'text', text: value }] };
  let run = value?.structuredContent;
  if (run === undefined && Array.isArray(value?.content) && value.content[0]?.type === 'text') {
    try { run = JSON.parse(value.content[0].text); } catch { run = undefined; }
  }
  if (run === undefined && typeof value?.runId === 'string') run = value;
  if (!storageId.test(String(run?.runId ?? ''))) return null;
  const datasets = run.storages?.datasets;
  const datasetIds = datasets && typeof datasets === 'object'
    ? Object.values(datasets).map(dataset => dataset?.id).filter(id => storageId.test(String(id ?? ''))) : [];
  return { runId: run.runId, datasetIds };
}

/**
 * PostToolUse: records the run an allowed actor call started. A get-actor-run answer may add the
 * datasets of a run that is already recorded, but never records a new run.
 */
export async function recordRun(stateDir, { startedRun, response }) {
  const run = runFromResponse(response);
  if (!run) return false;
  return withLedger(stateDir, ledger => {
    const known = ledger.runs[run.runId];
    if (!known && !startedRun) return false;
    const entry = known ?? { datasets: {}, polls: 0 };
    for (const id of run.datasetIds) entry.datasets[id] ??= 0;
    ledger.runs[run.runId] = entry;
    return true;
  });
}

/** PreToolUse: why a follow-up read is refused, or null once it is reserved. */
export async function reserveRead(stateDir, tool, input) {
  return withLedger(stateDir, ledger => {
    if (tool === 'get-dataset-items') {
      const run = Object.values(ledger.runs).find(entry => Object.hasOwn(entry.datasets, input.datasetId));
      if (!run) return 'Only a dataset of a run this collector started may be read.';
      if (run.datasets[input.datasetId] >= MAX_READS_PER_DATASET) return `A dataset may be read at most ${MAX_READS_PER_DATASET} times.`;
      run.datasets[input.datasetId] += 1;
      return null;
    }
    const run = ledger.runs[input.runId];
    if (!run) return 'Only a run this collector started may be followed or stopped.';
    if (tool === 'get-actor-run') {
      if (run.polls >= MAX_POLLS_PER_RUN) return `A run may be polled at most ${MAX_POLLS_PER_RUN} times; abort it and record the search as failed.`;
      run.polls += 1;
    }
    return null;
  });
}
