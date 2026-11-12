# Restockroo legal pages: design reference (issue #1190)

The Privacy Policy and Terms of Use page layouts
(`frontend/inventory-app/src/app/public/legal/`) implement the owner-approved Stitch designs. This
note identifies that design and records every place where the implementation intentionally differs
from it, so a later change - including the one that fills in the approved legal copy and publishes
the pages - can tell a deliberate adjustment from drift. How the pages are routed, how they became public
in every build (issue #1214) and why they make no request is in
[docs/architecture.md § The Privacy Policy and Terms of Use pages](../architecture.md#the-privacy-policy-and-terms-of-use-pages-issues-1190-and-1214).

## The approved export

- **Source:** the Stitch export attached to the first comment on
  [issue #1190](https://github.com/cristhyanc/InventoryApp/issues/1190), two desktop HTML files -
  `privacy-policy-desktop.html` and `terms-of-use-desktop.html`. No mobile export exists; the
  responsive behaviour below is adapted from the desktop export, the same way issue #954's landing
  page adapted its own export.
- **Authority:** rendered, the two files are the visual authority. Nothing from the export is
  committed to this repository, and none of its markup, Tailwind CDN script, inline Tailwind
  configuration, remote Google Fonts or Material Symbols links was copied - the same rule
  [docs/design/restockroo-landing-page.md](restockroo-landing-page.md) applied to its own export.
- **Content:** every section placeholder in the export reads `"[Approved content to be inserted]"`,
  and every date reads `"[Effective date]"`; the export contains no legal text. Issue #1214 replaced
  those placeholders with the owner-approved copy and effective dates (see
  [Published copy](#published-copy-issue-1214) below).

## Omissions (issue #1190's acceptance criteria)

The issue requires every one of these left out, because each is the export's own invented label,
action or decoration rather than something this application has:

| Export element | Omitted because |
| --- | --- |
| "Restockroo Operations / Legal & Regulatory" breadcrumb | Invented navigation structure; not a real section of the application. |
| "Restockroo Compliance Administration" (Privacy sign-off card) | Invented department name. |
| "Restockroo Governance" (Terms sign-off card) | Invented department name. |
| "Contents Ledger" table-of-contents label | Invented branding for an ordinary table of contents, which the implementation labels plainly ("Table of contents"). |
| "Contact Privacy Desk" / "Contact Support" buttons | Invented destinations. Replaced with the one plain `mailto:admin@restockroo.com.au` link the issue specifies. |
| The header's user avatar | Decorative chrome with nothing behind it on an unauthenticated page. |
| The header's "Start free" button | A second call to action beside Sign in and Get started, which the public layout already has on the real homepage; inventing a third here would duplicate it outside this issue's scope. |
| Privacy export's "Operational Disclosure & Scope" eyebrow label on the introduction callout | An invented decorative label around the one placeholder paragraph the export marks as content; the implementation keeps the placeholder paragraph itself (the issue lists an effective-date line and the sections as required, and the export's own placeholder is kept rather than dropped) without the invented badge around it. |
| Terms export's subtitle under its heading ("Stock management, supplier price comparisons and refill planning for vending operators.") | Not a placeholder - already-worded marketing copy, which the issue forbids inventing on a legal page. The Terms of Use layout has no introduction paragraph as a result; the Privacy Policy's own introduction is the export's actual `"[Approved content to be inserted]"` placeholder, which is kept. |
| The "Sovereignity" typo (Privacy table of contents entry 6) | Fixed to "Sovereignty" everywhere, as the issue requires; the export's own section heading was already spelled correctly. |

## How the export was adapted

| Export | Implementation |
| --- | --- |
| Tailwind from a CDN with an inline configuration, and a bespoke colour/spacing/typography scale | The application's own Tailwind build and the `rr` tokens, font and `rr-button-*`/`rr-*` styles the public landing page and `/sign-in` already use (issue #954, issue #957) - one shared visual language across every public page, not a second one per page. |
| Material Symbols from Google Fonts | The application's own `app-icon` geometry (`inventory_2` for the brand mark, `arrow_forward` rotated for "Back to home", as `/sign-in` already uses). |
| Two separate desktop-only pages, with a two-column layout (a sticky left-rail table of contents, up to a `lg` breakpoint) | One responsive layout per page: the table of contents and the business details move above the sections at narrow widths instead of beside them, and the body column has no fixed `max-w` wider than the viewport, so there is no horizontal scroll at 390px. |
| Two near-identical headers (brand, page nav links, Sign in/Start free, an avatar) and footers (brand, platform nav, compliance nav, a copyright line) | One minimal header (brand only, linking to `/home`) and one minimal footer (a copyright line), matching `/sign-in`'s own public-page chrome rather than reproducing the landing page's full marketing header and footer on an unlinked, placeholder-only page. |
| Interactive scroll-spy script highlighting the active table-of-contents entry while scrolling | Omitted. It is a progressive-enhancement nicety the export implements with inline `<script>`, which this application does not commit; the table of contents still scrolls to and focuses the chosen section (`LegalPageComponent.scrollToSection`, the landing page's own in-page navigation pattern). |
| Each sign-off card's "Authorised Operator" / "Principal Operator" framing | The business details are shown once, in the table-of-contents rail, without an invented title for who Eunhye Chung is beyond being the operator the ABN belongs to. |

## Published copy (issue #1214)

Issue #1214 put the owner-approved Privacy Policy and Terms of Use text into
`legal/privacy-policy-content.ts` and `legal/terms-of-use-content.ts`, verbatim, with the effective
dates the owner supplied, and made both pages public. It includes the receipt-scanning paragraph
([docs/legal/receipt-scanning-privacy-paragraph.md](../legal/receipt-scanning-privacy-paragraph.md),
issue #1202) in the Privacy Policy and the supplier price comparison clause
([docs/legal/supplier-price-comparison-clause.md](../legal/supplier-price-comparison-clause.md),
issue #1183) in the Terms of Use.

The homepage footer gained a **Legal** list linking to both pages. The legal pages' own header and
footer stay the minimal chrome described above; nothing else about the layouts changed.

What changed from the #1190 layout and from the owner's draft, deliberately:

| Change | Why |
| --- | --- |
| The Stitch export's section headings ("Who We Are and What This Covers", "Data Sovereignty & Cloud Hosting" and so on) are replaced by the approved draft's own eleven headings on each page, and each section's anchor is now a slug of its heading (for example `#who-we-are`) instead of `#section-N`. | The headings are part of the approved copy; the export's were design placeholders. |
| The draft's "Draft for review" banner, its opening **Review notes**, its **Implementation notes** and its **Evidence reviewed** list are not on either page. | The owner said to leave out the review notes; the other two are marked "not public policy text". |
| Privacy Policy section 5 opens with the receipt-scanning paragraph from `docs/legal/receipt-scanning-privacy-paragraph.md`. | The owner asked for the latest receipt-scanning disclosure; the draft has no receipt-scanning wording of its own, and section 5 is its AI section. |
| Privacy Policy section 6 publishes only its first paragraph. | The draft's second paragraph is a bracketed instruction to name verified overseas-processing countries that itself says "Do not publish this placeholder". |
| Terms of Use section 7's second paragraph is the supplier price comparison clause from `docs/legal/supplier-price-comparison-clause.md`, followed by the draft's two remaining sentences ("Review any AI-assisted match before relying on it. Reports are operational aids and do not replace professional accounting or tax advice."). | The owner asked for the latest supplier-comparison clause; it replaces the draft's own two shorter sentences on the same point. |
| Both pages share one effective date, `legal/legal-effective-date.ts`. | The draft calls for both pages to use their shared publication date when they go live together. |
| A section body is now a list of blocks - paragraphs and bulleted lists - and an email address or `https` address in the text renders as a link (`legalTextSegments`), without binding HTML. | The approved copy has bulleted lists, the contact email and the OAIC web address. |

## Follow-up

- **Legal review and consent tracking** remain outside the application's scope (issue #1214's
  exclusions): the pages publish the owner's copy as supplied.
