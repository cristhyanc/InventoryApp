import { LEGAL_EFFECTIVE_DATE } from './legal-effective-date';
import { LegalPageContent } from './legal-page-content';

/**
 * The Privacy Policy (issue #1214): the owner-approved copy, word for word, in the draft's own
 * section order and headings. The draft's opening review notes, its implementation notes and its
 * evidence list are not policy text and are left out, as the owner instructed. Two deliberate
 * placements, both recorded in `docs/design/restockroo-legal-pages.md`:
 *
 * - section 5 opens with the receipt-scanning disclosure from
 *   `docs/legal/receipt-scanning-privacy-paragraph.md` (issue #1202), the latest approved wording;
 * - section 6 carries only its first paragraph. The draft's bracketed instruction to name verified
 *   overseas countries says itself not to publish it.
 */
export const PRIVACY_POLICY_CONTENT: LegalPageContent = {
  title: 'Restockroo Privacy Policy',
  effectiveDateLabel: `Effective date: ${LEGAL_EFFECTIVE_DATE}`,
  sections: [
    {
      id: 'who-we-are',
      heading: 'Who we are',
      body: [
        'Restockroo is operated by Eunhye Chung, ABN 69 235 469 430 (“we”, “us” or “our”). This policy explains how we handle personal information when you use Restockroo or contact us. Contact us at admin@restockroo.com.au.'
      ]
    },
    {
      id: 'information-we-collect',
      heading: 'Information we collect',
      body: [
        'We collect information you provide, information supplied by your business’s authorised users and connected services, and technical information generated when the service is used. Depending on the features you use, this includes:',
        {
          items: [
            'Account identifiers, name, email address, business membership, role and account settings.',
            'Business contact information and information entered about sites, machines, suppliers, products, purchases, stock movements, expenses and sales.',
            'Receipts, invoices and other uploaded attachments, which may contain personal information.',
            'Information from an authorised Nayax connection, including operator and machine identifiers, product and sales records, and credentials needed to maintain the connection.',
            'Support correspondence and information you submit when reporting a problem.',
            'Technical, diagnostic and audit records about requests, errors, sign-in, administrative actions and service activity. Depending on platform settings, these may include device/browser information, network information and identifiers associated with an action.'
          ]
        },
        'Sign-in is handled through supported identity providers, including Microsoft and, where enabled, Google through Microsoft Entra External ID. We receive the account information needed to authenticate you and manage access. Please do not include sensitive personal information or payment-card details in product names or other free-text fields.'
      ]
    },
    {
      id: 'how-we-use-information',
      heading: 'How we use information',
      body: [
        'We use information to provide inventory and reporting features, authenticate users, manage business access, process authorised integrations, respond to enquiries, diagnose faults, prevent misuse and administer the service. We may also use it to meet legal obligations and resolve disputes. Necessary service notices are separate from optional promotional communications.',
        'If you do not provide information needed for an account or feature, we may be unable to provide it. You can make a general enquiry without creating an account, although we may need to verify identity before disclosing or changing account information.'
      ]
    },
    {
      id: 'who-receives-information',
      heading: 'Who receives information',
      body: [
        'Authorised members of your business can access information according to their permissions. Authorised support personnel may access relevant information when needed to investigate issues, maintain the service or protect security.',
        'We use service providers for hosting, authentication, diagnostics and integrations. Relevant services include Microsoft Azure and Microsoft Entra, Google when you choose Google sign-in, and Nayax when your business connects it. Where enabled, Amazon Web Services SQS transports Nayax sales events. Providers receive information needed for their role. We may also disclose information when required or authorised by law, or to advisers assisting with a dispute or legal obligation.'
      ]
    },
    {
      id: 'ai-and-supplier-price-collection',
      heading: 'AI and supplier-price collection',
      body: [
        'When you scan a receipt, Restockroo sends the receipt image or PDF, along with the names and IDs of your active products and suppliers, to Anthropic so that it can read the receipt and suggest matches. The scan does not save the receipt file or a purchase draft in Restockroo. Restockroo counts each scan against your plan’s monthly scan allowance. When a scan succeeds, the receipt is attached to the suggested purchase, and it is stored with the purchase when you save it unless you remove it first.',
        'Restockroo uses Anthropic Claude and supporting automation, including Apify and GitHub Actions, for supplier-price searches and product matching. That process uses product descriptions, brands, sizes, barcodes, supplier listings and search terms. It can include product search text derived from products entered by users.',
        'The reviewed collector interface does not include business names or IDs, user emails, stock quantities, customer sales records or receipt attachments in its work payload. However, personal information entered into product descriptions or search text could be included. Avoid entering personal or confidential information in those fields. Product descriptions and matching information may be used to maintain the shared product catalogue; this does not make a business’s stock levels or sales public.',
        'Collection transcripts and results may be retained in operational logs. This policy does not promise that every third-party AI service has zero retention or that a particular provider training setting applies; those depend on the arrangements in place. Any materially different use of personal information for AI features will be explained before it is introduced.'
      ]
    },
    {
      id: 'hosting-and-overseas-processing',
      heading: 'Hosting and overseas processing',
      body: [
        'Our primary application hosting is Microsoft Azure Australia East. Hosting the application in Australia does not mean every provider processes information only in Australia. Authentication, diagnostics, AI-assisted collection and integration services may involve overseas processing.'
      ]
    },
    {
      id: 'browser-storage',
      heading: 'Browser storage',
      body: [
        'Restockroo uses browser storage to support authentication and application operation. Identity providers may use their own cookies and browser storage during sign-in. Blocking or clearing this storage may sign you out or prevent features from working. Any non-essential analytics or advertising technologies introduced later will be described and managed as required by applicable law.'
      ]
    },
    {
      id: 'security-and-retention',
      heading: 'Security and retention',
      body: [
        'We take reasonable technical and organisational steps to protect information from unauthorised access, loss or misuse. No online service can guarantee absolute security.',
        'We retain information for as long as reasonably needed to provide the service, maintain security, resolve disputes and meet applicable recordkeeping obligations. Account closure does not necessarily remove every record immediately: restricted backups, audit records or legally required records may remain for their applicable retention period. We will explain relevant retention when responding to a deletion request.'
      ]
    },
    {
      id: 'access-correction-and-deletion',
      heading: 'Access, correction and deletion',
      body: [
        'Email admin@restockroo.com.au to request access to, correction of, or deletion of your personal information, or assistance exporting your business data. We may verify your identity and authority. A request to delete a personal account may need to be distinguished from deleting shared business records that other authorised users still need. Where we cannot fulfil a request, we will explain why, subject to legal restrictions.'
      ]
    },
    {
      id: 'questions-and-complaints',
      heading: 'Questions and complaints',
      body: [
        'Send privacy enquiries or complaints to admin@restockroo.com.au, describing the issue and your preferred response method. We aim to respond to privacy complaints within 30 days; if more time is needed, we will explain the delay. If you remain dissatisfied, you may contact the Office of the Australian Information Commissioner at https://www.oaic.gov.au, where the matter falls within its jurisdiction.'
      ]
    },
    {
      id: 'changes',
      heading: 'Changes',
      body: [
        'We may update this policy to reflect changes in the service or information handling. We will publish the revised date and provide an appropriate notice of material changes. An updated policy does not replace any consent or other legal basis required for a new use of information.'
      ]
    }
  ]
};
