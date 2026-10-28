# Restockroo landing page: design reference (issue #954)

The public landing page (`frontend/inventory-app/src/app/public/landing/`) implements the
owner-approved Stitch design. This note identifies that design and records every place where the
implementation intentionally differs from it, so a later change can tell a deliberate adjustment
from drift. How the page is routed and why it makes no request is in
[docs/architecture.md § The public landing page](../architecture.md#the-public-landing-page-issue-954).

## The approved export

- **File:** `stitch_inventory_app_landing_page (3).zip`, attached by the owner to
  [issue #954](https://github.com/cristhyanc/InventoryApp/issues/954#issuecomment-6458957741) as the
  final approved design. It supersedes every earlier export.
- **Contents used:** `restockroo_implementation_homepage_desktop/{code.html,screen.png}`,
  `restockroo_implementation_homepage_mobile/{code.html,screen.png}` and `field_ledger/DESIGN.md`.
- **Authority:** the two screenshots are the visual authority; the HTML and `DESIGN.md` were read as
  references only. Nothing from the export is committed to this repository, and none of its markup,
  Tailwind CDN script, inline Tailwind configuration, remote Google Fonts or Material Symbols links,
  or its menu script was copied.
- **Owner's design source:** <https://stitch.withgoogle.com/projects/5450834198659954552> (owner
  access may be required).

## How the export was adapted

| Export | Implementation |
| --- | --- |
| Tailwind from a CDN with an inline configuration | The application's own Tailwind build. The export's colours, which disagree between the two HTML files and `DESIGN.md`, are consolidated from the screenshots into one `rr` scale in `tailwind.config.js` (navy text, slate body copy, teal actions, a light lavender canvas), asserted for contrast in `design-tokens.contrast.spec.ts`. |
| Hanken Grotesk and JetBrains Mono from Google Fonts | Hanken Grotesk bundled through `@fontsource/hanken-grotesk`, as Inter already is, and the platform monospace stack (`font-mono`) for the figures that use a monospace face. No font is fetched from a third party. |
| Material Symbols from Google Fonts | The application's own `app-icon` geometry. Glyphs the export draws with a symbol the bundled Rounded set has no single-path equivalent for use the nearest bundled glyph: `widgets` for products and categories, `receipt` for purchases, `analytics` for reporting, and `inventory_2` for the brand mark (the mobile export's own choice). |
| Desktop and mobile as two separate pages | One responsive page with the same sections in the same order at every width. |
| The desktop export's "CORE INVENTORY", "WORKFLOW" and "QUESTIONS" eyebrows | The mobile export's section labels, which the issue restores: "Practical inventory management", "Features", "Optional integration", "How it works", "Frequently asked questions". The workflow heading is the desktop's "Simple three-step routine"; the FAQ heading is the mobile's "Questions & answers", so it does not repeat its own label. |
| Privacy Policy, Terms of Service and Contact links to `#privacy`, `#terms`, `#contact` | Omitted. The application has no privacy, terms or contact page, and the issue forbids dead links and invented legal or contact content. See the follow-up below. |
| Hash links styled as page routes (`#signin`, `#get-started`) | Real actions: **Sign in** uses the existing sign-in decision, **Get started** opens the existing business application route, and a signed-in person gets **Open app** instead. |

## Intentional copy changes

The issue requires every capability claim to match what the application does today, narrowing the
wording rather than adding features. These are the changes, each checked against the current code:

| Approved copy | Implemented copy | Why |
| --- | --- | --- |
| Products: "Organise items, pack sizes, product categories and cost per unit." | "Organise products, units and categories, with an average cost per unit for each product." | A product has a unit and an average unit cost, but no pack size. |
| Stock: "Keep an accurate ledger of stock across storage rooms and machine locations." | "Keep stock on hand accurate, with a recorded history of every stock movement and count." | Stock on hand is one quantity per product; it is not held per storage room or machine. Stock history and counts exist. |
| Purchases: "attach digital invoice dockets and track purchase history." | "attach the receipt and review your purchase history." | A purchase carries one attached document. |
| Machines: "Map out machines, assign sites and keep a clear record of where your products live." | "Keep your own list of vending machines. Connect Nayax to see the sites they are installed at." | Local machine records exist; sites are known only from the Nayax fleet read. |
| Reporting: "Review inventory valuation at cost, calculate margins and track cost changes over time." | "Review your inventory value at cost, with average unit costs kept up to date as purchases are recorded." | Inventory value at cost and average costing exist without Nayax; margin and profit figures come from Nayax sales reports. |
| Suppliers: "Keep supplier details, order frequencies and supplier pricing in one central place." | "Keep supplier details in one place and track the orders you place with them." | There is no order frequency; supplier orders exist. |
| Core inventory box: "Site & machine inventory mapping" | "Machine records" | As for the machines card. |
| Nayax box: "Supported vending sales data feed", "Cashless transaction summaries", "Combined sales & inventory reports" | "Supported vending sales data", "Sales and bookkeeping reports", "Machine and site details" | Avoids implying a live feed or telemetry, and names the reports that exist. |
| "100% standalone out-of-the-box … without hardware dependencies." | "Standalone out of the box. Record stock movements and counts by hand at any time, with no hardware needed." | Plain wording without the absolute claim. |
| Step 1: "Set up your company workspace and organisation settings." | "Sign in and submit your business details. Your workspace opens once the application is approved." | A business is created by an application that a platform administrator approves. |
| Step 2: "Enter your product catalogue lines, categories, cost prices and initial quantities." | "Enter your products, categories, unit costs and opening quantities." | Same meaning, the application's own terms. |
| Step 3: "Log incoming supplier dockets, attach receipts and update machine stock." | "Log supplier purchases with their receipts and record stock movements as they happen." | Stock is not held per machine. |
| Step tags: "Company profile setup", "Ledger & restocking records" | "Business application", "Purchase & stock history" | As for the steps. |
| FAQ: "Organise products, machines and sites, with optional Nayax integration …" | "Organise products, machines and purchases, with optional Nayax integration …" | Sites need Nayax. |
| FAQ: "Yes. The responsive web interface adapts to mobile screens." | "Yes. Restockroo runs in your web browser, and its layout adapts to mobile screens. There is no app to install." | States plainly that there is no native app. |
| Dashboard: "Stock on hand … across active locations" | "across active products" | Stock is not held per location. |
| Dashboard: "Inventory at cost … landed inventory valuation" / "Calculated cost" | "valued at average unit cost" | The valuation is the average-cost inventory value. |
| Dashboard machines table: a per-machine "Stock level" percentage and an "Operational"/"Review" status | A "Products" count per machine | A fill percentage implies live machine telemetry, which the issue rules out. |
| Dashboard stock table: "Supplier benchmark" with "Primary:" prices | "Supplier · last unit cost" | A purchase records a unit cost per product; there is no benchmark. |

The required copy is unchanged: the headline "Know your stock. Understand your costs.", the
integration heading "Core inventory works on its own. Connect Nayax when you need it.", the three
step titles, the four FAQ questions, the "Example dashboard · Sample data" label and every sample
figure the issue lists.

## Follow-up

- **Privacy, terms and contact.** The footer has no Privacy Policy, Terms of Service or Contact link
  because none of those destinations exists. Authoring them is legal and business work outside
  issue #954; once a real page or address exists, add the link to the footer.
