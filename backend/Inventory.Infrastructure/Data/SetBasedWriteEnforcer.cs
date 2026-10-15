using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// The read-only support-session rule for a write that does <strong>not</strong> go through
/// <c>SaveChanges</c> (issue #681).
///
/// <para><see cref="BusinessOwnershipEnforcer"/> refuses every <c>SaveChanges</c> in
/// <see cref="BusinessScopeState.SupportReadOnly"/>, which is what makes "a support session changes
/// nothing" a property of the persistence boundary rather than of each endpoint. An
/// <c>ExecuteUpdate</c> or <c>ExecuteDelete</c> runs in the database and never reaches that
/// enforcer, so it was outside the one guarantee the boundary was supposed to give. Nothing was
/// reachable through it - every such statement in this application sits behind a <c>POST</c>, and the
/// capability handler already refuses every non-read method in a session - but "unreachable today"
/// is the kind of safety that one new <c>GET</c> endpoint removes, and the whole point of enforcing
/// read-only three times over is that no single layer has to be the one that holds.</para>
///
/// <para>It is a separate type rather than a member of <see cref="AppDbContext"/> because it is a
/// rule an <em>adapter</em> applies at a statement it is about to run, not something the context can
/// apply for it: EF offers no interception point for a set-based statement equivalent to
/// <c>SaveChanges</c>. Each call site therefore names it, immediately before its statement, the way
/// each one already names the business it requires.</para>
/// </summary>
internal static class SetBasedWriteEnforcer
{
    /// <summary>
    /// Refuses the statement the caller is about to run if this request is a support session.
    ///
    /// The refusal is the same <see cref="SupportSessionReadOnlyException"/> the <c>SaveChanges</c>
    /// enforcer raises, so the HTTP boundary answers it with the one stable
    /// <c>read_only_support_session</c> code either way - a client cannot tell, and must not be able
    /// to tell, which layer refused.
    /// </summary>
    /// <param name="context">The context whose scope decides.</param>
    /// <exception cref="SupportSessionReadOnlyException">
    /// The request is running in a support session, where no write of any kind is wanted.
    /// </exception>
    public static void RequireWritableScope(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.BusinessScope.State == BusinessScopeState.SupportReadOnly)
        {
            throw new SupportSessionReadOnlyException();
        }
    }
}
