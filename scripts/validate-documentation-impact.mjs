// Documentation-impact gate: deterministic validation of the documentation-impact declaration
// in an agent task issue body and in a pull request body.
//
// The parser proves only that a meaningful declaration exists. It never inspects the diff or
// infers documentation impact from changed filenames: whether the declaration is truthful is
// judged by the independent review agent and by the human who approves and merges.
//
// Usage:
//   node scripts/validate-documentation-impact.mjs --issue-body <file>
//   node scripts/validate-documentation-impact.mjs --pr-body <file>
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// --- Contract ---------------------------------------------------------------------------

export const ISSUE_DECISION_LABEL = 'Documentation impact decision';
export const ISSUE_DETAILS_LABEL = 'Documentation impact details';
export const ISSUE_DECISION_REQUIRED = 'Documentation changes required';
export const ISSUE_DECISION_NOT_REQUIRED = 'No documentation changes required';
export const ISSUE_DECISIONS = Object.freeze([ISSUE_DECISION_REQUIRED, ISSUE_DECISION_NOT_REQUIRED]);

export const PR_SECTION_HEADING = 'Documentation impact';
export const PR_DECISION_UPDATED = 'UPDATED';
export const PR_DECISION_NOT_REQUIRED = 'NOT REQUIRED';
export const PR_DECISIONS = Object.freeze([PR_DECISION_UPDATED, PR_DECISION_NOT_REQUIRED]);
export const PR_DECISION_FIELD = 'Decision';
export const PR_EVIDENCE_FIELD = 'Evidence';

// A documentation reference is a path under docs/, a Markdown/reStructuredText/AsciiDoc file,
// README, CHANGELOG, or one of the repository's issue/pull request templates.
export const DOCUMENTATION_REFERENCE_PATTERN =
  /(?:^|[^\w/.-])((?:[\w.-]+\/)*[\w.-]+\.(?:md|mdx|markdown|rst|adoc)|docs\/[\w./-]*|README|CHANGELOG|\.github\/ISSUE_TEMPLATE\/[\w./-]*)(?![\w/])/gi;

// Minimum evidence: the whole answer, and the part of it that is specific to the change.
export const MINIMUM_WORDS = 8;
export const MINIMUM_SPECIFIC_WORDS = 3;

// Whole answers (after normalisation) that carry no information about this change.
export const GENERIC_ANSWERS = Object.freeze([
  'none',
  'none required',
  'none needed',
  'none identified',
  'n a',
  'na',
  'nil',
  'null',
  'no',
  'nothing',
  'not applicable',
  'not applicable to this change',
  'not required',
  'not needed',
  'not affected',
  'no change',
  'no changes',
  'no impact',
  'no doc changes',
  'no docs',
  'no docs changes',
  'no documentation',
  'no documentation change',
  'no documentation changes',
  'no documentation changes required',
  'no documentation changes needed',
  'no documentation required',
  'no documentation impact',
  'documentation changes required',
  'documentation required',
  'documentation not affected',
  'documentation not required',
  'documentation unaffected',
  'docs not affected',
  'docs unaffected',
  'docs updated',
  'docs changed',
  'documentation updated',
  'documentation changed',
  'updated docs',
  'updated documentation',
  'updated',
  'done',
  'yes',
  'required',
  'tbd',
  'todo',
  'see above',
  'see below',
  'see diff',
  'see pr',
  'see the diff',
  'as above',
  'as described',
  'self explanatory',
]);

// Function words that never make an answer specific to the change.
const FUNCTION_WORDS = new Set([
  'a', 'an', 'the', 'this', 'that', 'these', 'those', 'it', 'its', 'is', 'are', 'was', 'were',
  'be', 'been', 'being', 'and', 'or', 'nor', 'of', 'to', 'in', 'on', 'for', 'with', 'as', 'by',
  'at', 'from', 'so', 'no', 'not', 'none', 'na', 'n', 'any', 'all', 'do', 'does', 'did', 'done',
  'there', 'here', 'which', 'who', 'what', 'why', 'how', 'because', 'since', 'only', 'also',
  'but', 'if', 'then', 'than', 'very', 'just', 'we', 'i', 'you', 'they', 'our', 'their', 'has',
  'have', 'had', 'will', 'would', 'can', 'could', 'should', 'may', 'might', 'must', 'per', 'via',
  'etc', 'yes', 'own', 'into', 'out', 'up', 'about', 'over', 'under', 'still', 'already', 'e', 'g',
]);

