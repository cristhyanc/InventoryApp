/**
 * The copy and the sample figures of the public Restockroo landing page (issue #954).
 *
 * The source is the owner-approved Stitch export attached to the issue (see
 * `docs/design/restockroo-landing-page.md`). Where its wording claimed more than the application
 * does, the wording here is narrowed rather than the application extended, and that design note
 * records each change - so read it before "restoring" a phrase from the export.
 *
 * Everything below is static. The example dashboard is illustrative content with synthetic site
 * and supplier names: no figure on the landing page is read from any business.
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
    title: 'Inventory and cost reporting',
    description: 'Review your inventory value at cost, with average unit costs kept up to date as purchases are recorded.'
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
    description: 'Connect supported Nayax credentials whenever you want to add sales data.'
  },
  {
    title: 'Optional integration',
    description: 'Use core inventory without connecting Nayax.'
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
    title: 'Create your business',
    description: 'Sign in and submit your business details. Your workspace opens once the application is approved.',
    icon: 'domain',
    tag: 'Business application'
  },
  {
    number: '02',
    title: 'Add your products and opening stock',
    description: 'Enter your products, categories, unit costs and opening quantities.',
    icon: 'inventory_2',
    tag: 'Product & quantity baseline'
  },
  {
    number: '03',
    title: 'Record purchases and stock movements',
    description: 'Log supplier purchases with their receipts and record stock movements as they happen.',
    icon: 'sync_alt',
    tag: 'Purchase & stock history'
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

/** The example dashboard's headline figures. Synthetic, and labelled as such on the page. */
export interface SampleMetric {
  readonly label: string;
  readonly value: string;
  readonly caption: string;
  readonly icon: string;
  readonly warning?: boolean;
}

export const SAMPLE_METRICS: readonly SampleMetric[] = [
  { label: 'Stock on hand', value: '922 items', caption: 'across active products', icon: 'inventory_2' },
  { label: 'Low-stock products', value: '2 products', caption: 'below minimum threshold', icon: 'warning', warning: true },
  { label: 'Recent purchases', value: '11 dockets', caption: '$1,840.50 logged this month', icon: 'receipt' },
  { label: 'Inventory at cost', value: '$2,482.83', caption: 'valued at average unit cost', icon: 'account_balance_wallet' }
];

export interface SampleMachine {
  readonly site: string;
  readonly machine: string;
  readonly categories: string;
  readonly products: number;
}

export const SAMPLE_MACHINES: readonly SampleMachine[] = [
  { site: 'Harbour Point Plaza - Ground', machine: 'Beverage Combo #1', categories: 'Drinks, Snacks', products: 18 },
  { site: 'Apex Commercial - Tower A', machine: 'Cold Drinks Unit #3', categories: 'Drinks', products: 12 },
  { site: 'Apex Commercial - Tower B', machine: 'Snack & Confectionery #2', categories: 'Snacks, Bars', products: 15 }
];

export interface SampleStockLevel {
  readonly product: string;
  readonly category: string;
  readonly inStock: number;
  readonly minimum: number;
  readonly supplier: string;
  readonly lastUnitCost: string;
}

export const SAMPLE_STOCK_LEVELS: readonly SampleStockLevel[] = [
  { product: 'Red Bull 250mL', category: 'Energy Drinks', inStock: 0, minimum: 7, supplier: 'Wholesale Direct', lastUnitCost: '$1.42' },
  { product: 'Coke 375mL', category: 'Carbonated Drinks', inStock: 26, minimum: 17, supplier: 'Metro Dist', lastUnitCost: '$0.98' },
  { product: 'Kinder Bueno', category: 'Confectionery', inStock: 5, minimum: 7, supplier: 'Confectionery Hub', lastUnitCost: '$1.15' }
];

/** Whether a sample row is below its minimum, which is the only thing its low-stock warning means. */
export function isLowStock(level: SampleStockLevel): boolean {
  return level.inStock < level.minimum;
}

/** The browser tab title and the description meta tag the landing page publishes while it is shown. */
export const LANDING_TITLE = 'Restockroo · Inventory management for products, purchases and vending machines';
export const LANDING_DESCRIPTION =
  'Restockroo tracks stock on hand, stock movements, purchases and suppliers for small businesses and vending ' +
  'operators. Core inventory works on its own; connecting Nayax is optional.';
