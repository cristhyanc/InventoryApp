// Trusted transport of the final review result from a read-only reviewer job to the guarded
// publisher in agent-review.yml.
//
// The reviewer used to hand its JSON to the publisher as a job output. GitHub withholds any job
// output that contains a value it masks ("Skip output 'structured_output' since it may contain
// secret."), so a successful review could reach the publisher as an empty string and be lost (PR
// #558, run 38050149174). The review now travels as a short-lived artifact of the same workflow
// run instead:
//
// - `package` (reviewer job) validates the model's JSON against the review contract, redacts
//   secret-like values, and wraps it in an envelope bound to the repository, pull request, head
//   SHA, provider route and workflow run. The step then uploads that one file as the artifact named
//   by `artifact-name` and exposes only the artifact id and the envelope's SHA-256 as job outputs.
// - `fetch` (publisher job) finds that artifact through the Actions API of this exact run, checks
//   its id, run, name, expiry, size and digest, extracts the one envelope file with a size bound,
//   and re-verifies every binding, the contract and the redaction before it writes the plain review
//   for the publisher. Any mismatch fails with a coded reason and nothing is published.
//
// Reasons and log lines never echo model text or repository content. This file is standalone (Node
// built-ins only) because the publisher has no checkout: both jobs fetch it from the trusted
// workflow commit, never from the pull request head.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export const ENVELOPE_SCHEMA = 'inventoryapp.agent-review-result.v1';
export const ENVELOPE_FILE = 'review-envelope.json';
export const MAX_ENVELOPE_BYTES = 512 * 1024;
export const MAX_ARTIFACT_BYTES = 1024 * 1024;
export const REDACTED = '[REDACTED]';

const REVIEW_KEYS = ['blockers', 'criteria', 'inline_comments', 'reviewed_head_sha', 'suggestions', 'validation_evidence', 'verdict'];
const ENVELOPE_KEYS = ['agent_mode', 'head_sha', 'implementer', 'pr_number', 'redactions', 'repository', 'review', 'reviewer', 'run_attempt', 'run_id', 'schema'];
const VERDICTS = ['CHANGES REQUESTED', 'READY FOR HUMAN REVIEW'];
const STATUSES = ['met', 'not met', 'not verified'];
const SHA_PATTERN = /^[0-9a-f]{40}$/;

export class TransportError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}

const fail = (code, message) => { throw new TransportError(code, message); };

// Secret-like values that must never be published or kept in a stored artifact. Each pattern needs
// a recognisable prefix or key name, so commit SHAs, file paths and ordinary prose are untouched.
const SECRET_PATTERNS = [
  [/-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)/g, REDACTED],
  [/\bgh[pousr]_[A-Za-z0-9]{30,}/g, REDACTED],
  [/\bgithub_pat_[A-Za-z0-9_]{22,}/g, REDACTED],
  [/\bsk-ant-[A-Za-z0-9_-]{16,}/g, REDACTED],
  [/\bsk-(?:proj-)?[A-Za-z0-9_-]{32,}/g, REDACTED],
  [/\b(?:AKIA|ASIA)[0-9A-Z]{16}\b/g, REDACTED],
  [/\bxox[abprs]-[A-Za-z0-9-]{10,}/g, REDACTED],
  [/\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}/g, REDACTED],
  [/\b(AccountKey|SharedAccessKey|SharedAccessSignature|sig|client_secret|ClientSecret)=[^;\s"'&]{8,}/gi, `$1=${REDACTED}`],
  [/\b(Password|Pwd)=[^;\s"']+/gi, `$1=${REDACTED}`],
  [/\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{20,}/g, `$1 ${REDACTED}`],
  [/(https?:\/\/)[^\s:@/]+:[^\s@/]+@/g, `$1${REDACTED}@`],
];

/** Redacts secret-like values in one string; returns the new string and how many were replaced. */
export function redactText(text) {
  let count = 0;
  let value = text;
  for (const [pattern, replacement] of SECRET_PATTERNS) {
    value = value.replace(pattern, (...match) => {
      count += 1;
      return replacement.replace('$1', match[1] ?? '');
    });
  }
  return { value, count };
}

/** Redacts every string inside a JSON value; keys are fixed by the contract and left alone. */
export function redactSecrets(input) {
  let count = 0;
  const walk = (value) => {
    if (typeof value === 'string') {
      const result = redactText(value);
      count += result.count;
      return result.value;
    }
    if (Array.isArray(value)) return value.map(walk);
    if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, walk(v)]));
    return value;
  };
  return { value: walk(input), count };
}

