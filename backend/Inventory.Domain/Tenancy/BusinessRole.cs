namespace Inventory.Domain.Tenancy;

/// <summary>
/// What a member may do in their business (issue #521, one slice of #501 under #328).
///
/// A role is deliberately not a privilege <em>number</em>: nothing compares two roles with
/// <c>&lt;</c> or <c>&gt;</c>, and no capability is derived from the stored value. What a role
/// allows comes from the one explicit table in
/// <c>Inventory.Application.Access.RoleCapabilities</c>, so a future role can sit anywhere in the
/// hierarchy without re-deriving anyone's access.
///
/// The values are spaced, and none of them is <c>0</c>, for two reasons:
/// <list type="bullet">
///   <item><b>Room below <see cref="Operator"/>.</b> The read-only Viewer role #328 anticipates
///   belongs at <c>10</c> and can be added without renumbering a value that is already persisted
///   in a membership row.</item>
///   <item><b><c>0</c> is not a role.</b> It is the value an unset integer column and a
///   default-constructed enum both hold, so leaving it undeclared means such a value is an
///   undeclared role rather than the lowest-privileged one. <see cref="BusinessRoles"/> rejects
///   it and <see cref="BusinessMembershipResolutionPolicy"/> denies access for it, which is the
///   fail-closed direction.</item>
/// </list>
/// </summary>
public enum BusinessRole
{
    /// <summary>
    /// Day-to-day vending operations: the dashboard without its financials, the pick list,
    /// inventory and purchasing. No reports, no money configuration, no members.
    /// </summary>
    Operator = 20,

    /// <summary>
    /// Everything an <see cref="Operator"/> may do, plus the reporting, expense, import and
    /// commission work that needs the financial figures.
    /// </summary>
    Manager = 30,

    /// <summary>
    /// Everything a <see cref="Manager"/> may do, plus the business's own configuration: the
    /// Nayax integration, data repairs, members, roles and the business record itself.
    /// </summary>
    Owner = 40,
}