// Vocabulary of the documentation-impact question itself. An answer that consists only of
// these words restates the question instead of answering it: for NOT REQUIRED it does not say
// what the change touches, and for UPDATED ("documentation file updated") it does not say what
// changed. `documented` is deliberately absent: it is the verb of an UPDATED answer, and on its
// own it never makes a NOT REQUIRED answer specific.
const CATEGORY_WORDS = new Set([
  'behaviour', 'behavior', 'behaviours', 'behaviors', 'contract', 'contracts', 'api', 'apis',
  'architecture', 'architectural', 'configuration', 'configurations', 'config', 'automation',
  'automated', 'deployment', 'deployments', 'deploy', 'operation', 'operations', 'operational',
  'user', 'users', 'workflow', 'workflows', 'documentation', 'docs', 'doc', 'document',
  'documents', 'unaffected', 'affected', 'affect', 'affects', 'affecting',
  'unchanged', 'change', 'changes', 'changed', 'changing', 'required', 'require', 'requires',
  'requirement', 'needed', 'need', 'needs', 'impact', 'impacts', 'impacted', 'applicable',
  'apply', 'applies', 'internal', 'implementation', 'fix', 'fixes', 'code', 'refactor',
  'refactoring', 'existing', 'current', 'same', 'remain', 'remains', 'remaining', 'public',
  'external', 'visible', 'facing', 'without', 'within', 'made', 'make', 'makes', 'update',
  'updates', 'updated', 'section', 'sections', 'file', 'files', 'pr', 'pull', 'request', 'issue',
  'task', 'work', 'scope', 'touch', 'touches', 'touched', 'alter', 'alters', 'altered', 'modify',
  'modifies', 'modified', 'introduce', 'introduces', 'introduced', 'new', 'nothing', 'anything',
  'something', 'therefore', 'hence', 'thus', 'reason', 'reasons', 'describe', 'described',
  'explain', 'explained', 'meaningful', 'evidence', 'details', 'detail', 'decision',
]);

// Placeholders left over from a template: `<meaningful evidence>`, `<file>`, an unterminated
// HTML comment. Type parameters such as `List<T>` are not placeholders.
const PLACEHOLDER_WORDS =
  /^(?:evidence|file|files|path|paths|section|sections|details?|description|explanation|reason|reasons|list|todo|tbd|text|value|answer|meaningful evidence|what changed)$/i;

// --- Text helpers -----------------------------------------------------------------------

export function normaliseBody(body) {
  if (typeof body !== 'string') {
    return '';
  }
  return body.replaceAll('\r\n', '\n').replaceAll('\r', '\n').replace(/<!--[\s\S]*?-->/g, '');
}

// GitHub renders an empty optional issue-form field as `_No response_`.
const NO_RESPONSE_PATTERN = /^_?No response_?$/i;

function cleanAnswer(text) {
  const lines = text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line !== '' && !NO_RESPONSE_PATTERN.test(line));
  return lines.join('\n');
}

export function normaliseWords(text) {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, ' ')
    .trim()
    .split(' ')
    .filter((word) => word !== '');
}

export function findPlaceholders(text) {
  const placeholders = [];
  for (const match of text.matchAll(/<([^<>\n]*)>/g)) {
    const inner = match[1].trim();
    if (/\s/.test(inner) || PLACEHOLDER_WORDS.test(inner)) {
      placeholders.push(match[0]);
    }
  }
  if (text.includes('<!--')) {
    placeholders.push('<!--');
  }
  return placeholders;
}

export function findDocumentationReferences(text) {
  const references = [];
  for (const match of text.matchAll(DOCUMENTATION_REFERENCE_PATTERN)) {
    references.push(match[1]);
  }
  return references;
}