const nonEmptyString = (value) => typeof value === 'string' && value.length > 0;
const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const exactKeys = (value, keys) => isObject(value) && Object.keys(value).length === keys.length && keys.every((k) => Object.hasOwn(value, k));

/**
 * The review contract shared by both reviewers (the same rules as the Claude --json-schema and the
 * Copilot jq check). Returns the names of the failed checks only, never field contents.
 */
export function contractFailures(review) {
  if (!isObject(review)) return ['root_is_object'];
  const checks = {
    exact_keys: exactKeys(review, REVIEW_KEYS),
    reviewed_head_sha: typeof review.reviewed_head_sha === 'string' && SHA_PATTERN.test(review.reviewed_head_sha),
    verdict: VERDICTS.includes(review.verdict),
    blockers: Array.isArray(review.blockers) && review.blockers.every(nonEmptyString),
    criteria: Array.isArray(review.criteria) && review.criteria.length > 0 && review.criteria.every((c) =>
      exactKeys(c, ['criterion', 'evidence', 'status']) && nonEmptyString(c.criterion) && nonEmptyString(c.evidence) && STATUSES.includes(c.status)),
    suggestions: Array.isArray(review.suggestions) && review.suggestions.every((s) => typeof s === 'string'),
    validation_evidence: nonEmptyString(review.validation_evidence),
    inline_comments: Array.isArray(review.inline_comments) && review.inline_comments.every((c) =>
      exactKeys(c, ['body', 'line', 'path']) && nonEmptyString(c.path) && nonEmptyString(c.body) && Number.isInteger(c.line) && c.line >= 1),
  };
  return Object.entries(checks).filter(([, ok]) => !ok).map(([name]) => name);
}

/** The run-scoped artifact name. The run id, not the name, is the trust anchor; the name only selects. */
export function artifactName({ runId, reviewer }) {
  if (!/^[1-9][0-9]*$/.test(String(runId))) fail('context', 'The workflow run id is missing or invalid.');
  if (!['claude', 'copilot'].includes(reviewer)) fail('context', 'The reviewer is missing or unknown.');
  return `agent-review-result-${reviewer}-${runId}`;
}

/** The binding every envelope must carry, read from the trusted job environment. */
export function contextFromEnv(env = process.env) {
  const context = {
    repository: env.GITHUB_REPOSITORY ?? '',
    prNumber: env.PR_NUMBER ?? '',
    headSha: env.HEAD_SHA ?? '',
    agentMode: env.AGENT_MODE ?? '',
    implementer: env.IMPLEMENTER ?? '',
    reviewer: env.REVIEWER ?? '',
    runId: env.GITHUB_RUN_ID ?? '',
    runAttempt: env.GITHUB_RUN_ATTEMPT ?? '',
  };
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(context.repository)) fail('context', 'The repository is missing or invalid.');
  if (!/^[1-9][0-9]*$/.test(context.prNumber)) fail('context', 'The pull request number is missing or invalid.');
  if (!SHA_PATTERN.test(context.headSha)) fail('context', 'The reviewed head SHA is missing or invalid.');
  if (!['cross-claude', 'full-claude'].includes(context.agentMode)) fail('context', 'The review route is missing or unknown.');
  if (context.implementer !== 'claude') fail('context', 'The implementer is missing or unknown.');
  if (!/^[1-9][0-9]*$/.test(context.runAttempt)) fail('context', 'The workflow run attempt is missing or invalid.');
  artifactName(context);
  return context;
}

