/**
 * The Reordering card's one-line guidance (issue #1111), worded from the authoritative reorder rule
 * rather than from a simplified one.
 *
 * `Inventory.Domain.Products.ProductReorderPolicy` is that rule: a reorder is suggested when the
 * projected stock - warehouse stock plus stock already on order, less what the machines currently
 * need refilled - is **at or below** the low-stock threshold (the comparison is inclusive), and the
 * suggested quantity is what brings warehouse stock back to Restock To once the machines are refilled
 * and the order is received. Nothing is ordered automatically: the rule only suggests.
 *
 * This only words the two values on screen; it calculates nothing. It answers `null` while either
 * value is missing, negative, not a whole number or in the wrong order, so the card never states a
 * rule for values the form would refuse.
 */
export function reorderGuidance(lowStockThreshold: number | null, restockTo: number | null): string | null {
  if (!isCount(lowStockThreshold) || !isCount(restockTo) || restockTo < lowStockThreshold) {
    return null;
  }

  return (
    `A reorder is suggested when warehouse stock plus stock on order, less what machines need, is at or ` +
    `below ${units(lowStockThreshold)}. The suggested quantity brings warehouse stock back to ` +
    `${units(restockTo)} once machines are refilled and the order arrives.`
  );
}

function isCount(value: number | null): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0;
}

function units(value: number): string {
  return value === 1 ? '1 unit' : `${value} units`;
}
