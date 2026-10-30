import { PickListMachineQuantity, PickListProduct } from '../../models/models';
import { InvalidPickQuantity, MAXIMUM_PICK_QUANTITY, isPlanCellKey } from './plan-picks';

/**
 * Where a Plan picks plan is kept between leaving the Pick List and coming back to it (issue
 * #1021), and the only code in the application that touches that storage.
 *
 * **`sessionStorage`, deliberately.** The owner's decision of 31 October 2026 was to keep the plan
 * in the browser rather than on the server, and the narrowest browser storage that survives an
 * in-app navigation and a reload is the one this tab owns: it is never shared with another tab or
 * device, is never sent to any server, and disappears when the tab closes. The stored plan holds
 * the business's product names and storage figures, which is why nothing wider was chosen - see
 * `PlanPicksStoreService` for the account, business and support-session rules that decide whether
 * anything may be written or read at all.
 *
 * **Nothing read back is trusted.** A stored record is a payload from outside the running
 * application: it may be absent, truncated, hand-edited, or written by an older build of this page.
 * {@link readStoredPlanPicks} therefore validates the version, the scope it names and every field,
 * row and cell key before any of it is returned, and answers `null` for anything it cannot vouch
 * for. The page then behaves exactly as it did before a plan was ever stored.
 *
 * **Every access is guarded.** Private mode, blocked site data, a storage quota and a serialization
 * failure all surface as a throw from `sessionStorage`; none of them may break the page. A read that
 * throws is "nothing stored", and a write that throws is reported as a failed write
 * ({@link writeStoredPlanPicks} answers `false`) and takes the previous record with it, so an older
 * plan can never be left behind looking like the latest saved work.
 */
export const PLAN_PICKS_STORAGE_KEY = 'inventoryapp.planPicks';

/**
 * The format version of a stored plan. A record written by any other version is read as absent and
 * removed: the alternative is restoring fields that have since changed meaning.
 */
export const PLAN_PICKS_STORAGE_VERSION = 1;

/**
 * Who a stored plan belongs to: the signed-in identity and the business in view. A record naming
 * anything else is not this operator's plan and is never restored. The frontend has no business id
 * of its own - `GET /api/me/access` reports the business by name - so the name is what scopes it.
 */
export interface PlanPicksScope {
  readonly accountKey: string;
  readonly businessName: string;
}

/** One cell's adjusted quantity, by its {@link isPlanCellKey} key. */
export interface StoredPlanOverride {
  readonly key: string;
  readonly quantity: number;
}

/** One cell's pending unusable text and the message refusing it, by its cell key. */
export interface StoredPlanInvalidQuantity extends InvalidPickQuantity {
  readonly key: string;
}

/**
 * A complete Plan picks plan: the applied selection, the snapshot it was planned against and the
 * instant that snapshot was read, plus the operator's own overrides, ticks and pending text.
 *
 * The snapshot travels with the plan because an override belongs to the snapshot it was made
 * against (the issue #996 rule): coming back restores both together, so no `GET /api/pick-list` is
 * made and the "last refreshed" time keeps saying when the figures on screen were actually read.
 */
export interface StoredPlanPicks {
  readonly appliedMachineIds: readonly number[];
  readonly appliedProductIds: readonly number[];
  readonly products: readonly PickListProduct[];

  /** The snapshot's own read instant, as an ISO 8601 string; never re-stamped on a restore. */
  readonly lastRefreshedAt: string;
  readonly overrides: readonly StoredPlanOverride[];
  readonly pickedCells: readonly string[];
  readonly invalidQuantities: readonly StoredPlanInvalidQuantity[];
}

/** The record as it is actually held: the plan, the format it was written in, and whose it is. */
interface StoredPlanPicksRecord extends PlanPicksScope {
  readonly version: number;
  readonly plan: StoredPlanPicks;
}

/** An identifier or a planned quantity: a whole number of `0` or more, within `maximum`. */
function isWholeNumber(value: unknown, maximum = Number.MAX_SAFE_INTEGER): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= maximum;
}