// Distinct words that are neither function words nor documentation-impact vocabulary. Only
// these make an answer specific to the change; repeating one does not add specificity.
export function countSpecificWords(words) {
  return new Set(words.filter((word) => !FUNCTION_WORDS.has(word) && !CATEGORY_WORDS.has(word))).size;
}

/**
 * Judges whether one free-text answer is meaningful for the given decision.
 *
 * @param {string} text  The answer, with HTML comments already removed.
 * @param {object} options
 * @param {'documentation-updated' | 'documentation-not-required'} options.kind
 * @param {string} options.field  Name used in error messages.
 * @returns {string[]} errors; empty when the answer is acceptable.
 */
export function assessEvidence(text, { kind, field }) {
  const errors = [];
  const answer = cleanAnswer(text ?? '');

  if (answer === '') {
    errors.push(`${field} is empty. State the affected documentation or explain specifically why documentation is unaffected.`);
    return errors;
  }

  const placeholders = findPlaceholders(answer);
  if (placeholders.length > 0) {
    errors.push(`${field} still contains template placeholder text: ${placeholders.join(', ')}.`);
    return errors;
  }

  const words = normaliseWords(answer);
  const normalised = words.join(' ');
  if (GENERIC_ANSWERS.includes(normalised)) {
    errors.push(`${field} is a bare or generic answer ("${answer.replaceAll('\n', ' ')}"), which is not accepted.`);
    return errors;
  }

  if (kind === 'documentation-updated') {
    const references = findDocumentationReferences(answer);
    if (references.length === 0) {
      errors.push(
        `${field} must name at least one documentation file or section that changed ` +
          '(for example docs/automation.md, AGENTS.md, README, or a Markdown file).',
      );
    }
    // Documentation-impact vocabulary ("documentation file updated") does not say what changed
    // any more than the file name does, so it is excluded exactly as for NOT REQUIRED.
    const withoutReferences = answer.replace(DOCUMENTATION_REFERENCE_PATTERN, ' ');
    if (countSpecificWords(normaliseWords(withoutReferences)) < MINIMUM_SPECIFIC_WORDS) {
      errors.push(
        `${field} must say what changed in each documentation file: naming the file and adding generic words ` +
          'such as "documentation", "file", "section" or "updated" is not enough.',
      );
    }
    return errors;
  }

  if (words.length < MINIMUM_WORDS) {
    errors.push(
      `${field} is too short to be a meaningful explanation (${words.length} words; at least ${MINIMUM_WORDS} are required). ` +
        'Explain, for this specific change, why behaviour, contracts, architecture, configuration, automation, deployment, operations and user workflows are unaffected.',
    );
  }
  if (countSpecificWords(words) < MINIMUM_SPECIFIC_WORDS) {
    errors.push(
      `${field} is generic: it restates the documentation categories without saying what this change does or does not touch. ` +
        'Name the code, feature, report, endpoint or file involved and why documentation stays accurate.',
    );
  }
  return errors;
}

// --- Sections ---------------------------------------------------------------------------

const HEADING_PATTERN = /^(#{1,6})[ \t]+(.+?)[ \t]*#*[ \t]*$/;

/**
 * Returns every section whose heading text equals `label`, at heading levels 2 or 3. GitHub
 * renders issue-form fields as `### Label`; hand-written issues in this repository use `## Label`.
 * A section ends at the next heading of the same or a higher level.
 */
export function findSections(body, label, { levels = [2, 3] } = {}) {
  const lines = normaliseBody(body).split('\n');
  const wanted = label.trim().toLowerCase();
  const sections = [];
  let inFence = false;

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    if (/^\s*(```|~~~)/.test(line)) {
      inFence = !inFence;
      continue;
    }
    if (inFence) {
      continue;
    }
    const heading = HEADING_PATTERN.exec(line);
    if (!heading) {
      continue;
    }
    const level = heading[1].length;
    if (!levels.includes(level) || heading[2].trim().toLowerCase() !== wanted) {
      continue;
    }

    const content = [];
    let fenced = false;
    for (let cursor = index + 1; cursor < lines.length; cursor += 1) {
      const candidate = lines[cursor];
      if (/^\s*(```|~~~)/.test(candidate)) {
        fenced = !fenced;
      }
      if (!fenced) {
        const next = HEADING_PATTERN.exec(candidate);
        if (next && next[1].length <= level) {
          break;
        }
      }
      content.push(candidate);
    }
    sections.push({ level, line: index + 1, content: content.join('\n') });
  }
  return sections;
}

