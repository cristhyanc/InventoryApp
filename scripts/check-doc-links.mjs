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

export function listDocumentationFiles(root = repositoryRoot) {
  const docsDir = join(root, 'docs');
  const docFiles = readdirSync(docsDir, { recursive: true })
    .filter((entry) => entry.toLowerCase().endsWith('.md'))
    .map((entry) => join('docs', entry));
  return [...ROOT_DOCUMENTATION_FILES, ...docFiles].sort();
}

// --- Headings and anchors ----------------------------------------------------------------

const HEADING_PATTERN = /^(#{1,6})[ \t]+(.+?)[ \t]*#*[ \t]*$/;
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
// never written across a line break, excludes newlines. Fenced code blocks are stripped first, so
// an example link inside one is never checked as a real link.
const LINK_PATTERN = /(?<!!)\[[^\]]*\]\(([^)\n]+)\)/g;
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
    const absolutePath = join(root, relativePath);
    const markdown = readFileSync(absolutePath, 'utf8');

    for (const { line, target } of extractLinks(markdown)) {
      if (target === '' || SCHEME_PATTERN.test(target) || target.startsWith('//')) {
        continue;
      }

      const hashIndex = target.indexOf('#');
      const rawPathPart = hashIndex === -1 ? target : target.slice(0, hashIndex);
      const anchor = hashIndex === -1 ? null : target.slice(hashIndex + 1).toLowerCase();
      const pathPart = rawPathPart.split('?')[0];

      let decodedPathPart = pathPart;
      try {
        decodedPathPart = decodeURIComponent(pathPart);
      } catch {
        // Not valid percent-encoding: use the literal text, which is what the filesystem sees too.
      }
      const targetAbsolutePath = pathPart === '' ? absolutePath : resolve(dirname(absolutePath), decodedPathPart);

      if (!fileExists(targetAbsolutePath)) {
        broken.push({ file: relativePath, line, target, reason: `target does not exist: ${pathPart}` });
        continue;
      }

      if (anchor === null || anchor === '') {
        continue;
      }

      if (extname(targetAbsolutePath).toLowerCase() !== '.md') {
        // Not a Markdown file: GitHub still scrolls to a named element, but there is no
        // heading-derived anchor to verify here, so file existence above is all that applies.
        continue;
      }

      if (!anchorsFor(targetAbsolutePath).has(anchor)) {
        broken.push({ file: relativePath, line, target, reason: `no heading produces anchor "#${anchor}" in ${pathPart === '' ? relativePath : pathPart}` });
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
