import { LEGAL_EFFECTIVE_DATE } from './legal-effective-date';
import { LegalPageContent } from './legal-page-content';

/**
 * The Terms of Use (issue #1214): the owner-approved copy, word for word, in the draft's own section
 * order and headings. The draft's review notes, implementation notes and evidence list are not
 * terms text and are left out, as the owner instructed. One deliberate placement, recorded in
 * `docs/design/restockroo-legal-pages.md`: section 7's supplier-price sentences are the latest
 * approved supplier price comparison clause from
 * `docs/legal/supplier-price-comparison-clause.md` (issue #1183), in place of the draft's own two
 * shorter sentences on the same point. Section 10 is the owner's later replacement, "Rights and
 * liability" (13 November 2026), in place of the draft's "Rights and responsibility".
 */
export const TERMS_OF_USE_CONTENT: LegalPageContent = {
  title: 'Restockroo Terms of Use',
  effectiveDateLabel: `Effective date: ${LEGAL_EFFECTIVE_DATE}`,
  sections: [
    {
      id: 'agreement-and-operator',
      heading: 'Agreement and operator',
      body: [
        'These terms govern your use of Restockroo, operated by Eunhye Chung, ABN 69 235 469 430. Contact: admin@restockroo.com.au. By accepting these terms when registering or joining a business, you agree to them. If you act for a business, you must have authority to do so. You must be at least 18 years old to create an account.'
      ]
    },
    {
      id: 'the-service',
      heading: 'The service',
      body: [
        'Restockroo provides tools for vending inventory, purchasing, refills, supplier-price comparisons and related reporting. Some features depend on a connected third-party service, such as Nayax, or on your plan. You are responsible for reviewing imported information and keeping your business records accurate.'
      ]
    },
    {
      id: 'free-introductory-pro-access',
      heading: 'Free introductory Pro access',
      body: [
        'During the introductory beta period, participating businesses receive Pro access without a subscription charge. No credit card is required for this offer, and free access does not authorise us to charge you later. The offer does not cover charges from your own third-party providers.',
        'The applicable end date will be shown in the service or communicated directly. We will provide at least 30 days’ notice before free Pro access ends, including available plans, prices, applicable limits and what happens if you do not subscribe. You will only incur subscription charges if you expressly choose a paid subscription and accept its pricing and billing terms.',
        'We will not delete your business data solely because you decline a paid subscription. Before reducing access, we will explain your options and provide a reasonable opportunity to export your data or request export assistance. Account closure and longer-term retention are addressed below and in the Privacy Policy. Storage and other clearly disclosed usage limits may apply during free access.'
      ]
    },
    {
      id: 'accounts-and-authorised-users',
      heading: 'Accounts and authorised users',
      body: [
        'Provide accurate registration information, protect your sign-in credentials and notify us promptly of suspected unauthorised access. Business administrators are responsible for granting appropriate access and removing access when no longer needed. You are responsible for activity you authorise, but these terms do not make you responsible for failures attributable to us.'
      ]
    },
    {
      id: 'acceptable-use',
      heading: 'Acceptable use',
      body: [
        'Do not use Restockroo unlawfully, access another business’s records without authority, upload malicious material, bypass access controls or deliberately disrupt the service. Only upload or connect information you have the right to use. Do not submit sensitive personal information or payment-card credentials into fields that do not require them.'
      ]
    },
    {
      id: 'your-data-and-our-software',
      heading: 'Your data and our software',
      body: [
        'You retain your rights in the data and documents you supply. You grant us permission to host, process, transmit and display them as needed to provide, secure and support the service and operate your authorised integrations. Product descriptions used for price matching may contribute to a shared catalogue as explained in the Privacy Policy; this permission does not permit publication of your private stock quantities or sales records.',
        'We and our licensors retain rights in the Restockroo software and branding. You receive permission to use the service for your business purposes while your account is active and you comply with these terms.'
      ]
    },
    {
      id: 'integrations-prices-and-ai-outputs',
      heading: 'Integrations, prices and AI outputs',
      body: [
        'Connecting Nayax or another service authorises the data exchange and operations described for that integration. You must have permission to connect the account and comply with that provider’s applicable terms. Disconnecting an integration stops future access as supported by the feature; it does not necessarily delete information already imported.',
        'Supplier comparisons are based on recorded offers from supported suppliers and do not cover every supplier or guarantee the lowest final purchase cost. Prices, availability and product matches may be incomplete, delayed or incorrect, including where automated collection or matching is used. Before purchasing, verify the exact product, pack size, GST treatment, location or membership conditions, availability, delivery charges and final total directly with the supplier. Nothing in these terms excludes, restricts or modifies rights or remedies that cannot lawfully be excluded.',
        'Review any AI-assisted match before relying on it. Reports are operational aids and do not replace professional accounting or tax advice.'
      ]
    },
    {
      id: 'beta-operation-and-changes',
      heading: 'Beta operation and changes',
      body: [
        'The service is in beta and may contain defects or experience interruptions. We will use reasonable care in providing it but do not promise uninterrupted availability or an error-free service. Keep copies of records essential to your business.',
        'We may improve or change features. Where a change materially reduces an existing service or changes these terms to your disadvantage, we will ordinarily provide at least 30 days’ notice and allow you to stop using the service and request export assistance. Urgent security or legal changes may take effect sooner, with notice as soon as reasonably practicable. This clause does not authorise automatic introduction of paid charges.'
      ]
    },
    {
      id: 'suspension-and-closure',
      heading: 'Suspension and closure',
      body: [
        'You may request account or business closure at admin@restockroo.com.au. We may restrict access where reasonably necessary to address a material breach, security incident, unlawful use or a legal requirement. Where practicable, we will explain the reason and allow you to resolve the issue before suspension. Immediate restrictions may be necessary to prevent harm.',
        'If we discontinue the service, we will give reasonable advance notice and an opportunity to export data, unless a legal or urgent security restriction prevents this. Following closure, we will handle deletion and retained records in accordance with the Privacy Policy and applicable obligations.'
      ]
    },
    {
      id: 'rights-and-liability',
      heading: 'Rights and liability',
      body: [
        'Nothing in these Terms excludes, restricts or modifies any right, guarantee or remedy that cannot lawfully be excluded, including under the Australian Consumer Law. The limits below apply only to the extent permitted by law.',
        'Restockroo provides inventory and purchasing tools to assist your business. You are responsible for checking supplier prices, pack sizes, availability and AI-assisted receipt matches before purchasing or saving records. Each party’s liability is reduced to the extent the other party’s acts or omissions caused or contributed to the loss.',
        'To the extent permitted by law, neither party is liable to the other for indirect or consequential loss arising from use of the service. Restockroo’s total liability to you for claims arising out of or relating to the service or these Terms is limited to the subscription fees you paid Restockroo in the 12 months before the first event giving rise to the claim.',
        'These limits do not apply to fraud, wilful misconduct, or any liability that cannot lawfully be limited.'
      ]
    },
    {
      id: 'disputes-and-applicable-law',
      heading: 'Disputes and applicable law',
      body: [
        'Please contact admin@restockroo.com.au first so we can try to resolve a concern promptly. These terms are governed by the laws of New South Wales, Australia. This does not prevent you from using a court, tribunal, regulator or statutory remedy available to you under applicable law.'
      ]
    }
  ]
};
