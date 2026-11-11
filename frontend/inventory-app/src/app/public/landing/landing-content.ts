/**
 * The copy and the example figures of the public Restockroo landing page (issue #954, its refill
 * and supplier comparison examples replaced by issue #1192).
 *
 * The source is the owner-approved Stitch export attached to the issue (see
 * `docs/design/restockroo-landing-page.md`). Where its wording claimed more than the application
 * does, the wording here is narrowed rather than the application extended, and that design note
 * records each change - so read it before "restoring" a phrase from the export.
 *
 * Everything below is static. The refill and supplier comparison examples are illustrative content
 * with a fictional site, machine and supplier offers: no figure on the landing page is read from
 * any business, and the page makes no API request.
 */

/** A section a header or footer link scrolls to. The id is the section element's own id. */
export interface LandingSection {
  readonly id: string;
  readonly label: string;
}

export const LANDING_SECTIONS: readonly LandingSection[] = [
  { id: 'features', label: 'Features' },
  { id: 'how-it-works', label: 'How it works' },
  { id: 'nayax', label: 'Nayax integration' },
  { id: 'faqs', label: 'FAQs' }
];

export interface LandingFeature {
  readonly icon: string;
  readonly title: string;
  readonly description: string;
}

export const LANDING_FEATURES: readonly LandingFeature[] = [
  {
    icon: 'widgets',
    title: 'Products and categories',
    description: 'Organise products, units and categories, with an average cost per unit for each product.'
  },
  {
    icon: 'swap_horiz',
    title: 'Stock on hand and stock movements',
    description: 'Keep stock on hand accurate, with a recorded history of every stock movement and count.'
  },
  {
    icon: 'receipt',
    title: 'Purchases and receipts',
    description: 'Record supplier purchases, attach the receipt and review your purchase history.'
  },
  {
    icon: 'storefront',
    title: 'Machines and sites',
    description: 'Keep your own list of vending machines. Connect Nayax to see the sites they are installed at.'
  },
  {
    icon: 'analytics',
    title: 'Inventory value at cost',
    description: 'See your inventory valued at cost on the dashboard, with average unit costs kept up to date as purchases are recorded.'
  },
  {
    icon: 'local_shipping',
    title: 'Suppliers and ordering',
    description: 'Keep supplier details in one place and track the orders you place with them.'
  }
];

export const CORE_INVENTORY_POINTS: readonly string[] = [
  'Products & categories catalogue',
  'Stock on hand tracking & movements',
  'Purchase dockets & receipt records',
  'Machine records'
];

export const NAYAX_POINTS: readonly string[] = [
  'Supported vending sales data',
  'Sales and bookkeeping reports',
  'Machine and site details',
  'Connect credentials when ready'
];

export interface LandingFact {
  readonly title: string;
  readonly description: string;
}

export const INTEGRATION_FACTS: readonly LandingFact[] = [
  {
    title: 'Standalone out of the box',
    description: 'Record stock movements and counts by hand at any time, with no hardware needed.'
  },
  {
    title: 'Optional sales connection',
    description: 'Nayax is open to every business today. Connect it for sales integration whenever you are ready.'
  },
  {
    title: 'Optional integration',
    description: 'Connecting Nayax stays optional: core inventory keeps working without it.'
  }
];

export interface LandingStep {
  readonly number: string;
  readonly title: string;
  readonly description: string;
  readonly icon: string;
  readonly tag: string;
}

export const LANDING_STEPS: readonly LandingStep[] = [
  {
    number: '01',
    title: 'Set up your business',
    description: 'Sign in and submit your business details. Your workspace opens once the application is approved.',
    icon: 'domain',
    tag: 'Business application'
  },
  {
    number: '02',
    title: 'Add products and machines',
    description: 'Enter your products, categories and unit costs, your machines, and your opening stock quantities.',
    icon: 'inventory_2',
    tag: 'Product & machine setup'
  },
  {
    number: '03',
    title: 'Plan and record refills',
    description: 'Plan your next refill with the Pick List, then record the purchases and stock movements it drives.',
    icon: 'sync_alt',
    tag: 'Refill & stock history'
  }
];

export interface LandingFaq {
  readonly question: string;
  readonly answer: string;
}

export const LANDING_FAQS: readonly LandingFaq[] = [
  {
    question: 'Do I need Nayax?',
    answer: 'No. Core inventory features work without connecting a sales provider.'
  },
  {
    question: 'Is Restockroo suitable for vending businesses?',
    answer:
      'Yes. Organise products, machines and purchases, with optional Nayax integration for supported sales data and related reports.'
  },
  {
    question: 'Can I manage purchases and suppliers?',
    answer: 'Yes. Record purchases, attach receipts and keep supplier information together.'
  },
  {
    question: 'Can I use Restockroo on my phone?',
    answer: 'Yes. Restockroo runs in your web browser, and its layout adapts to mobile screens. There is no app to install.'
  }
];

/**
 * The refill example (issue #1192): one site and machine, with a short pick list of products to
 * pack. "Loaded" is how many of the "To pack" quantity have been packed so far in the same Pick
 * List run - it is not a second, separately recorded quantity.
 */
export interface LandingRefillItem {
  readonly product: string;
  readonly toPack: number;
  readonly loaded: number;
}

export const LANDING_REFILL_SITE = 'Banksia House';
export const LANDING_REFILL_MACHINE = 'Banksia House VM';

