using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Tests;

/// <summary>
/// The one place tests choose a tenant scope for an <c>AppDbContext</c> (issue #64).
///
/// <c>new AppDbContext(options)</c> is fail-closed - it resolves no business, so it reads nothing
/// and writes nothing. Every context in a test therefore states which of three things it is:
/// unrestricted setup, a specific business, or a caller whose membership failed. That is the
/// security invariant this helper exists to keep visible: unrestricted access is an explicit
/// opt-in, never what you get by not thinking about it.
///
/// This type lives in the root test namespace so every test namespace can reach it without a
/// using directive, which keeps the opt-in short enough that nobody is tempted to route around
/// it.
/// </summary>
internal static class TestAppDbContext
{
    /// <summary>
    /// A context that sees and may write every business's data.
    ///
    /// Use it only to arrange or verify the state of the world from outside the boundary - for
    /// example seeding business B's records so a test can prove business A cannot see them, or
    /// reading back what a scoped write actually persisted. Never use it to stand in for a
    /// request: a test that exercises application behaviour through an unrestricted context
    /// proves nothing about tenant isolation.
    /// </summary>
    public static AppDbContext Unrestricted(DbContextOptions<AppDbContext> options) =>
        new(options, UnscopedBusinessScope.Instance);

    /// <summary>A context acting as one business, exactly as a scoped request would.</summary>
    public static AppDbContext For(DbContextOptions<AppDbContext> options, int businessId) =>
        new(options, ScopeFor(businessId));

    /// <summary>
    /// A context for a caller whose business could not be resolved - no membership, or an
    /// ambiguous one. Reads must return nothing and tenant-owned writes must be refused.
    /// </summary>
    public static AppDbContext Denied(DbContextOptions<AppDbContext> options) =>
        new(options, DeniedScope());

    /// <summary>The scope a successful request would carry, for asserting on it directly.</summary>
    public static IBusinessScope ScopeFor(int businessId)
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(businessId));
        return scope;
    }

    /// <summary>The scope an authenticated caller with no usable membership would carry.</summary>
    public static IBusinessScope DeniedScope() => new BusinessScope();
}
