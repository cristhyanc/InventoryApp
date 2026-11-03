using Inventory.Application.Products;
using Inventory.Domain.Gst;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IBulkProductGstRuleStore"/> (issue #1076): counting the
/// business's products that carry no GST rule, and configuring one rule on exactly those.
///
/// <para><strong>One column of one table is written, and nothing else is read or written at all.</strong>
/// <c>Products.GstRule</c> is the whole write - not the product's name, price, unit, category,
/// stock, cost, valuation or <c>UpdatedAt</c>, which describes the catalogue record the Nayax import
/// and the product edit maintain (the same restraint <see cref="EfProductStore.SetGstRuleAsync"/>
/// keeps for one product). No purchase, purchase line, classification, provenance, stock movement,
/// sale, supplier or costing row is touched, which is what makes this configuration rather than a
/// reclassification (AGENTS.md § Purchase GST classification).</para>
///
/// <para><strong>Tenant scoping is the central one.</strong> Both statements go through
/// <see cref="AppDbContext"/>'s global query filters, so "every product" means every product of the
/// caller's resolved business and no predicate over <c>BusinessId</c> appears here; the write goes
/// through <c>BusinessOwnershipEnforcer</c> on <c>SaveChanges</c> like any other. There is no
/// <c>IgnoreQueryFilters</c> statement and no write lease: an unresolved business matches no row and
/// the enforcer refuses the save, which is the fail-closed behaviour rather than an exception
/// to it.</para>
///
/// <para><strong>Active and inactive products alike.</strong> Neither statement filters on
/// <c>IsActive</c>, deliberately: a deactivated product still has purchase history whose GST the
/// business has to account for, and the scope the operator confirms says so in so many words.</para>
///
/// <para><strong>The write is one <c>BEGIN IMMEDIATE</c> transaction, and the lock is taken before
/// the count</strong> (<see cref="SqliteWriteLockTransaction"/>, the shape every read-then-write
/// decision in this folder uses). The count the change is authorised by, the comparison against
/// what the operator confirmed and the update are then one operation: a deferred transaction would
/// take its write lock after that count, which is precisely the window in which another caller's
/// explicit rule or new product makes the approved set the wrong one. Every eligible row is
/// configured in a single <c>SaveChanges</c> inside that transaction, so either all of them are
/// stored or none is - a failure anywhere leaves the catalogue exactly as it was, because the
/// transaction is disposed without the commit.</para>
///
/// <para><strong>The rows are tracked and saved rather than updated in bulk by SQL.</strong>
/// <c>ExecuteUpdate</c> would be one statement, and it would also bypass <c>SaveChanges</c> - and
/// with it the central ownership enforcement every write in this application passes through. The
/// per-row update is the deliberate choice: this is a catalogue-sized write made by one operator
/// once, and the boundary it keeps is worth more than the statement count.</para>
/// </summary>
public sealed class EfBulkProductGstRuleStore : IBulkProductGstRuleStore
{
    private readonly AppDbContext _db;

    public EfBulkProductGstRuleStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public Task<int> CountProductsWithNoRuleAsync(CancellationToken cancellationToken) =>
        _db.Products
            .AsNoTracking()
            .Where(product => product.GstRule == GstRules.None)
            .CountAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<int> SetRuleOnProductsWithNoRuleAsync(
        GstClassification rule, int expectedEligibleCount, CancellationToken cancellationToken)
    {
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        // The authoritative read, inside the lock: the eligible products as they are now, selected
        // by the eligibility rule itself rather than by the ids of a previous read. That is what
        // keeps an explicit rule committed in between from being overwritten - such a product is
        // simply not in this set - and it is the set the comparison below is made against.
        var eligible = await _db.Products
            .Where(product => product.GstRule == GstRules.None)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The expected-value comparison, also inside the lock: the set as it is now against the
        // count the operator confirmed. A mismatch is somebody else's product or rule change, and
        // applying anyway would write a set nobody approved.
        BulkProductGstRulePolicy.RequireUnchangedEligibleCount(expectedEligibleCount, eligible.Count);

        foreach (var product in eligible)
        {
            product.GstRule = rule;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return eligible.Count;
    }
}
