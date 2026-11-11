import { LegalPageContent } from './legal-page-content';

const PLACEHOLDER = '[Approved content to be inserted]';

/**
 * The Terms of Use's content (issue #1190): the Stitch export's own clause headings, in order, each
 * holding the export's placeholder - no legal wording, no invented date. Unlike the Privacy Policy,
 * the export's Terms page has no placeholder introduction under its heading - only an already-worded
 * marketing tagline, which is left out for the same reason the design note gives for the export's
 * other invented copy. See `docs/design/restockroo-legal-pages.md`.
 */
export const TERMS_OF_USE_CONTENT: LegalPageContent = {
  title: 'Restockroo Terms of Use',
  effectiveDateLabel: '[Effective date]',
  sections: [
    { id: 'section-1', heading: 'Agreement to Terms', body: PLACEHOLDER },
    { id: 'section-2', heading: 'Description of the Service', body: PLACEHOLDER },
    { id: 'section-3', heading: 'Public Beta Access & Free Pro Tier', body: PLACEHOLDER },
    { id: 'section-4', heading: 'User Accounts & Responsibilities', body: PLACEHOLDER },
    { id: 'section-5', heading: 'Acceptable Use Policy', body: PLACEHOLDER },
    { id: 'section-6', heading: 'Data Ownership & Intellectual Property', body: PLACEHOLDER },
    { id: 'section-7', heading: 'Third-Party Integrations & Supplier Data', body: PLACEHOLDER },
    { id: 'section-8', heading: 'Service Availability & Beta Disclaimers', body: PLACEHOLDER },
    { id: 'section-9', heading: 'Termination & Account Closure', body: PLACEHOLDER },
    { id: 'section-10', heading: 'Governing Law & Dispute Resolution', body: PLACEHOLDER }
  ]
};