function requireSingleSection(body, label, errors) {
  const sections = findSections(body, label);
  if (sections.length === 0) {
    errors.push(`Missing required section "${label}".`);
    return null;
  }
  if (sections.length > 1) {
    errors.push(`Section "${label}" appears ${sections.length} times; exactly one is required.`);
    return null;
  }
  return sections[0];
}

// --- Issue body -------------------------------------------------------------------------

/**
 * Validates the documentation-impact declaration of an agent task issue.
 *
 * @param {string} body
 * @returns {{ valid: boolean, decision: string | null, errors: string[] }}
 */
export function validateIssueBody(body) {
  const errors = [];
  let decision = null;

  const decisionSection = requireSingleSection(body, ISSUE_DECISION_LABEL, errors);
  if (decisionSection) {
    const answer = cleanAnswer(decisionSection.content);
    const answerLines = answer === '' ? [] : answer.split('\n');
    if (answerLines.length === 0) {
      errors.push(`"${ISSUE_DECISION_LABEL}" is empty. Choose exactly one of: ${ISSUE_DECISIONS.map((d) => `"${d}"`).join(', ')}.`);
    } else if (answerLines.length > 1) {
      errors.push(`"${ISSUE_DECISION_LABEL}" must contain exactly one decision, but contains ${answerLines.length} lines.`);
    } else if (!ISSUE_DECISIONS.includes(answerLines[0])) {
      errors.push(
        `"${ISSUE_DECISION_LABEL}" is "${answerLines[0]}", which is not supported. ` +
          `Use exactly one of: ${ISSUE_DECISIONS.map((d) => `"${d}"`).join(', ')}.`,
      );
    } else {
      decision = answerLines[0];
    }
  }

  const detailsSection = requireSingleSection(body, ISSUE_DETAILS_LABEL, errors);
  if (detailsSection) {
    const kind = decision === ISSUE_DECISION_NOT_REQUIRED ? 'documentation-not-required' : 'documentation-updated';
    const field = `"${ISSUE_DETAILS_LABEL}"`;
    if (decision === null) {
      // Without a valid decision the details can only be checked for emptiness and placeholders.
      const answer = cleanAnswer(detailsSection.content);
      if (answer === '') {
        errors.push(`${field} is empty.`);
      } else {
        const placeholders = findPlaceholders(answer);
        if (placeholders.length > 0) {
          errors.push(`${field} still contains template placeholder text: ${placeholders.join(', ')}.`);
        } else if (GENERIC_ANSWERS.includes(normaliseWords(answer).join(' '))) {
          errors.push(`${field} is a bare or generic answer ("${answer.replaceAll('\n', ' ')}"), which is not accepted.`);
        }
      }
    } else {
      errors.push(...assessEvidence(detailsSection.content, { kind, field }));
    }
  }

  return { valid: errors.length === 0, decision, errors };
}

// --- Pull request body ------------------------------------------------------------------

function fieldLines(content, field) {
  // Accepts `Decision: x`, `**Decision**: x` and `**Decision:** x`.
  const pattern = new RegExp(`^[ \\t]*(?:\\*\\*)?${field}(?:\\*\\*)?[ \\t]*:[ \\t]*(?:\\*\\*)?[ \\t]*(.*)$`, 'i');
  const lines = content.split('\n');
  const matches = [];
  for (let index = 0; index < lines.length; index += 1) {
    const match = pattern.exec(lines[index]);
    if (match) {
      matches.push({ index, value: match[1].trim() });
    }
  }
  return { lines, matches };
}

/**
 * Validates the `## Documentation impact` section of a pull request body.
 *
 * @param {string} body
 * @returns {{ valid: boolean, decision: string | null, errors: string[] }}
 */
