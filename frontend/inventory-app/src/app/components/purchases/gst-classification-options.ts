import { GstClassification } from '../../models/models';

/**
 * The one GST picker vocabulary the purchase entry and edit forms share (issue #431), so the two
 * pages cannot drift apart on the options they offer or on what each state is called.
 *
 * `Unknown` is offered deliberately, as "Not classified": it is the default for a new line or charge
 * and a real persisted state a person may return to, never a hidden placeholder for GST-free.
 */
export const GST_CLASSIFICATION_OPTIONS: ReadonlyArray<{ value: GstClassification; label: string }> = [
  { value: GstClassification.Unknown, label: 'Not classified' },
  { value: GstClassification.Taxable, label: 'Taxable' },
  { value: GstClassification.GstFree, label: 'GST-free' }
];

/**
 * How one component's stored classification is named wherever a purchase is displayed. An absent
 * value is an unclassified component, which is what every purchase recorded before issue #429 holds.
 */
export function gstClassificationLabel(value: GstClassification | null | undefined): string {
  return GST_CLASSIFICATION_OPTIONS.find((option) => option.value === (value ?? GstClassification.Unknown))?.label
    ?? 'Not classified';
}

/**
 * Whether a delivery or package charge exists at all, which is what decides whether its picker is
 * shown. It mirrors the server's own absent-charge rule (`PurchaseGstPolicy.IsChargePresent`,
 * parent issue #62 decision D3): a null or zero charge has no classification and is never
 * unresolved. This is a presentation decision about a control's visibility, not a GST calculation -
 * the GST figures themselves always come from the API.
 */
export function isChargePresent(amount: number | null | undefined): boolean {
  return amount !== null && amount !== undefined && Number(amount) !== 0;
}
