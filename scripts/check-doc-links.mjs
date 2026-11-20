// Deterministic checker for relative links and in-document anchors across the repository's hand
// maintained Markdown (issue #1260, found by audit #1229). It never calls GitHub: headings are
// turned into anchors with the same slug rule GitHub's own Markdown renderer uses, so a broken
// link is caught the moment it is written rather than the next time somebody actually clicks it.
//
// Usage:
//   node scripts/check-doc-links.mjs
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

// The root files this task names, plus every Markdown file under docs/. Screenshot READMEs are
// genuine hand-maintained documentation (they are not generated), so they are in scope too.
const ROOT_DOCUMENTATION_FILES = ['README.md', 'AGENTS.md', 'CLAUDE.md', 'CONTRIBUTING.md', 'SECURITY.md'];

// Code-unit order, so the scanned file list (and with it the report order) is the same on every
// machine instead of following the locale, as `localeCompare` would.
const compareStrings = (left, right) => {
  if (left === right) {
    return 0;
  }
  return left < right ? -1 : 1;
};

export function listDocumentationFiles(root = repositoryRoot) {
  const docsDir = join(root, 'docs');
  const docFiles = readdirSync(docsDir, { recursive: true })
    .filter((entry) => entry.toLowerCase().endsWith('.md'))
    .map((entry) => join('docs', entry));
  return [...ROOT_DOCUMENTATION_FILES, ...docFiles].sort(compareStrings);
}

// --- Headings and anchors ----------------------------------------------------------------

