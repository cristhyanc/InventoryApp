/**
 * The two stable refusal codes a support session can answer a request with, and the one sentence
 * this application shows for each (issue #554). The codes mirror
 * `Inventory.Application.SupportSessions.SupportSessionErrorCodes` exactly; there is no shared
 * contract package, so the frontend's own copy is this file alone - nowhere else parses one of
 * these two codes out of a response body.
 */

/** `SupportSessionErrorCodes.ReadOnly`: a write reached the API while a support session was active. */
export const READ_ONLY_SUPPORT_SESSION_CODE = 'read_only_support_session';

/** `SupportSessionErrorCodes.NayaxUnavailable`: a request needed Nayax credentials a support session never resolves. */
export const NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_CODE = 'unavailable_in_support_view';

/**
 * Shown for a write a support session refused with {@link READ_ONLY_SUPPORT_SESSION_CODE} - the
 * backstop for a write control this build failed to hide or disable ahead of the request (a race
 * with the session ending, or a page this slice has not yet reached).
 */
export const READ_ONLY_SUPPORT_SESSION_MESSAGE = 'This action is unavailable: read-only support session.';

/**
 * Shown both for a direct refusal carrying {@link NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_CODE} and for a
 * Dashboard summary section published as `{ available: false, reason: "SupportView" }`
 * (`UnavailableDashboardSectionDto`) - the same sentence for the error and for the degraded card,
 * because both say the one thing: a support session never resolves the business's Nayax
 * credentials (issue #551's third criterion).
 */
export const NAYAX_UNAVAILABLE_IN_SUPPORT_VIEW_MESSAGE = 'Nayax is not available in support view';

/**
 * Shown when a support session ends without the administrator ending it - its own expiry, or the
 * server refusing a call with `support_session_ended` (issue #681) - so the return to their own
 * dashboard is explained rather than silent.
 */
export const SUPPORT_SESSION_ENDED_MESSAGE = 'Your support session has ended. You are back in your own account.';