export const LANDING_REFILL_ITEMS: readonly LandingRefillItem[] = [
  { product: 'Coke - Original 375mL', toPack: 24, loaded: 24 },
  { product: "Smith's Chips - Original 45g", toPack: 12, loaded: 8 },
  { product: 'Red Bull - Original 250mL', toPack: 10, loaded: 0 },
  { product: 'Kit Kat - 4 Finger 45g', toPack: 8, loaded: 8 }
];

/** What the refill example says "Loaded" means, so it is never read as its own tracked record. */
export const LANDING_REFILL_LOADED_NOTE =
  "Loaded is how many of each product you've packed so far in this Pick List run, not a separate record.";

/** Whether every unit of a refill item's planned quantity has been packed. */
export function refillItemIsPacked(item: LandingRefillItem): boolean {
  return item.loaded >= item.toPack;
}

/**
 * The supplier comparison example (issue #1192): one product, the supplier offers the owner's
 * design shows, and the badges and notices that go with them. `unitPriceIncGst` decides the
 * "Lowest unit price" badge, computed rather than stored per row, so the three tied 30-pack rows
 * stay in step with the figures above them if either ever changes.
 */
export interface LandingSupplierOffer {
  readonly supplier: string;
  readonly packSize: string;
  readonly packPrice: number;
  readonly unitPriceIncGst: number;
  readonly unitPriceExGst: number;
  readonly special: boolean;
  readonly availability: 'In stock' | 'Stock unknown';
  readonly lastChecked: string;
}

export const LANDING_SUPPLIER_COMPARISON_PRODUCT = 'Coke - Original 375mL';
export const LANDING_SUPPLIER_COMPARISON_CATEGORY = 'Drinks';

export const LANDING_SUPPLIER_OFFERS: readonly LandingSupplierOffer[] = [
  { supplier: 'Coles', packSize: '30 pack', packPrice: 32.0, unitPriceIncGst: 1.07, unitPriceExGst: 0.97, special: false, availability: 'In stock', lastChecked: '11/11/2026, 05:17 am' },
  { supplier: 'Woolworths', packSize: '30 pack', packPrice: 32.0, unitPriceIncGst: 1.07, unitPriceExGst: 0.97, special: true, availability: 'In stock', lastChecked: '11/11/2026, 05:17 am' },
  { supplier: 'Big W', packSize: '30 pack', packPrice: 32.0, unitPriceIncGst: 1.07, unitPriceExGst: 0.97, special: false, availability: 'In stock', lastChecked: '08/11/2026, 09:10 am' },
  { supplier: 'Coles', packSize: '24 pack', packPrice: 27.0, unitPriceIncGst: 1.13, unitPriceExGst: 1.02, special: true, availability: 'In stock', lastChecked: '11/11/2026, 05:17 am' },
  { supplier: 'Big W', packSize: '24 pack', packPrice: 27.0, unitPriceIncGst: 1.13, unitPriceExGst: 1.02, special: false, availability: 'In stock', lastChecked: '08/11/2026, 09:10 am' },
  { supplier: 'Woolworths', packSize: '10 pack', packPrice: 11.5, unitPriceIncGst: 1.15, unitPriceExGst: 1.05, special: true, availability: 'In stock', lastChecked: '11/11/2026, 05:17 am' },
  { supplier: 'ALDI', packSize: '18 pack', packPrice: 20.99, unitPriceIncGst: 1.17, unitPriceExGst: 1.06, special: false, availability: 'Stock unknown', lastChecked: '11/11/2026, 05:17 am' }
];

export const LANDING_SUPPLIER_COMPARISON_HEADING = 'Compare prices across supported suppliers';
export const LANDING_SUPPLIER_EXAMPLE_NOTICE = 'Example comparison — prices and availability change.';
export const LANDING_SUPPLIER_CONFIRM_NOTICE = 'Confirm the product, pack size and final price with the supplier before buying.';
export const LANDING_LOWEST_UNIT_PRICE_LABEL = 'Lowest unit price';
export const LANDING_SPECIAL_BADGE_LABEL = 'Special';
export const LANDING_ONLINE_PRICE_NOTE = 'Online price, may differ in store';

/** The badges a row carries: the owner's design shows "Lowest unit price" before "Special". */
export function landingSupplierOfferBadges(offer: LandingSupplierOffer, offers: readonly LandingSupplierOffer[]): readonly string[] {
  const badges: string[] = [];
  if (isLandingLowestUnitPrice(offer, offers)) {
    badges.push(LANDING_LOWEST_UNIT_PRICE_LABEL);
  }
  if (offer.special) {
    badges.push(LANDING_SPECIAL_BADGE_LABEL);
  }
  return badges;
}

/** Whether an offer ties for the lowest unit price (inc GST) among the offers shown. */
export function isLandingLowestUnitPrice(offer: LandingSupplierOffer, offers: readonly LandingSupplierOffer[]): boolean {
  const lowest = Math.min(...offers.map((candidate) => candidate.unitPriceIncGst));
  return offer.unitPriceIncGst === lowest;
}

/** `$1.07`, the one money format this example uses. */
export function landingMoney(value: number): string {
  return `$${value.toFixed(2)}`;
}

/** The browser tab title and the description meta tag the landing page publishes while it is shown. */
export const LANDING_TITLE = 'Restockroo · Inventory management for products, purchases and vending machines';
export const LANDING_DESCRIPTION =
  'Restockroo tracks stock on hand, stock movements, purchases and suppliers for small businesses and vending ' +
  'operators. Core inventory works on its own; connecting Nayax is optional.';