/** Validates the raw model output and returns the envelope text to upload. */
export function packageReview(raw, context) {
  if (typeof raw !== 'string' || raw.trim() === '') fail('empty', 'The reviewer produced no structured output.');
  let review;
  try {
    review = JSON.parse(raw);
  } catch {
    fail('malformed', 'The reviewer output is not valid JSON.');
  }
  const failures = contractFailures(review);
  if (failures.length > 0) fail('contract', `The reviewer output breaks the review contract (${failures.join(', ')}).`);
  if (review.reviewed_head_sha !== context.headSha) fail('wrong-sha', `The review names SHA ${review.reviewed_head_sha}, not the reviewed SHA ${context.headSha}.`);
  const redacted = redactSecrets(review);
  const envelope = {
    schema: ENVELOPE_SCHEMA,
    repository: context.repository,
    pr_number: Number(context.prNumber),
    head_sha: context.headSha,
    agent_mode: context.agentMode,
    implementer: context.implementer,
    reviewer: context.reviewer,
    run_id: Number(context.runId),
    run_attempt: Number(context.runAttempt),
    redactions: redacted.count,
    review: redacted.value,
  };
  const text = JSON.stringify(envelope);
  if (Buffer.byteLength(text) > MAX_ENVELOPE_BYTES) fail('oversized', `The review result exceeds ${MAX_ENVELOPE_BYTES} bytes.`);
  return { text, redactions: redacted.count };
}

/** Re-verifies a received envelope against the publisher's own trusted context. */
export function verifyEnvelope(bytes, context) {
  if (bytes.length === 0) fail('empty', 'The review artifact is empty.');
  if (bytes.length > MAX_ENVELOPE_BYTES) fail('oversized', `The review artifact exceeds ${MAX_ENVELOPE_BYTES} bytes.`);
  let envelope;
  try {
    envelope = JSON.parse(bytes.toString('utf8'));
  } catch {
    fail('malformed', 'The review artifact is not valid JSON.');
  }
  if (!exactKeys(envelope, ENVELOPE_KEYS)) fail('malformed', 'The review artifact does not have the envelope shape.');
  if (envelope.schema !== ENVELOPE_SCHEMA) fail('malformed', 'The review artifact has an unknown schema.');
  const expected = {
    repository: context.repository,
    pr_number: Number(context.prNumber),
    head_sha: context.headSha,
    agent_mode: context.agentMode,
    implementer: context.implementer,
    reviewer: context.reviewer,
    run_id: Number(context.runId),
  };
  for (const [key, value] of Object.entries(expected)) {
    if (envelope[key] !== value) fail('mismatch', `The review artifact is bound to a different ${key.replace('_', ' ')}.`);
  }
  // A re-run attempt may publish a review from an earlier attempt of the same run, never a later one.
  if (!Number.isInteger(envelope.run_attempt) || envelope.run_attempt < 1 || envelope.run_attempt > Number(context.runAttempt)) {
    fail('mismatch', 'The review artifact is bound to a different run attempt.');
  }
  const failures = contractFailures(envelope.review);
  if (failures.length > 0) fail('contract', `The review in the artifact breaks the review contract (${failures.join(', ')}).`);
  if (envelope.review.reviewed_head_sha !== context.headSha) fail('wrong-sha', `The review names SHA ${envelope.review.reviewed_head_sha}, not the reviewed SHA ${context.headSha}.`);
  // Never trust the reviewer job's redaction: apply it again before anything is published.
  const redacted = redactSecrets(envelope.review);
  const prior = Number.isInteger(envelope.redactions) && envelope.redactions >= 0 ? envelope.redactions : 0;
  return { review: redacted.value, redactions: prior + redacted.count };
}

export const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');

// The publisher's GitHub CLI and unzip. Tests point the paths at fakes.
const GH_PATH = process.env.AGENT_REVIEW_TRANSPORT_GH_PATH || 'gh';
const UNZIP_PATH = process.env.AGENT_REVIEW_TRANSPORT_UNZIP_PATH || 'unzip';
const run = (file, args, maxBuffer) => execFileSync(file, args, { maxBuffer, stdio: ['ignore', 'pipe', 'pipe'] });

/**
 * Finds, downloads and verifies this run's review artifact. `artifactId` and `digest` are the
 * reviewer job's outputs; both are short values that can never carry review text.
 */
