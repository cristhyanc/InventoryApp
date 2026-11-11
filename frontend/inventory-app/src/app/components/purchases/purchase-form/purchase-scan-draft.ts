import { GstClassification } from '../../../models/models';
import { PurchaseScanDraft, PurchaseScanLine, PurchaseScanValue } from '../../../models/purchase-scan';
import {
  PurchaseLinePrice,
  lineTotalFromUnitCost,
  roundToCents,
  unitCostFromLineTotal
} from '../purchase-line-price';

/**
 * Turns the scanned draft `POST /api/purchases/scan` answered with into the Add Purchase form's own
 * fields and lines (issue #1166, sub-issue D of #975).
 *
 * **It calculates no money.** Every figure here was decided by `Inventory.Domain.ReceiptScanning`
 * and is carried through exactly as the draft published it: the units, the unrounded unit cost the
 * purchase line submits, the printed line amount, the receipt total and the delivery and package
 * charges. The only arithmetic is the purchase form's own existing line-price module, used to show
 * the complementary field beside an entered one, and the cent rounding of the amounts that are on
 * the receipt but are **not** purchase lines, which exists so the receipt-total check can explain
 * the difference rather than leave the operator chasing it.
 *
 * **It decides nothing about GST.** A line arrives unclassified and stays that way: input GST comes
 * from an explicit per-component classification a person or a rule makes, never from a receipt's own
 * marker (AGENTS.md § Purchase GST classification). The receipt's GST hint is the receipt's claim.
 *
 * **Nothing it produces has been saved.** The fill is a suggestion in a form; the purchase is
 * created by the ordinary create request when the operator saves, through the endpoint and the
 * validation that already existed.
 */

/** The text the unresolved-levy row carries, which is the one thing to do about it: check. */
export const UNRESOLVED_LEVY_MESSAGE = 'Container levy not added to any product: check the receipt';

/** The choice a line that is not stock for resale shows, and which keeps it out of the purchase. */
export const NOT_STOCK_SKIP_LABEL = 'Not stock, skip';

/** What marks a pack size issue #1165 suggested from the business's own purchase history. */
export const PACK_SIZE_FROM_LAST_PURCHASE_LABEL = 'from your last purchase';

/** Which line kinds become a form line at all. `delivery` and `package` are never returned as lines. */
const FORM_LINE_KINDS = new Set(['stock', 'other']);

/** The sentence each published `missing` marker asks for, in the operator's own words. */
const MISSING_SENTENCES: Readonly<Record<string, string>> = {
  packs: 'How many packs arrived is not printed on the receipt. Enter the units.',
  unitsPerPack: 'The pack size is not printed on the receipt. Enter the units.',
  lineTotal: 'No line total is printed on the receipt. Enter the price.',
  priceIncludesGst: 'The receipt does not say whether this price includes GST. Enter the price.',
  taxable: 'The receipt does not say whether this line is taxable. Enter the price.',
  unitCost: 'The unit price could not be worked out from the receipt. Enter it.'
};

/** What an unrecognised marker from a newer API says, rather than being silently dropped. */
const UNRECOGNISED_MISSING_SENTENCE = 'Check this line against the receipt.';

/** Which of the two kinds a filled line came from: stock for resale, or something else. */
export type PurchaseScannedLineKind = 'stock' | 'other';

/** What the scan established about one filled line, for the person checking it before they save. */
export interface PurchaseScannedLine {
  /** The line exactly as the receipt printed it. */
  readonly text: string;

  readonly kind: PurchaseScannedLineKind;

  readonly packs: number | null;
  readonly unitsPerPack: number | null;
  readonly units: number | null;

  /** `2 x 24-pack = 48`, so the pack maths can be checked, or `null` when it cannot be shown. */
  readonly packMaths: string | null;

  /**
   * Whether the pack size was suggested from the business's own purchase history rather than read
   * off the receipt (issue #1165). Marked, because it is this application's guess and not a quotation.
   */
  readonly packSizeSuggested: boolean;

  /** Whether the product on this line is this application's match rather than the person's choice. */
  readonly productSuggested: boolean;

