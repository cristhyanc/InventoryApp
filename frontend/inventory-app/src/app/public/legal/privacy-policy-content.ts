import { LegalPageContent } from './legal-page-content';

const PLACEHOLDER = '[Approved content to be inserted]';

/**
 * The Privacy Policy's content (issue #1190): the Stitch export's own section headings, in order,
 * each holding the export's placeholder - no legal wording, no invented date. See
 * `docs/design/restockroo-legal-pages.md` for the export this is adapted from and every intentional
 * omission.
 */
export const PRIVACY_POLICY_CONTENT: LegalPageContent = {
  title: 'Restockroo Privacy Policy',
  effectiveDateLabel: '[Effective date]',
  introduction: PLACEHOLDER,
  sections: [
    { id: 'section-1', heading: 'Who We Are and What This Covers', body: PLACEHOLDER },
    { id: 'section-2', heading: 'Data We Collect', body: PLACEHOLDER },
    { id: 'section-3', heading: 'Purpose of Processing', body: PLACEHOLDER },
    { id: 'section-4', heading: 'Sub-processors & Infrastructure', body: PLACEHOLDER },
    { id: 'section-5', heading: 'Automated Indexing & Receipts', body: PLACEHOLDER },
    // The export's table of contents misspelled this "Sovereignity"; the section heading itself
    // did not, and the issue requires the typo fixed everywhere it appears.
    { id: 'section-6', heading: 'Data Sovereignty & Cloud Hosting', body: PLACEHOLDER },
    { id: 'section-7', heading: 'Cookies & Local Session Cache', body: PLACEHOLDER },
    { id: 'section-8', heading: 'Security & Ledger Retention', body: PLACEHOLDER },
    { id: 'section-9', heading: 'Australian Privacy Rights', body: PLACEHOLDER },
    { id: 'section-10', heading: 'Modifications & Revision Log', body: PLACEHOLDER }
  ]
};
