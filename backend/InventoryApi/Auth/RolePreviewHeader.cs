namespace InventoryApi.Auth;

/// <summary>
/// The request header a Super Admin previews a role with (issue #720).
///
/// <para>Its value is one of the three published role names - <c>Owner</c>, <c>Manager</c> or
/// <c>Operator</c> - and <see cref="RequestRolePreviewAccessor"/> is the only thing in the
/// application that reads it. What it does is narrow what the request may do: the capabilities it
/// would otherwise have had, intersected with that role's. It can only ever take access away.</para>
///
/// <para><strong>It never chooses a business.</strong> Which business a request acts in is decided
/// by the caller's membership or, for a support session, by the stored session row the presented id
/// matched (AGENTS.md § Tenant ownership and data isolation). A previewed request resolves exactly
/// the business it would have resolved without the header, and a platform administrator with no
/// membership and no session is refused with the header exactly as they are without it.</para>
///
/// <para><strong>It is not a credential.</strong> Unlike <see cref="SupportSessionHeader"/>, whose
/// value is matched against a stored secret, this header's value is a role <em>name</em> and grants
/// nothing: whether it is honoured at all is decided by re-evaluating
/// <see cref="PlatformAdmin.PlatformAdminPolicy"/> on the request, and what it then does is subtract.
/// A caller the policy refuses is told so rather than served unnarrowed, because a header whose only
/// effect is to remove access must never be droppable into full access.</para>
/// </summary>
public static class RolePreviewHeader
{
    /// <summary>The header name, as the one spelling every reader of it must use.</summary>
    public const string Name = "X-Role-Preview";
}
