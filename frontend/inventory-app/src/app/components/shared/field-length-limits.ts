/**
 * The browser-side copy of the free-text length limits (issue #1286). The server stays the
 * authority: each record's Domain policy (`ProductFieldLengthPolicy`, `CategoryFieldLengthPolicy`,
 * `SupplierFieldLengthPolicy`, `PurchaseFieldLengthPolicy`, `OperatingExpenseFieldLengthPolicy`,
 * `MachineFieldLengthPolicy`) refuses a longer value with `400` whatever a form does. These numbers
 * only give the inputs a matching `maxlength`, so a person cannot type past the limit.
 */
export const FIELD_LENGTH_LIMITS = {
  product: { name: 200, sku: 100, unit: 100, description: 2000 },
  category: { name: 200, description: 2000 },
  supplier: { name: 200, contactName: 200, phone: 50, email: 254, address: 500 },
  purchase: { title: 200, notes: 2000 },
  expense: { description: 500, notes: 2000 },
  machine: { name: 200 }
} as const;

/**
 * The `maxlength` to bind to an edit form's input: the limit, or `null` - no limit, so neither the
 * browser attribute nor Angular's `maxlength` validator applies - while the input still holds a
 * value that was loaded from the server already longer than the limit.
 *
 * Rows written before the limits existed may hold a longer value, and the server accepts an update
 * that leaves that field as it is. Angular's validator would mark such a loaded value invalid, so
 * the form would refuse an edit the API allows; this keeps the field unlimited until the person
 * changes it, after which the limit applies like on any other value. The loaded value is measured
 * as the validator measures it (untrimmed), so nothing the validator would mark invalid on load is
 * ever limited before it is changed.
 */
export function editableMaxLength(
  current: string | null | undefined,
  loaded: string | null | undefined,
  limit: number
): number | null {
  const loadedText = loaded ?? '';
  const keepsLegacyValue = loadedText.length > limit && (current ?? '') === loadedText;
  return keepsLegacyValue ? null : limit;
}
