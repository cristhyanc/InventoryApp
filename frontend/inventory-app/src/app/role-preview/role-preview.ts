import { HttpErrorResponse } from '@angular/common/http';

/**
 * The published contract of the Super Admin's role preview (issue #720's API, issue #721's
 * frontend): the header a request presents, the three role names it may present, the three stable
 * refusal codes, and the two sentences this application shows for them.
 *
 * Everything here mirrors `Inventory.Application.Access.RolePreviewPolicy` and
 * `Inventory.Application.Access.RolePreviewErrorCodes` exactly, the way
 * `support-session-messages.ts` mirrors the support session's own codes: there is no shared
 * contract package, so this file is the frontend's one copy and nowhere else spells a code or the
 * header out.
 *
 * **The role names are a request value, not a role table.** They are the three names the API
 * accepts in the header, matched case-sensitively there, and nothing here says what a role may do:
 * the capabilities of a preview come from `GET /api/me/access` answered under the header, exactly
 * as every other capability answer does. `access/capabilities.ts` therefore still holds no role,
 * and `components/admin/roles/role-definitions.ts` - the Roles *page*'s descriptive content - is
 * not read by any of this.
 */

/** `InventoryApi.Auth.RolePreviewHeader`: the header a previewed request presents. */
export const ROLE_PREVIEW_HEADER = 'X-Role-Preview';

/**
 * The roles a preview may name, in the order the user menu offers them. `RolePreviewPolicy`
 * accepts exactly these three spellings and refuses everything else with `400`, so they are
 * written out rather than derived from anything.
 */
export const PREVIEWABLE_ROLES = ['Owner', 'Manager', 'Operator'] as const;

/** One role a preview may name. */
export type PreviewRole = (typeof PREVIEWABLE_ROLES)[number];

/** `RolePreviewErrorCodes.ReadOnly`: a write was attempted while a preview was active. */
export const READ_ONLY_ROLE_PREVIEW_CODE = 'read_only_role_preview';

/** `RolePreviewErrorCodes.NotPermitted`: the caller may not preview a role at all. */
export const ROLE_PREVIEW_NOT_PERMITTED_CODE = 'role_preview_not_permitted';

/** `RolePreviewErrorCodes.Invalid`: the presented value is not one of the three role names. */
export const INVALID_ROLE_PREVIEW_CODE = 'invalid_role_preview';

/**
 * Shown for a write the preview refused with {@link READ_ONLY_ROLE_PREVIEW_CODE}.
 *
 * Write controls stay visible during a preview deliberately (issue #721): the preview is meant to
 * be honest about what the role is *offered*, and the API's refusal is the read-only rail. This
 * sentence is therefore the expected answer to pressing one, not a backstop for a control that
 * should have been hidden - which is why it names the role and the way out of it.
 */
export function readOnlyRolePreviewMessage(role: PreviewRole): string {
  return `Previewing as ${role} is read-only. Exit the preview to make changes.`;
}

/**
 * Shown when the API refuses the preview itself - the caller may not preview
 * ({@link ROLE_PREVIEW_NOT_PERMITTED_CODE}), or the value could not be read
 * ({@link INVALID_ROLE_PREVIEW_CODE}) - so the return to the administrator's own view is explained
 * rather than silent, the way a support session's own end is (issue #681).
 */
export const ROLE_PREVIEW_ENDED_MESSAGE = 'The role preview has ended.';

/**
 * Whether `error` is one of the preview's two `403` refusals, which say nothing about the caller's
 * own role: the first is about the *method* and the second about the preview itself. Both are
 * reported by `rolePreviewInterceptor` with the sentence the refusal is actually about, so
 * `memberForbiddenNoticeInterceptor` leaves them alone and `accessForbiddenInterceptor` must not
 * re-read the capabilities over one.
 */
export function isRolePreviewRefusal(error: HttpErrorResponse): boolean {
  const body = error.error as { code?: string } | null | undefined;
  return body?.code === READ_ONLY_ROLE_PREVIEW_CODE || body?.code === ROLE_PREVIEW_NOT_PERMITTED_CODE;
}
