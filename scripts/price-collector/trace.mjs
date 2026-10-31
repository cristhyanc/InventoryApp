// The collector's run log (#929). The owner decided on 31 October 2026 that a run must be readable
// end to end - queries, tool answers, rows and the API's decisions - even though the repository's
// Actions logs are public. Credentials are the one thing never written: every value of a secret the
// process holds is masked before a line reaches the log, and so is any bearer token.
import { appendFileSync, existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

export const LOG_DIR = 'logs';
export const TRACE_FILE = 'trace.jsonl';
/** One log entry's longest string; a tool answer beyond it keeps its head and tail. */
export const MAX_FIELD_CHARS = 20000;
/** The whole trace's ceiling, so a looping session cannot fill the runner's disk. */
export const MAX_TRACE_BYTES = 50 * 1024 * 1024;

const SECRET_NAMES = ['ANTHROPIC_API_KEY', 'APIFY_TOKEN', 'PRICE_COLLECTOR_API_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN', 'GITHUB_TOKEN'];

/** The secret values this process holds, longest first so a value inside another is masked whole. */
export function secretsOf(env = {}) {
  return SECRET_NAMES.map(name => env[name]).filter(value => typeof value === 'string' && value.length >= 8)
    .sort((a, b) => b.length - a.length);
}

/** Masks every secret value and every bearer or JWT-shaped token in a string. */
export function redact(text, secrets = []) {
  let out = String(text);
  for (const secret of secrets) out = out.split(secret).join('***');
  return out
    .replace(/(Bearer\s+)[A-Za-z0-9._~+/=-]{8,}/gi, '$1***')
    .replace(/\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/g, '***')
    .replace(/(apify_api_)[A-Za-z0-9]{8,}/g, '$1***')
    .replace(/(sk-ant-)[A-Za-z0-9_-]{8,}/g, '$1***');
}

/** A string shortened to its head and tail, saying how much was left out. */
export function clip(text, max = MAX_FIELD_CHARS) {
  const value = String(text);
  if (value.length <= max) return value;
  const half = Math.floor(max / 2);
  return `${value.slice(0, half)} … [${value.length - max} characters left out] … ${value.slice(-half)}`;
}

/** Any value as loggable text: strings as they are, everything else as JSON. */
export function textOf(value) {
  if (value === undefined) return '';
  if (typeof value === 'string') return value;
  try { return JSON.stringify(value); } catch { return String(value); }
}

export function logDir(stateDir) {
  return join(stateDir, LOG_DIR);
}

/** Appends one masked line to a log file of the run. Logging never changes a decision. */
export function appendLog(stateDir, file, line, env = {}) {
  try {
    if (!stateDir) return;
    const dir = logDir(stateDir);
    if (!existsSync(dir)) mkdirSync(dir, { recursive: true, mode: 0o700 });
    const path = join(dir, file);
    if (existsSync(path) && statSync(path).size > MAX_TRACE_BYTES) return;
    appendFileSync(path, `${redact(line, secretsOf(env))}\n`, { mode: 0o600 });
  } catch {
    // A full disk or a missing directory loses a log line, never a guard decision.
  }
}

/** One trace entry: the time, then the given fields, each string field clipped. */
export function trace(stateDir, entry, env = {}) {
  const clipped = Object.fromEntries(Object.entries(entry).map(([key, value]) => [key, typeof value === 'string' ? clip(value) : value]));
  appendLog(stateDir, TRACE_FILE, JSON.stringify({ at: new Date().toISOString(), ...clipped }), env);
}

/** The trace entries, oldest first; a damaged line is skipped. */
export function readTrace(stateDir) {
  const path = join(logDir(stateDir), TRACE_FILE);
  if (!existsSync(path)) return [];
  return readFileSync(path, 'utf8').split('\n').flatMap(line => {
    try { return line ? [JSON.parse(line)] : []; } catch { return []; }
  });
}