/**
 * A figure the snapshot reported. Deliberately not bounded below: these are the provider's and the
 * business's own numbers, and a plan must not be thrown away because one of them is negative in a
 * way this page only displays.
 */
function isFigure(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value);
}

function readIds(value: unknown): number[] | null {
  if (!Array.isArray(value) || !value.every((id) => isWholeNumber(id))) {
    return null;
  }
  return value as number[];
}

function readCell(value: unknown): PickListMachineQuantity | null {
  const cell = value as Partial<PickListMachineQuantity> | null;
  if (
    typeof cell !== 'object' ||
    cell === null ||
    !isWholeNumber(cell.machineId) ||
    !(cell.mdbCode === null || isWholeNumber(cell.mdbCode)) ||
    !isFigure(cell.currentQuantity) ||
    !isFigure(cell.targetQuantity) ||
    !isWholeNumber(cell.quantityToPick, MAXIMUM_PICK_QUANTITY)
  ) {
    return null;
  }

  return {
    machineId: cell.machineId,
    mdbCode: cell.mdbCode ?? null,
    currentQuantity: cell.currentQuantity,
    targetQuantity: cell.targetQuantity,
    quantityToPick: cell.quantityToPick
  };
}

function readProduct(value: unknown): PickListProduct | null {
  const product = value as Partial<PickListProduct> | null;
  if (
    typeof product !== 'object' ||
    product === null ||
    !isWholeNumber(product.productId) ||
    typeof product.productName !== 'string' ||
    !(product.mdbCode === null || isWholeNumber(product.mdbCode)) ||
    !isFigure(product.storageQuantityInStock) ||
    !isFigure(product.totalQuantityToPick) ||
    !isFigure(product.storageShortageQuantity) ||
    !Array.isArray(product.machineQuantities)
  ) {
    return null;
  }

  const machineQuantities: PickListMachineQuantity[] = [];
  for (const entry of product.machineQuantities) {
    const cell = readCell(entry);
    if (cell === null) {
      return null;
    }
    machineQuantities.push(cell);
  }

  return {
    productId: product.productId,
    productName: product.productName,
    mdbCode: product.mdbCode ?? null,
    storageQuantityInStock: product.storageQuantityInStock,
    totalQuantityToPick: product.totalQuantityToPick,
    storageShortageQuantity: product.storageShortageQuantity,
    machineQuantities
  };
}

function readProducts(value: unknown): PickListProduct[] | null {
  if (!Array.isArray(value)) {
    return null;
  }

  const products: PickListProduct[] = [];
  for (const entry of value) {
    const product = readProduct(entry);
    if (product === null) {
      return null;
    }
    products.push(product);
  }
  return products;
}

function readOverrides(value: unknown): StoredPlanOverride[] | null {
  if (!Array.isArray(value)) {
    return null;
  }

  const overrides: StoredPlanOverride[] = [];
  for (const entry of value) {
    const override = entry as Partial<StoredPlanOverride> | null;
    if (typeof override !== 'object' || override === null || !isPlanCellKey(override.key)) {
      return null;
    }
    // The same bound `parsePickQuantity` enforces on a typed quantity: a stored plan may not carry
    // an override no cell of this page could ever have produced.
    if (!isWholeNumber(override.quantity, MAXIMUM_PICK_QUANTITY)) {
      return null;
    }
    overrides.push({ key: override.key, quantity: override.quantity });
  }
  return overrides;
}

function readPickedCells(value: unknown): string[] | null {
  if (!Array.isArray(value) || !value.every((key) => isPlanCellKey(key))) {
    return null;
  }
  return value as string[];
}