  /**
   * The amount this line carries on the receipt. It is what the receipt-total check counts while the
   * line is skipped, so a line that is not stock still explains the difference.
   */
  readonly receiptAmount: number | null;

  /** What the scan could not complete this line with, as sentences the form shows on the line. */
  readonly missing: readonly string[];

  /**
   * Whether this line is "{@link NOT_STOCK_SKIP_LABEL}" - shown, counted towards the receipt total
   * and **not** saved as a purchase line. It starts `true` for a line the receipt said is not stock,
   * and the person changes it by choosing a product.
   */
  skipped: boolean;
}

/** A levy the receipt's own totals could not place on any product: its own row, never a line. */
export interface PurchaseScannedLevy {
  /** The levy as the receipt printed it. */
  readonly text: string;

  /** Its printed amount, or `null` when the receipt did not print one. */
  readonly amount: number | null;

  /** {@link UNRESOLVED_LEVY_MESSAGE}, carried here so the row and this module cannot disagree. */
  readonly message: string;
}

/** One form line the scan filled in: the purchase line's own fields plus what the scan said. */
export interface PurchaseScanDraftLine extends PurchaseLinePrice {
  productId: number;
  gstClassification: GstClassification;
  scan: PurchaseScannedLine;
}

/**
 * The purchase-level fields the draft established. `null` is "the receipt did not say", and the form
 * keeps whatever it had - which is what leaves a purchase date the receipt has none for at today.
 */
export interface PurchaseScanFields {
  readonly supplierId: number | null;
  readonly purchaseDate: string | null;
  readonly invoiceNumber: string | null;
  readonly receiptTotal: number | null;
  readonly deliveryCost: number | null;
  readonly packageCost: number | null;
}

/** Everything one scanned receipt puts into the form. */
export interface PurchaseScanFill {
  readonly fields: PurchaseScanFields;
  readonly lines: PurchaseScanDraftLine[];
  readonly levies: PurchaseScannedLevy[];
}

/** The business's own products and suppliers, as the form already holds them. */
export interface PurchaseScanCatalogue {
  readonly products: readonly { readonly id: number }[];
  readonly suppliers: readonly { readonly id: number }[];
}

/**
 * The form fields, lines and levy rows one scanned draft produces.
 *
 * A suggested product or supplier is kept only when the form actually has it: the backend only ever
 * suggests an id of this business, so this is belt and braces rather than the tenancy rule, but a
 * picker bound to an id it has no option for shows blank, and a line would then be saved against a
 * product nothing on screen names.
 */
export function purchaseScanFill(draft: PurchaseScanDraft, catalogue: PurchaseScanCatalogue): PurchaseScanFill {
  const productIds = new Set(catalogue.products.map((product) => product.id));
  const supplierIds = new Set(catalogue.suppliers.map((supplier) => supplier.id));

  const lines: PurchaseScanDraftLine[] = [];
  const levies: PurchaseScannedLevy[] = [];

  for (const line of draft.lines ?? []) {
    if (line.kind === 'levy') {
      levies.push({
        text: line.text.value,
        amount: numberOf(line.lineTotal),
        message: UNRESOLVED_LEVY_MESSAGE
      });
      continue;
    }

    if (!FORM_LINE_KINDS.has(line.kind)) {
      // A kind the lines never carry - delivery and package are summed into the charges, and
      // anything else is from an API this build does not know. Guessing what to do with it would be
      // guessing about money.
      continue;
    }

    lines.push(toFormLine(line, productIds));
  }

  return {
    fields: {
      supplierId: knownId(numberOf(draft.supplierId), supplierIds),
      purchaseDate: textOf(draft.purchaseDate),
      invoiceNumber: textOf(draft.invoiceNumber),
      receiptTotal: numberOf(draft.receiptTotal),
      deliveryCost: numberOf(draft.deliveryCost),
      packageCost: numberOf(draft.packageCost)
    },
    lines,
    levies
  };
}

/**
 * The receipt amounts that are **not** part of the purchase: every line still marked
 * "{@link NOT_STOCK_SKIP_LABEL}" and every unresolved levy.
 *
 * The receipt-total check adds it, so a receipt whose total includes an office item or a levy this
 * purchase does not record reconciles instead of showing a difference nobody can act on. It is
 * never added to what the purchase costs: none of it is saved.
 */
