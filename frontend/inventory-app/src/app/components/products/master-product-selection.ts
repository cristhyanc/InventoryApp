import { HttpErrorResponse } from '@angular/common/http';

/**
 * The master product the operator picked from the New Product Name suggestions (issue #1052), and
 * the two values that selection filled in.
 *
 * The two values are remembered because they are the whole evidence of the link: the create of issue
 * #1051 believes `masterProductId` only while the submitted Name and unit label are still the ones
 * the shared catalogue produced, so a selection whose values have been edited away is no selection
 * at all and is dropped rather than sent and refused.
 */
export interface MasterProductSelection {
  readonly masterProductId: number;
  readonly productName: string;
  readonly unitLabel: string;
}

/** `MasterProductSelectionPolicy.UnavailableCode`: the master product is no longer in the catalogue. */
export const MASTER_PRODUCT_UNAVAILABLE_CODE = 'master_product_unavailable';

/** `MasterProductSelectionPolicy.ChangedCode`: the Name or unit label no longer agrees with it. */
export const MASTER_PRODUCT_CHANGED_CODE = 'master_product_changed';

/** `MasterProductSelectionPolicy.RefusedCode`: a Super Admin has ruled that pairing out. */
export const MASTER_PRODUCT_REFUSED_CODE = 'master_product_refused';

const MASTER_PRODUCT_CONFLICT_CODES = [
  MASTER_PRODUCT_UNAVAILABLE_CODE,
  MASTER_PRODUCT_CHANGED_CODE,
  MASTER_PRODUCT_REFUSED_CODE
] as const;

/** One of the three stable codes `POST api/products` refuses a selection with (issue #1051). */
export type MasterProductConflictCode = (typeof MASTER_PRODUCT_CONFLICT_CODES)[number];

/** A refused selection: which refusal it was, and the sentence to show for it. */
export interface MasterProductConflict {
  readonly code: MasterProductConflictCode;
  readonly message: string;
}

/**
 * The one sentence this application words itself about a refused selection, for a `409` that carried
 * a code and no text at all. The three real sentences are the server's
 * (`MasterProductSelectionPolicy`), because it is the only side that knows which of the three
 * happened and why; this says the two things that are true of all of them.
 */
const CONFLICT_FALLBACK_MESSAGE =
  'The product you chose from the shared catalogue could not be linked. Choose it again, or create ' +
  'this product without the link.';

/**
 * The refused selection a create failure carries, or `null` when the failure is not one - a
 * validation `400`, another conflict, a transport failure, anything else.
 *
 * It is the `ProblemDetails` shape `ProductsController.MasterProductSelectionConflict` writes
 * (issue #1051), the same shape `plan_required` uses: `code` beside `message`, with `detail`
 * carrying the same sentence. **The code is what is branched on, never the sentence**, because the
 * code is the stable half of that contract and the wording may be improved at any time.
 */
export function masterProductConflictOf(error: unknown): MasterProductConflict | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) {
    return null;
  }

  const body: unknown = error.error;
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const { code, message, detail } = body as { code?: unknown; message?: unknown; detail?: unknown };
  if (typeof code !== 'string' || !(MASTER_PRODUCT_CONFLICT_CODES as readonly string[]).includes(code)) {
    return null;
  }

  return {
    code: code as MasterProductConflictCode,
    message: text(message) ?? text(detail) ?? CONFLICT_FALLBACK_MESSAGE
  };
}

function text(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null;
}

/**
 * Whether a submitted value is still the prefill value it was filled in with - the browser-side
 * mirror of `MasterProductSelectionPolicy`'s normalisation: whitespace at the ends, runs of
 * whitespace inside the value, and case are not edits, and everything else is, `"45 g"` for `"45g"`
 * included.
 *
 * It exists so the form stops sending a selection the server would refuse, which is why it has to
 * fold exactly what the server folds and nothing more: folding something extra would send a
 * selection into a `409`, and folding something less would drop a link the server would have made.
 * The server stays the authority either way - it recomputes both values from the stored master
 * product and decides - and the lower-casing here is the invariant one (`toLowerCase`, never
 * `toLocaleLowerCase`), because a label must mean the same thing in every browser.
 */
export function isSamePrefillValue(left: string, right: string): boolean {
  return collapse(left) === collapse(right);
}

function collapse(value: string): string {
  return value.trim().replace(/\s+/g, ' ').toLowerCase();
}