export function validatePullRequestBody(body) {
  const errors = [];
  let decision = null;

  const sectionMatch = requireSingleSection(body, PR_SECTION_HEADING, errors);
  if (!sectionMatch) {
    return { valid: false, decision, errors };
  }
  const content = sectionMatch.content;

  const decisions = fieldLines(content, PR_DECISION_FIELD);
  if (decisions.matches.length === 0) {
    errors.push(`Missing "${PR_DECISION_FIELD}:" line in the "${PR_SECTION_HEADING}" section.`);
  } else if (decisions.matches.length > 1) {
    errors.push(`"${PR_DECISION_FIELD}:" appears ${decisions.matches.length} times; exactly one decision is required.`);
  } else {
    const value = decisions.matches[0].value;
    if (value === '') {
      errors.push(`"${PR_DECISION_FIELD}:" is empty. Use exactly "${PR_DECISION_UPDATED}" or "${PR_DECISION_NOT_REQUIRED}".`);
    } else if (!PR_DECISIONS.includes(value)) {
      errors.push(
        `"${PR_DECISION_FIELD}: ${value}" is not supported. Use exactly "${PR_DECISION_UPDATED}" or "${PR_DECISION_NOT_REQUIRED}".`,
      );
    } else {
      decision = value;
    }
  }

  const evidence = fieldLines(content, PR_EVIDENCE_FIELD);
  if (evidence.matches.length === 0) {
    errors.push(`Missing "${PR_EVIDENCE_FIELD}:" line in the "${PR_SECTION_HEADING}" section.`);
  } else if (evidence.matches.length > 1) {
    errors.push(`"${PR_EVIDENCE_FIELD}:" appears ${evidence.matches.length} times; exactly one evidence entry is required.`);
  } else {
    const start = evidence.matches[0].index;
    const decisionIndexes = new Set(decisions.matches.map((match) => match.index));
    const evidenceText = [evidence.matches[0].value]
      .concat(evidence.lines.slice(start + 1).filter((_line, offset) => !decisionIndexes.has(start + 1 + offset)))
      .join('\n');
    const field = `"${PR_EVIDENCE_FIELD}"`;

    if (decision === null) {
      const answer = cleanAnswer(evidenceText);
      if (answer === '') {
        errors.push(`${field} is empty.`);
      } else {
        const placeholders = findPlaceholders(answer);
        if (placeholders.length > 0) {
          errors.push(`${field} still contains template placeholder text: ${placeholders.join(', ')}.`);
        } else if (GENERIC_ANSWERS.includes(normaliseWords(answer).join(' '))) {
          errors.push(`${field} is a bare or generic answer ("${answer.replaceAll('\n', ' ')}"), which is not accepted.`);
        }
      }
    } else {
      const kind = decision === PR_DECISION_UPDATED ? 'documentation-updated' : 'documentation-not-required';
      errors.push(...assessEvidence(evidenceText, { kind, field }));
    }
  }

  return { valid: errors.length === 0, decision, errors };
}

// --- Command line -----------------------------------------------------------------------

function printUsage() {
  console.error('Usage: node scripts/validate-documentation-impact.mjs (--issue-body <file> | --pr-body <file>)');
}

export function main(argv) {
  const [flag, file, ...rest] = argv;
  if (!flag || !file || rest.length > 0 || !['--issue-body', '--pr-body'].includes(flag)) {
    printUsage();
    return 2;
  }

  let body;
  try {
    body = readFileSync(resolve(file), 'utf8');
  } catch (error) {
    console.error(`Could not read ${file}: ${error.message}`);
    return 2;
  }

  const subject = flag === '--issue-body' ? 'issue' : 'pull request';
  const result = flag === '--issue-body' ? validateIssueBody(body) : validatePullRequestBody(body);
  const annotate = process.env.GITHUB_ACTIONS === 'true';

  if (result.valid) {
    console.log(`Documentation impact declaration of the ${subject} is valid (decision: ${result.decision}).`);
    return 0;
  }

  console.error(`Documentation impact declaration of the ${subject} is invalid:`);
  for (const error of result.errors) {
    console.error(`  - ${error}`);
    if (annotate) {
      console.log(`::error title=Documentation impact::${error}`);
    }
  }
  return 1;
}

const invokedDirectly =
  process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url);

if (invokedDirectly) {
  process.exitCode = main(process.argv.slice(2));
}