// An ATX heading, whose text runs to the end of the line. A closing `##` sequence and trailing
// whitespace need no alternative of their own here (and matching them with one would make the
// heading text's end ambiguous, so the match would backtrack): slugify below drops `#` and trims.
// Requiring a non-space first character keeps the text's start unambiguous against the separator.
const HEADING_PATTERN = /^(#{1,6})[ \t]+(\S.*)$/;
const FENCE_PATTERN = /^\s*(```|~~~)/;

// GitHub's own heading-to-anchor rule: lower-case, drop everything but letters, digits,
// underscores, hyphens and spaces (which silently strips Markdown emphasis/code/punctuation
// without rewriting the surrounding spacing), then turn the remaining whitespace into hyphens.
export function slugify(headingText) {
  return headingText
    .toLowerCase()
    .replace(/[^\w\- ]+/g, '')
    .trim()
    .replace(/\s+/g, '-');
}

// Every heading's anchor, keyed by the exact slug GitHub would assign — including GitHub's own
// "-1", "-2", ... suffix for a slug repeated later in the same document.
export function extractAnchors(markdown) {
  const anchors = new Set();
  const seen = new Map();
  let inFence = false;

  for (const line of markdown.split('\n')) {
    if (FENCE_PATTERN.test(line)) {
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
    const base = slugify(heading[2]);
    const count = seen.get(base) ?? 0;
    seen.set(base, count + 1);
    anchors.add(count === 0 ? base : `${base}-${count}`);
  }

  return anchors;
}

// --- Links ---------------------------------------------------------------------------------

// An inline Markdown link, `[text](target)`, excluding an image's `![text](target)`. The link text
// may wrap across lines (this repository hand-wraps prose), so only the target itself, which is
// never written across a line break, excludes newlines. Neither run accepts its own opening
// delimiter: an unclosed `[` or `(` would otherwise be rescanned from every following position, and
// bracketed link text still yields the same target from its innermost `[`. Fenced code blocks are
// stripped first, so an example link inside one is never checked as a real link.
const LINK_PATTERN = /(?<!!)\[[^[\]]*\]\(([^()\n]+)\)/g;
const SCHEME_PATTERN = /^[a-zA-Z][a-zA-Z0-9+.-]*:/;

function stripFencedCodeBlocks(markdown) {
  const lines = markdown.split('\n');
  let inFence = false;
  return lines
    .map((line) => {
      if (FENCE_PATTERN.test(line)) {
        inFence = !inFence;
        return '';
      }
      return inFence ? '' : line;
    })
    .join('\n');
}

function lineNumberAt(markdown, index) {
  let line = 1;
  for (let i = 0; i < index; i += 1) {
    if (markdown[i] === '\n') {
      line += 1;
    }
  }
  return line;
}

/**
 * @param {string} markdown
 * @returns {{ line: number, target: string }[]}
 */
export function extractLinks(markdown) {
  const scannable = stripFencedCodeBlocks(markdown);
  const links = [];
  for (const match of scannable.matchAll(LINK_PATTERN)) {
    const target = match[1].trim();
    links.push({ line: lineNumberAt(scannable, match.index), target });
  }
  return links;
}

// --- Checking one repository ---------------------------------------------------------------

function fileExists(path) {
  try {
    statSync(path);
    return true;
  } catch {
    return false;
  }
}

// Nothing in the repository can resolve these: an empty target, anything carrying a URL scheme
// (`https:`, `mailto:`) and a protocol-relative `//host/path`.
function isExternalTarget(target) {
  return target === '' || SCHEME_PATTERN.test(target) || target.startsWith('//');
}

// `dir/file.md?plain=1#anchor` split into the path part and the lower-cased anchor (null when the
// target names no anchor at all).
function splitTarget(target) {
  const hashIndex = target.indexOf('#');
  const rawPathPart = hashIndex === -1 ? target : target.slice(0, hashIndex);
  return {
    pathPart: rawPathPart.split('?')[0],
    anchor: hashIndex === -1 ? null : target.slice(hashIndex + 1).toLowerCase(),
  };
}

function decodePathPart(pathPart) {
  try {
    return decodeURIComponent(pathPart);
  } catch {
    // Not valid percent-encoding: use the literal text, which is what the filesystem sees too.
    return pathPart;
  }
}

/**
 * Why one link in the file `from` describes is broken, or null when it resolves.
 *
 * @param {string} target
 * @param {{ absolutePath: string, relativePath: string }} from
 * @param {(path: string) => Set<string>} anchorsFor
 * @returns {string | null}
 */
function brokenLinkReason(target, from, anchorsFor) {
  const { pathPart, anchor } = splitTarget(target);
  const targetAbsolutePath = pathPart === '' ? from.absolutePath : resolve(dirname(from.absolutePath), decodePathPart(pathPart));

  if (!fileExists(targetAbsolutePath)) {
    return `target does not exist: ${pathPart}`;
  }

  if (anchor === null || anchor === '') {
    return null;
  }

  if (extname(targetAbsolutePath).toLowerCase() !== '.md') {
    // Not a Markdown file: GitHub still scrolls to a named element, but there is no
    // heading-derived anchor to verify here, so file existence above is all that applies.
    return null;
  }

  if (anchorsFor(targetAbsolutePath).has(anchor)) {
    return null;
  }

  return `no heading produces anchor "#${anchor}" in ${pathPart === '' ? from.relativePath : pathPart}`;
}

/**
 * Checks every relative link and in-document anchor in `relativePaths` (repository-relative,
 * forward-slash paths) and returns one entry per broken link.
 *
 * @param {string[]} relativePaths
 * @param {string} root
 * @returns {{ file: string, line: number, target: string, reason: string }[]}
 */
export function checkDocumentationLinks(relativePaths, root = repositoryRoot) {
  const broken = [];
  const anchorsByFile = new Map();
  const anchorsFor = (path) => {
    if (!anchorsByFile.has(path)) {
      anchorsByFile.set(path, extractAnchors(readFileSync(path, 'utf8')));
    }
    return anchorsByFile.get(path);
  };

  for (const relativePath of relativePaths) {
    const from = { absolutePath: join(root, relativePath), relativePath };

    for (const { line, target } of extractLinks(readFileSync(from.absolutePath, 'utf8'))) {
      if (isExternalTarget(target)) {
        continue;
      }

      const reason = brokenLinkReason(target, from, anchorsFor);
      if (reason !== null) {
        broken.push({ file: relativePath, line, target, reason });
      }
    }
  }

  return broken;
}

// --- Command line ---------------------------------------------------------------------------

export function main() {
  const files = listDocumentationFiles();
  const broken = checkDocumentationLinks(files);

  if (broken.length === 0) {
    console.log(`Checked ${files.length} documentation files: no broken relative links or anchors.`);
    return 0;
  }

  console.error(`${broken.length} broken link(s):`);
  for (const { file, line, target, reason } of broken) {
    console.error(`  ${file}:${line}: [${target}] — ${reason}`);
  }
  return 1;
}

const invokedDirectly = process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url);

if (invokedDirectly) {
  process.exitCode = main();
}
