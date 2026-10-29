import { HttpErrorResponse } from '@angular/common/http';

/**
 * The two per-feature Nayax availability answers and the one sentence this application shows for
 * each (issue #727, decision P9 of issue #329).
 *
 * Nayax is an optional add-on, so "this feature needs Nayax and Nayax is not there" is a normal
 * state of a working business rather than an error. The backend publishes exactly two such answers,
 * under one vocabulary (`Inventory.Application.Reorder.MachineNeedUnavailableReasons` for a
 * withheld dashboard section and a storage-only reorder listing, and the two `409` error codes of
 * `NayaxNotConnectedException`/`NayaxPermissionNotGrantedException` for a feature that could not be
 * answered at all). This file is the frontend's own copy of that vocabulary and the only place the
 * codes are mapped to wording - mirroring `support-session/support-session-messages.ts`, which does
 * the same for the support session's two refusals.
 *
 * **A code is never displayed.** Each sentence names the feature it is about, because availability
 * is per feature: in `PendingPermissions` the connection as a whole is usable and Nayax has simply
 * not granted this one read, so one page can carry a permission message for one card and real
 * figures beside it.
 *
 * **Since issue #967 the backend publishes a third reason, `PlanRequired`**, for a business whose
 * plan does not include the sales provider, beside the unchanged `nayax_not_connected` error code
 * and in the same per-feature reason fields. This file deliberately does **not** recognise it yet:
 * the functions below answer `null` for a code they have never heard of, which reports the feature
 * as unavailable without claiming why, and a "needs Pro" lock is issue #979's work. Adding it here
 * is that task, not a gap in this one - saying "Nayax is not connected" for a business whose
 * credentials are stored and working is exactly the wording #967 made distinguishable.
 */
export type NayaxUnavailability = 'NayaxNotConnected' | 'PermissionNotGranted';

/** The business has no Nayax credentials an ordinary operation may use. */
export const NAYAX_NOT_CONNECTED_REASON = 'NayaxNotConnected';

/** The credentials are usable and Nayax refused this feature's read for lack of permission. */
export const PERMISSION_NOT_GRANTED_REASON = 'PermissionNotGranted';

/**
 * `UnavailableDashboardSectionDto.SupportViewReason`: the section was not computed because a
 * read-only support session resolves no Nayax credentials at all (issue #552). It is deliberately
 * **not** a {@link NayaxUnavailability}: it says nothing about the business's connection, and its
 * one sentence lives in `support-session/support-session-messages.ts`.
 */
export const SUPPORT_VIEW_REASON = 'SupportView';

/** `NayaxNotConnectedException.ErrorCode`, as a `409` ProblemDetails `code`. */
const NAYAX_NOT_CONNECTED_CODE = 'nayax_not_connected';

/** `NayaxPermissionNotGrantedException.ErrorCode`, as a `409` ProblemDetails `code`. */
const NAYAX_PERMISSION_NOT_GRANTED_CODE = 'nayax_permission_not_granted';

/**
 * The availability answer a published reason code states, or `null` for anything else - a support
 * session's own reason, or a code this build has never heard of. An unrecognised code is never
 * guessed into one of the two: a caller then reports the feature as unavailable without claiming
 * why.
 */
export function nayaxUnavailabilityOfReason(reason: string | null | undefined): NayaxUnavailability | null {
  switch (reason) {
    case NAYAX_NOT_CONNECTED_REASON:
      return NAYAX_NOT_CONNECTED_REASON;
    case PERMISSION_NOT_GRANTED_REASON:
      return PERMISSION_NOT_GRANTED_REASON;
    default:
      return null;
  }
}

/**
 * The availability answer a failed request carries, or `null` when the failure is not one of the
 * two - a transport failure, a `502` from a real Nayax outage, a `403` about the caller's own
 * access. Only the exact `409` codes count: a request that failed for any other reason is an
 * error, and reporting it as "Nayax is not connected" would describe a connection nobody tested.
 */
export function nayaxUnavailabilityOfError(error: unknown): NayaxUnavailability | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) {
    return null;
  }

  const body: unknown = error.error;
  const code = typeof body === 'object' && body !== null ? (body as { code?: unknown }).code : null;
  switch (code) {
    case NAYAX_NOT_CONNECTED_CODE:
      return NAYAX_NOT_CONNECTED_REASON;
    case NAYAX_PERMISSION_NOT_GRANTED_CODE:
      return PERMISSION_NOT_GRANTED_REASON;
    default:
      return null;
  }
}

/**
 * The sentence for one feature being unavailable for one of the two reasons. `feature` is the
 * feature's own name as the screen calls it, so the message is about that card or section and not
 * about the application as a whole.
 */
export function nayaxUnavailableMessage(unavailability: NayaxUnavailability, feature: string): string {
  return unavailability === NAYAX_NOT_CONNECTED_REASON
    ? `${feature} is unavailable because Nayax is not connected.`
    : `${feature} is unavailable because Nayax has not granted this application permission to read it.`;
}

/**
 * The label a storage-only reorder figure carries (issue #726's `StorageOnly` mode): what the
 * figure did account for is complete and local, and what it did not account for is the machines'
 * current replenishment need. Without the reason - a listing whose mode said storage-only but whose
 * reason this build did not recognise - the label states the scope and claims nothing about why.
 */
export function storageOnlyReorderLabel(reason: NayaxUnavailability | null): string {
  const scope = "Storage stock only: these figures do not include the machines' current replenishment need";
  switch (reason) {
    case NAYAX_NOT_CONNECTED_REASON:
      return `${scope}, because Nayax is not connected.`;
    case PERMISSION_NOT_GRANTED_REASON:
      return `${scope}, because Nayax has not granted this application permission to read the machines.`;
    default:
      return `${scope}.`;
  }
}