export function fetchReview(context, { artifactId, digest }) {
  if (!/^[1-9][0-9]*$/.test(artifactId ?? '')) fail('missing', 'The reviewer job reported no review artifact id.');
  if (!/^[0-9a-f]{64}$/.test(digest ?? '')) fail('missing', 'The reviewer job reported no review artifact digest.');
  const name = artifactName(context);
  let listing;
  try {
    listing = JSON.parse(run(GH_PATH, ['api', `repos/${context.repository}/actions/runs/${context.runId}/artifacts?name=${name}&per_page=100`], 4 * 1024 * 1024).toString('utf8'));
  } catch {
    fail('unavailable', 'The run artifacts could not be listed.');
  }
  const matches = (listing?.artifacts ?? []).filter((a) => a?.name === name);
  if (matches.length === 0) fail('missing', 'This run has no review artifact.');
  if (matches.length > 1) fail('mismatch', 'This run has more than one review artifact.');
  const [artifact] = matches;
  if (String(artifact.id) !== artifactId) fail('mismatch', 'The review artifact id does not match the reviewer job output.');
  if (String(artifact.workflow_run?.id ?? '') !== String(context.runId)) fail('mismatch', 'The review artifact belongs to another workflow run.');
  if (artifact.expired) fail('missing', 'The review artifact has expired.');
  if (!Number.isInteger(artifact.size_in_bytes) || artifact.size_in_bytes > MAX_ARTIFACT_BYTES) fail('oversized', 'The review artifact is larger than allowed.');

  const work = mkdtempSync(join(tmpdir(), 'agent-review-transport-'));
  try {
    const zip = join(work, 'artifact.zip');
    try {
      writeFileSync(zip, run(GH_PATH, ['api', `repos/${context.repository}/actions/artifacts/${artifactId}/zip`], MAX_ARTIFACT_BYTES + 1024));
    } catch {
      fail('unavailable', 'The review artifact could not be downloaded.');
    }
    let entries;
    try {
      entries = run(UNZIP_PATH, ['-Z1', zip], 64 * 1024).toString('utf8').split('\n').filter(Boolean);
    } catch {
      fail('malformed', 'The review artifact is not a readable archive.');
    }
    if (entries.length !== 1 || entries[0] !== ENVELOPE_FILE) fail('malformed', `The review artifact must contain exactly ${ENVELOPE_FILE}.`);
    let bytes;
    try {
      bytes = run(UNZIP_PATH, ['-p', zip, ENVELOPE_FILE], MAX_ENVELOPE_BYTES);
    } catch {
      fail('oversized', `The review artifact could not be extracted within ${MAX_ENVELOPE_BYTES} bytes.`);
    }
    if (sha256(bytes) !== digest) fail('mismatch', 'The review artifact digest does not match the reviewer job output.');
    return verifyEnvelope(bytes, context);
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

function main(argv) {
  const [command, ...args] = argv;
  try {
    if (command === 'package' && args.length === 2) {
      const context = contextFromEnv();
      let raw = '';
      try { raw = readFileSync(args[0], 'utf8'); } catch { raw = ''; }
      const { text, redactions } = packageReview(raw, context);
      writeFileSync(args[1], text);
      process.stdout.write(`${JSON.stringify({ name: artifactName(context), digest: sha256(Buffer.from(text)), bytes: Buffer.byteLength(text), redactions })}\n`);
      return 0;
    }
    if (command === 'fetch' && args.length === 1) {
      const context = contextFromEnv();
      const { review, redactions } = fetchReview(context, { artifactId: process.env.REVIEW_ARTIFACT_ID, digest: process.env.REVIEW_ARTIFACT_DIGEST });
      writeFileSync(args[0], JSON.stringify(review));
      process.stdout.write(`${JSON.stringify({ redactions })}\n`);
      return 0;
    }
    process.stderr.write('usage: agent-review-transport.mjs package <raw-review.json> <envelope-out.json> | fetch <review-out.json>\n');
    return 64;
  } catch (error) {
    if (error instanceof TransportError) {
      process.stderr.write(`${error.code}: ${error.message}\n`);
      return 2;
    }
    throw error;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = main(process.argv.slice(2));
}
