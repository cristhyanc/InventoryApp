/**
 * The suggested purchase draft `POST /api/purchases/scan` answers with (issue #1163), mirrored here
 * exactly as the API publishes it - `PurchaseScanResponseDto` and the `PurchaseScan*` types beside
 * it - and nothing more.
 *
 * **Every vocabulary is a published token, never a number**, for the reason the backend publishes
 * words: a client branches on a word, and renaming a member on either side must not change the
 * contract. The tokens are the backend's own (`Inventory.Domain.ReceiptScanning`): a line's
 * {@link PurchaseScanLine.kind}, a value's {@link PurchaseScanValue.provenance}, the two GST
 * answers, and each marker in {@link PurchaseScanLine.missing}.
 *
 * **Nothing in it has been stored.** No file, no draft and no purchase: the operator edits the
 * suggestion and records it through the ordinary create, which is where a purchase is created and
 * validated. See docs/architecture.md § Receipt scanning.
 */

/** Where one value came from: `read` off the receipt, or `suggested` by the application. */
export type PurchaseScanProvenance = 'read' | 'suggested';

/** One value of the draft, and whether it was read off the receipt or suggested here. */
export interface PurchaseScanValue<T> {
  readonly value: T;
  readonly provenance: PurchaseScanProvenance;
}

/**
 * What the receipt said about one line's GST. The three tokens are `yes`, `no` and `unknown`; it is
 * the receipt's claim and **not** a GST classification, which only a person or an explicit rule
 * decides (AGENTS.md § Purchase GST classification).
 */
export interface PurchaseScanGstHint {
  readonly priceIncludesGst: string;
  readonly taxable: string;
  readonly marker: string | null;
}

/**
 * One entry of the draft. `delivery` and `package` lines are never returned: the backend sums them
 * into {@link PurchaseScanDraft.deliveryCost} and {@link PurchaseScanDraft.packageCost}.
 */
export interface PurchaseScanLine {
  /** `stock`, `levy` or `other`. Only `stock` ever carries a product suggestion. */
  readonly kind: string;

  /** The line as the receipt printed it. */
  readonly text: PurchaseScanValue<string>;

  readonly packs?: PurchaseScanValue<number> | null;

  /** Units per pack as printed - absent, never `1`, when the receipt does not print it. */
  readonly unitsPerPack?: PurchaseScanValue<number> | null;

  /** Packs times units per pack. */
  readonly units?: PurchaseScanValue<number> | null;

  /** The line's GST-inclusive amount. */
  readonly lineTotal?: PurchaseScanValue<number> | null;

  /** The amount over the units, unrounded - the cost the purchase line carries. */
  readonly unitCost?: PurchaseScanValue<number> | null;

  /** A suggested product of this business, when one was matched. */
  readonly productId?: PurchaseScanValue<number> | null;

  readonly gst: PurchaseScanGstHint;

  /** Whether this is a levy the receipt's own totals could not place on any product. */
  readonly levyUnresolved: boolean;

  /** What the line could not be completed with, as published tokens. */
  readonly missing: readonly string[];
}

/** How much of the business's monthly scan allowance is gone (issue #1164). */
export interface PurchaseScanUsage {
  readonly month: string;
  readonly used: number;
  readonly remaining: number;
  readonly limit: number;
}

/** The whole suggested purchase one scanned receipt produced. */
export interface PurchaseScanDraft {
  readonly supplier?: PurchaseScanValue<string> | null;
  readonly supplierId?: PurchaseScanValue<number> | null;
  /** The purchase date as `yyyy-MM-dd`, which is what the form's date field holds. */
  readonly purchaseDate?: PurchaseScanValue<string> | null;
  readonly invoiceNumber?: PurchaseScanValue<string> | null;
  readonly receiptTotal?: PurchaseScanValue<number> | null;
  readonly gstTotal?: PurchaseScanValue<number> | null;
  readonly deliveryCost?: PurchaseScanValue<number> | null;
  readonly packageCost?: PurchaseScanValue<number> | null;
  readonly lines: readonly PurchaseScanLine[];
  readonly usage: PurchaseScanUsage;
}
