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
 * **Since issue #967 the backend publishes a third reason, `PlanRequired`** (issue #979 adds it
 * here), for a business whose plan does not include the sales provider, beside the unchanged
 * `nayax_not_connected` error code and in the same per-feature reason fields. It is the one of the
 * three that names no connection problem at all - the business's saved credentials, if it has any,
 * are untouched - so it reads as "needs the Pro plan" rather than "Nayax is not connected", which
 * would be untrue of a business whose credentials are stored and working. Every feature this file
 * answers about is this business's sales-provider connection or its site commissions, and both are
 * the `Pro` plan (`PlanFeaturePolicy.RequiredPlan`), so the sentence below names it directly rather
 * than looking it up in `access/plan-feature.ts`.
 */
export type NayaxUnavailability = 'NayaxNotConnected' | 'PermissionNotGranted' | 'PlanRequired';

/** The business has no Nayax credentials an ordinary operation may use. */
export const NAYAX_NOT_CONNECTED_REASON = 'NayaxNotConnected';

/** The credentials are usable and Nayax refused this feature's read for lack of permission. */
export const PERMISSION_NOT_GRANTED_REASON = 'PermissionNotGranted';

/** The business's plan does not include the sales provider at all (issue #967). */
export const PLAN_REQUIRED_REASON = 'PlanRequired';

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
    case PLAN_REQUIRED_REASON:
      return PLAN_REQUIRED_REASON;
    default:
      return null;
  }
}

/**
 * The availability answer a failed request carries, or `null` when the failure is not one of the
 * three - a transport failure, a `502` from a real Nayax outage, a `403` about the caller's own
 * access. Only the exact `409` codes count: a request that failed for any other reason is an
 * error, and reporting it as "Nayax is not connected" would describe a connection nobody tested.
 *
 * **The connection and the plan refusal share one code** (`NayaxNotConnectedException.ErrorCode`,
 * issue #967, deliberately unchanged so a client that has not learnt about plans yet keeps
 * degrading exactly as it did before): `nayaxUnavailableReason` on the response body is what tells
 * them apart, and a value this build has never heard of is read as the connection refusal, which is
 * every refusal there was before issue #967.
 */
export function nayaxUnavailabilityOfError(error: unknown): NayaxUnavailability | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) {
    return null;
  }

  const body: unknown = error.error;
  const code = typeof body === 'object' && body !== null ? (body as { code?: unknown }).code : null;
  switch (code) {
    case NAYAX_NOT_CONNECTED_CODE: {
      const reason =
        typeof body === 'object' && body !== null
          ? (body as { nayaxUnavailableReason?: unknown }).nayaxUnavailableReason
          : null;
      return reason === PLAN_REQUIRED_REASON ? PLAN_REQUIRED_REASON : NAYAX_NOT_CONNECTED_REASON;
    }
    case NAYAX_PERMISSION_NOT_GRANTED_CODE:
      return PERMISSION_NOT_GRANTED_REASON;
    default:
      return null;
  }
}

/**
 * The sentence for one feature being unavailable for one of the three reasons. `feature` is the
 * feature's own name as the screen calls it, so the message is about that card or section and not
 * about the application as a whole.
 */
export function nayaxUnavailableMessage(unavailability: NayaxUnavailability, feature: string): string {
  switch (unavailability) {
    case NAYAX_NOT_CONNECTED_REASON:
      return `${feature} is unavailable because Nayax is not connected.`;
    case PERMISSION_NOT_GRANTED_REASON:
      return `${feature} is unavailable because Nayax has not granted this application permission to read it.`;
    default:
      return `${feature} needs the Pro plan. Any Nayax credentials this business has saved are unchanged.`;
  }
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
    case PLAN_REQUIRED_REASON:
      return `${scope}, because this business's plan does not include the Nayax connection.`;
    default:
      return `${scope}.`;
  }
}