export function scannedAmountOutsideLines(
  lines: readonly { readonly scan?: PurchaseScannedLine }[],
  levies: readonly PurchaseScannedLevy[]
): number {
  const skipped = lines.reduce(
    (sum, line) => sum + (line.scan?.skipped ? line.scan.receiptAmount ?? 0 : 0),
    0
  );
  const levied = levies.reduce((sum, levy) => sum + (levy.amount ?? 0), 0);

  return roundToCents(skipped + levied);
}

/** Whether this line is one the scan filled in and the person has left out of the purchase. */
export function isScannedLineSkipped(line: { readonly scan?: PurchaseScannedLine }): boolean {
  return line.scan?.skipped === true;
}

function toFormLine(line: PurchaseScanLine, productIds: ReadonlySet<number>): PurchaseScanDraftLine {
  const quantity = numberOf(line.units);
  const unitCost = numberOf(line.unitCost);
  const receiptAmount = numberOf(line.lineTotal);
  const productId = line.kind === 'stock' ? knownId(numberOf(line.productId), productIds) : null;

  return {
    productId: productId ?? 0,
    quantity,
    // The draft's own unit cost is the figure the purchase line submits, so it is the entered one
    // and the line total follows from it. With no unit cost, the printed amount is entered instead:
    // typing the units is then all it takes, rather than being asked for the price again.
    ...(unitCost !== null
      ? { priceEntryMode: 'unit' as const, unitCost, lineTotal: lineTotalFromUnitCost(unitCost, quantity) }
      : {
          priceEntryMode: 'total' as const,
          lineTotal: receiptAmount,
          unitCost: unitCostFromLineTotal(receiptAmount, quantity)
        }),
    // Nothing here classifies a line: the receipt's GST marker is the receipt's claim, and an input
    // GST classification is only ever a person's or a rule's.
    gstClassification: GstClassification.Unknown,
    scan: {
      text: line.text.value,
      kind: line.kind === 'other' ? 'other' : 'stock',
      packs: numberOf(line.packs),
      unitsPerPack: numberOf(line.unitsPerPack),
      units: quantity,
      packMaths: packMathsOf(line),
      packSizeSuggested: line.unitsPerPack?.provenance === 'suggested',
      productSuggested: productId !== null,
      receiptAmount,
      missing: missingSentences(line.missing ?? []),
      // A line the receipt says is not stock for resale starts out of the purchase, and a product
      // chosen for it is what puts it in.
      skipped: line.kind === 'other'
    }
  };
}

/** `2 x 24-pack = 48`, shown only when all three figures are there to be checked against each other. */
function packMathsOf(line: PurchaseScanLine): string | null {
  const packs = numberOf(line.packs);
  const unitsPerPack = numberOf(line.unitsPerPack);
  const units = numberOf(line.units);

  return packs !== null && unitsPerPack !== null && units !== null
    ? `${packs} x ${unitsPerPack}-pack = ${units}`
    : null;
}

/** Each distinct missing marker as its sentence, with an unrecognised one named as a thing to check. */
function missingSentences(missing: readonly string[]): readonly string[] {
  const sentences = missing.map((marker) => MISSING_SENTENCES[marker] ?? UNRECOGNISED_MISSING_SENTENCE);
  return [...new Set(sentences)];
}

/** The id only when the form has it; `null` for an absent suggestion and for one it cannot name. */
function knownId(id: number | null, known: ReadonlySet<number>): number | null {
  return id !== null && known.has(id) ? id : null;
}

/** A numeric value of the draft, or `null` when it carries none or carries something unusable. */
function numberOf(value: PurchaseScanValue<number> | null | undefined): number | null {
  return typeof value?.value === 'number' && Number.isFinite(value.value) ? value.value : null;
}

/** A text value of the draft, trimmed, or `null` when it carries none or carries only blanks. */
function textOf(value: PurchaseScanValue<string> | null | undefined): string | null {
  const text = typeof value?.value === 'string' ? value.value.trim() : '';
  return text.length > 0 ? text : null;
}
