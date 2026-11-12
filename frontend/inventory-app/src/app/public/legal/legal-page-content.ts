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

// An email address or an https URL. A URL never swallows the sentence's own closing punctuation.
const LINK_PATTERN = /([A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})|(https:\/\/[^\s,;]+[^\s,;.)])/g;

/**
 * Splits a paragraph into plain text and links, so the copy can stay a plain string and still render
 * its email address and web addresses as real links - without ever binding HTML.
 */
export function legalTextSegments(text: string): LegalTextSegment[] {
  const segments: LegalTextSegment[] = [];
  let last = 0;
  for (const match of text.matchAll(LINK_PATTERN)) {
    const index = match.index ?? 0;
    if (index > last) {
      segments.push({ text: text.slice(last, index) });
    }
    const [value, email] = match;
    segments.push({ text: value, href: email ? `mailto:${value}` : value });
    last = index + value.length;
  }
  if (last < text.length) {
    segments.push({ text: text.slice(last) });
  }
  return segments;
}

/** Whether a body block is a bulleted list rather than a paragraph. */
export function isLegalPageList(block: LegalPageBlock): block is LegalPageList {
  return typeof block !== 'string';
}
