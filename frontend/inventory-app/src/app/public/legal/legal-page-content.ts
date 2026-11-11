/**
 * The shape of a legal page's content (issue #1190): a title, an effective-date line, an optional
 * introduction and a numbered list of sections, each with its own in-page anchor. Every string here
 * is either the Stitch export's own section heading or one of its two placeholders -
 * `"[Approved content to be inserted]"` and `"[Effective date]"` - because the legal copy is not
 * approved yet. See `privacy-policy-content.ts`, `terms-of-use-content.ts` and
 * `docs/design/restockroo-legal-pages.md` for what each page actually holds and why.
 */

export interface LegalPageSection {
  /** The in-page anchor the table of contents links to, and the section's own id. */
  readonly id: string;
  readonly heading: string;
  readonly body: string;
}

export interface LegalPageContent {
  readonly title: string;
  readonly effectiveDateLabel: string;
  /** A short placeholder paragraph shown under the heading, when the export has one. */
  readonly introduction?: string;
  readonly sections: readonly LegalPageSection[];
}

/** The business display details every legal page shows (issue #1190's acceptance criteria). */
export const LEGAL_CONTACT = {
  operatorName: 'Eunhye Chung',
  abn: 'ABN 69 235 469 430',
  email: 'admin@restockroo.com.au'
} as const;
