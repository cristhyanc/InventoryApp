/**
 * The shape of a legal page's content (issue #1190's layout, filled with the owner-approved copy by
 * issue #1214): a title, an effective-date line, an optional introduction and a numbered list of
 * sections, each with its own in-page anchor. See `privacy-policy-content.ts`,
 * `terms-of-use-content.ts` and `docs/design/restockroo-legal-pages.md`.
 */

/** A bulleted list inside a section. */
export interface LegalPageList {
  readonly items: readonly string[];
}

/** One block of a section's body: a paragraph (a plain string) or a bulleted list. */
export type LegalPageBlock = string | LegalPageList;

export interface LegalPageSection {
  /** The in-page anchor the table of contents links to, and the section's own id. */
  readonly id: string;
  readonly heading: string;
  readonly body: readonly LegalPageBlock[];
}

export interface LegalPageContent {
  readonly title: string;
  readonly effectiveDateLabel: string;
  /** A short paragraph shown under the heading, when the page has one. */
  readonly introduction?: string;
  readonly sections: readonly LegalPageSection[];
}

/** One run of a paragraph's text: plain, or a link when `href` is set. */
export interface LegalTextSegment {
  readonly text: string;
  readonly href?: string;
}

/** The business display details every legal page shows (issue #1190's acceptance criteria). */
export const LEGAL_CONTACT = {
  operatorName: 'Eunhye Chung',
  abn: 'ABN 69 235 469 430',
  email: 'admin@restockroo.com.au'
} as const;

const TRAILING_PUNCTUATION = '.,;:)';

/** The link a whitespace-separated word stands for, or undefined when it is plain text. */
function linkFor(word: string): string | undefined {
  if (word.startsWith('https://')) {
    return word;
  }
  const at = word.indexOf('@');
  return at > 0 && word.indexOf('.', at) > at + 1 ? `mailto:${word}` : undefined;
}

/**
 * Splits a paragraph into plain text and links, so the copy can stay a plain string and still render
 * its email address and web addresses as real links - without ever binding HTML. It walks the text
 * word by word (no backtracking regular expression), and a link never swallows the sentence's own
 * closing punctuation.
 */
export function legalTextSegments(text: string): LegalTextSegment[] {
  const segments: LegalTextSegment[] = [];
  let plain = '';
  for (const part of text.split(/(\s+)/)) {
    let end = part.length;
    while (end > 0 && TRAILING_PUNCTUATION.includes(part[end - 1])) {
      end--;
    }
    const word = part.slice(0, end);
    const href = linkFor(word);
    if (!href) {
      plain += part;
      continue;
    }
    if (plain) {
      segments.push({ text: plain });
    }
    segments.push({ text: word, href });
    plain = part.slice(end);
  }
  if (plain) {
    segments.push({ text: plain });
  }
  return segments;
}

/** Whether a body block is a bulleted list rather than a paragraph. */
export function isLegalPageList(block: LegalPageBlock): block is LegalPageList {
  return typeof block !== 'string';
}