function readInvalidQuantities(value: unknown): StoredPlanInvalidQuantity[] | null {
  if (!Array.isArray(value)) {
    return null;
  }

  const pending: StoredPlanInvalidQuantity[] = [];
  for (const entry of value) {
    const invalid = entry as Partial<StoredPlanInvalidQuantity> | null;
    if (
      typeof invalid !== 'object' ||
      invalid === null ||
      !isPlanCellKey(invalid.key) ||
      typeof invalid.text !== 'string' ||
      typeof invalid.error !== 'string'
    ) {
      return null;
    }
    pending.push({ key: invalid.key, text: invalid.text, error: invalid.error });
  }
  return pending;
}

/** The stored plan, field by field, or `null` when any part of it cannot be vouched for. */
function readPlan(value: unknown): StoredPlanPicks | null {
  const plan = value as Partial<StoredPlanPicks> | null;
  if (typeof plan !== 'object' || plan === null) {
    return null;
  }

  const appliedMachineIds = readIds(plan.appliedMachineIds);
  const appliedProductIds = readIds(plan.appliedProductIds);
  const products = readProducts(plan.products);
  const overrides = readOverrides(plan.overrides);
  const pickedCells = readPickedCells(plan.pickedCells);
  const invalidQuantities = readInvalidQuantities(plan.invalidQuantities);
  const lastRefreshedAt = typeof plan.lastRefreshedAt === 'string' ? plan.lastRefreshedAt : '';
  if (
    appliedMachineIds === null ||
    appliedMachineIds.length === 0 ||
    appliedProductIds === null ||
    products === null ||
    overrides === null ||
    pickedCells === null ||
    invalidQuantities === null ||
    !Number.isFinite(Date.parse(lastRefreshedAt))
  ) {
    return null;
  }

  return { appliedMachineIds, appliedProductIds, products, lastRefreshedAt, overrides, pickedCells, invalidQuantities };
}

/** Removes this tab's stored plan. A browser that refuses storage has nothing held to remove. */
export function clearStoredPlanPicks(): void {
  try {
    sessionStorage.removeItem(PLAN_PICKS_STORAGE_KEY);
  } catch {
    // Storage refused: there is nothing stored to forget.
  }
}

/**
 * The plan this tab holds for `scope`, or `null` when there is none to restore - nothing stored, a
 * record written in another format, a record belonging to another identity or business, a payload
 * that does not validate, or storage this browser refuses to read.
 *
 * A record that is present but unusable is removed, so one unreadable write cannot keep answering
 * "nothing" for the rest of the tab's life.
 */
export function readStoredPlanPicks(scope: PlanPicksScope): StoredPlanPicks | null {
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(PLAN_PICKS_STORAGE_KEY);
  } catch {
    return null;
  }

  if (raw === null) {
    return null;
  }

  let record: Partial<StoredPlanPicksRecord> | null = null;
  try {
    record = JSON.parse(raw) as Partial<StoredPlanPicksRecord> | null;
  } catch {
    clearStoredPlanPicks();
    return null;
  }

  if (
    typeof record !== 'object' ||
    record === null ||
    record.version !== PLAN_PICKS_STORAGE_VERSION ||
    record.accountKey !== scope.accountKey ||
    record.businessName !== scope.businessName
  ) {
    return null;
  }

  const plan = readPlan(record.plan);
  if (plan === null) {
    clearStoredPlanPicks();
  }
  return plan;
}

/**
 * Stores `plan` for `scope`, replacing whatever this tab held, and answers whether the complete
 * plan is now stored.
 *
 * `false` is the honest answer to a refused write - private mode, a quota, a serialization failure -
 * and the caller keeps warning that leaving loses the work. The previous record is removed in that
 * case: a plan the operator has moved on from must not be left behind as the newest saved copy.
 */
export function writeStoredPlanPicks(scope: PlanPicksScope, plan: StoredPlanPicks): boolean {
  const record: StoredPlanPicksRecord = { version: PLAN_PICKS_STORAGE_VERSION, ...scope, plan };
  try {
    sessionStorage.setItem(PLAN_PICKS_STORAGE_KEY, JSON.stringify(record));
    return true;
  } catch {
    clearStoredPlanPicks();
    return false;
  }
}
