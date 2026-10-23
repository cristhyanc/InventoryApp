using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Turns the issue #64 DbSet audit into a standing rule instead of a one-off review.
///
/// Every persisted entity is either tenant-owned or deliberately global, and this test holds the
/// list of deliberate exceptions. Adding a new entity without ownership fails here, which is the
/// point: the failure mode this boundary has to prevent is a table quietly arriving later with
/// no owner and no filter, readable by every business.
/// </summary>
public class BusinessOwnershipCoverageTests
{
    /// <summary>
    /// The only entities that are not tenant-owned, each for a structural reason.
    ///
    /// <see cref="Business"/> is the ownership boundary itself, and <see cref="BusinessMembership"/>
    /// is what resolves a caller to one. Filtering membership by the current business would be
    /// circular - resolution reads it before any business is known - and would deny every caller.
    ///
    /// <see cref="BusinessBackfillAudit"/> is an operational record of the tenancy rollout, not
    /// business data. It carries a BusinessId as a fact about what the backfill did, and must stay
    /// readable while diagnosing a run that assigned rows to the wrong business - exactly the case
    /// where a tenant filter would hide the evidence being looked for.
    ///
    /// <see cref="MemberInvite"/> is the fourth, and the reviewed ownership decision on issue #532
    /// (it was tenant-owned as issue #531 first shipped it). The structural reason is the
    /// redemption: a person arriving with an invite link has no membership, so their request
    /// resolves no business and carries a denied scope, and under a denied scope a filtered read
    /// matches no row at all - which would make an invite impossible to find by the one thing that
    /// caller presents, its token. The business cannot come from the request either, so it comes
    /// from the invite row the token identifies. A filter the one path the table exists for would
    /// have to evade is not a boundary; the business id named in every statement, which is what both
    /// invite adapters do, is. See docs/architecture.md § Invite redemption.
    ///
    /// <see cref="SupportSession"/> is the fifth, and the reviewed ownership decision on issue #550.
    /// The structural reason is who writes it: every request that reaches this table is a
    /// platform-admin request, and a platform administrator is a member of no business, so the
    /// request resolves nothing and carries a denied scope. A tenant-owned session table could
    /// therefore be neither written nor read by the only caller it exists for - the filter would
    /// match none of its rows and the ownership stamp would have no business to stamp - and the
    /// business cannot come from the request either, so it comes from the business row the session's
    /// own transaction read. The boundary is that business id, and the actor pair beside it, named in
    /// every statement of the one adapter over this table. See docs/architecture.md § Support
    /// sessions.
    ///
    /// Nothing else is exempt. In particular there is no "global reference data" in this schema:
    /// the enum-like constants (payment and costing status, expense category, commission basis,
    /// stock adjustment reason) live in code as C# enums, not tables, and every configuration
    /// table that does exist - Nayax processing fee rates included - is business configuration
    /// and is owned. The one table that is about no business at all is classified separately, in
    /// <see cref="PlatformEntities"/> below, and deliberately not here.
    /// </summary>
    private static readonly HashSet<Type> DeliberatelyGlobalEntities =
    [
        typeof(Business),
        typeof(BusinessMembership),
        typeof(BusinessBackfillAudit),
        typeof(MemberInvite),
        typeof(SupportSession),
    ];

    /// <summary>
    /// The platform tables: rows this application owns on behalf of the platform, about no business
    /// at all (issue #848).
    ///
    /// <para>This is a different classification from <see cref="DeliberatelyGlobalEntities"/> above,
    /// and the difference is the thing worth stating. Each of those five <em>is</em> about one
    /// business and carries its key in every statement - "not tenant-owned" never meant "not owned".
    /// A platform table has no business to carry: <see cref="SupplierPreset"/> is the platform's own
    /// list of suggested suppliers, and what a business ends up with is its own ordinary tenant-owned
    /// <see cref="Supplier"/> row that issue #835 child C copies from it. Keeping the two lists apart
    /// is what stops this table being read as an unassigned tenant table that somebody forgot to
    /// stamp - and keeps the "still carries a business key" rule below exactly as strong for the five
    /// it is about.</para>
    ///
    /// <para>A platform table is still only writable by the platform-admin surface, which carries a
    /// <em>denied</em> business scope; that is enforced by the endpoint's authorization, not by the
    /// ownership mechanism, because there is no ownership here to enforce.</para>
    /// </summary>
    private static readonly HashSet<Type> PlatformEntities =
    [
        typeof(SupplierPreset),
    ];

    /// <summary>
    /// The five exemptions above are a closed list, asserted rather than left implicit: a sixth
    /// table arriving here is the one change this whole test exists to make visible, and the
    /// failure message is where a reviewer is told to look for the structural reason.
    /// </summary>
    [Fact]
    public void Exactly_five_entities_are_declared_structurally_global() =>
        Assert.Equal(
            [
                "Business",
                "BusinessBackfillAudit",
                "BusinessMembership",
                "MemberInvite",
                "SupportSession",
            ],
            DeliberatelyGlobalEntities
                .Select(type => type.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());

    /// <summary>
    /// The platform list is closed in the same way and for the same reason: a second table claiming
    /// to be about no business is a tenancy decision, not an implementation detail.
    /// </summary>
    [Fact]
    public void Exactly_one_entity_is_declared_a_platform_table() =>
        Assert.Equal(
            ["SupplierPreset"],
            PlatformEntities
                .Select(type => type.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());

    /// <summary>
    /// The two lists may not overlap. A table is about one business or about none, and a type in
    /// both would be claiming each rule's exemption while obeying neither.
    /// </summary>
    [Fact]
    public void A_platform_table_is_not_also_a_structurally_global_one() =>
        Assert.Empty(PlatformEntities.Intersect(DeliberatelyGlobalEntities));

    /// <summary>
    /// A platform table carries no business key at all, which is the whole of what distinguishes it
    /// from the five above: adding a <c>BusinessId</c> to one would make it a tenant table that
    /// nothing filters or stamps - exactly the failure this file exists to prevent.
    /// </summary>
    [Fact]
    public void A_platform_table_carries_no_business_key_and_no_query_filter()
    {
        var model = BuildModel();

        Assert.All(PlatformEntities, type =>
        {
            Assert.False(
                typeof(IBusinessOwned).IsAssignableFrom(type),
                $"{type.Name} is a platform table and must not implement IBusinessOwned.");
            Assert.Null(type.GetProperty(nameof(IBusinessOwned.BusinessId)));

            var entityType = model.FindEntityType(type)!;
            Assert.True(
                entityType.GetDeclaredQueryFilters() is not { Count: > 0 },
                $"{type.Name} must carry no tenant query filter: it is about no business, so a "
                    + "filter comparing the current business could only ever match none of its "
                    + "rows (docs/architecture.md § Tenant ownership).");
            Assert.DoesNotContain(
                entityType.GetProperties(),
                property => property.Name == nameof(IBusinessOwned.BusinessId));
        });
    }

    /// <summary>
    /// A structurally global table must still be bound to a business by a column every statement
    /// names - "not tenant-owned" is not "not owned". <see cref="Business"/> is the one exception,
    /// because it <em>is</em> the business.
    /// </summary>
    [Fact]
    public void Every_structurally_global_table_but_the_business_itself_still_carries_a_business_key() =>
        Assert.All(
            DeliberatelyGlobalEntities.Where(type => type != typeof(Business)),
            type => Assert.NotNull(type.GetProperty(nameof(IBusinessOwned.BusinessId))));

    /// <summary>
    /// The invite's per-business token uniqueness survives the ownership change. It is asserted
    /// here because <see cref="Every_unique_constraint_on_business_owned_data_includes_the_business"/>
    /// below no longer covers this table, and a later edit that "simplified" the index to the hash
    /// alone would make one business's invite able to deny another's.
    /// </summary>
    [Fact]
    public void The_invite_token_hash_stays_unique_per_business_rather_than_globally()
    {
        var invite = BuildModel().FindEntityType(typeof(MemberInvite))!;

        var unique = Assert.Single(invite.GetIndexes().Where(index => index.IsUnique));
        Assert.Equal(
            [nameof(IBusinessOwned.BusinessId), nameof(MemberInvite.TokenHash)],
            unique.Properties.Select(property => property.Name).ToArray());

        // And the leading-column index every statement in both invite adapters relies on.
        Assert.Contains(
            invite.GetIndexes(),
            index => index.Properties.Count == 1
                && index.Properties[0].Name == nameof(IBusinessOwned.BusinessId));
    }

    /// <summary>
    /// The invite carries no global query filter, which is the half of the decision that is easy to
    /// undo by accident: re-adding <c>IBusinessOwned</c> would compile, pass most tests, and leave
    /// the redemption unable to find an invite under its denied scope.
    /// </summary>
    [Fact]
    public void The_invite_carries_no_query_filter()
    {
        var invite = BuildModel().FindEntityType(typeof(MemberInvite))!;

        Assert.False(typeof(IBusinessOwned).IsAssignableFrom(typeof(MemberInvite)));
        Assert.True(
            invite.GetDeclaredQueryFilters() is not { Count: > 0 },
            "MemberInvite must carry no tenant query filter: the invite redemption of issue #532 "
                + "finds an invite by its token under a denied business scope, which a filter "
                + "comparing the current business can never match (docs/architecture.md § Invite "
                + "redemption).");
    }

    /// <summary>
    /// The support session's id hash is unique <em>globally</em>, which is the deliberate opposite of
    /// the invite token's per-business constraint above (issue #550). The rule the invite follows is
    /// about uniqueness over a value a <em>business</em> supplies, where a global index would let one
    /// business's row deny another's; a session id is generated by this server from cryptographic
    /// randomness, is supplied by nobody, and is matched by a request that has no business to narrow
    /// the lookup with - so global uniqueness is what makes "which session is this?" have one answer.
    /// A later edit that scoped it by business would make that lookup a scan over a non-unique value.
    /// </summary>
    [Fact]
    public void The_support_session_id_hash_is_unique_globally_rather_than_per_business()
    {
        var session = BuildModel().FindEntityType(typeof(SupportSession))!;

        var unique = Assert.Single(session.GetIndexes().Where(index => index.IsUnique));
        Assert.Equal(
            [nameof(SupportSession.SessionIdHash)],
            unique.Properties.Select(property => property.Name).ToArray());

        // And the two indexes every statement in the one adapter over this table relies on: the
        // business the deactivation hook ends sessions for, and the actor pair a "my session" read
        // seeks on.
        Assert.Contains(
            session.GetIndexes(),
            index => index.Properties.Count == 1
                && index.Properties[0].Name == nameof(IBusinessOwned.BusinessId));
        Assert.Contains(
            session.GetIndexes(),
            index => index.Properties
                .Select(property => property.Name)
                .SequenceEqual(
                    [
                        nameof(SupportSession.ActorDirectoryTenantId),
                        nameof(SupportSession.ActorObjectId),
                    ],
                    StringComparer.Ordinal));
    }

    /// <summary>
    /// The support session carries no global query filter, which is the half of its ownership
    /// decision that is easy to undo by accident: adding <c>IBusinessOwned</c> would compile, pass
    /// most tests, and leave every platform-admin request - which resolves no business at all -
    /// unable to read or write a session.
    /// </summary>
    [Fact]
    public void The_support_session_carries_no_query_filter()
    {
        var session = BuildModel().FindEntityType(typeof(SupportSession))!;

        Assert.False(typeof(IBusinessOwned).IsAssignableFrom(typeof(SupportSession)));
        Assert.True(
            session.GetDeclaredQueryFilters() is not { Count: > 0 },
            "SupportSession must carry no tenant query filter: every request that reaches it is a "
                + "platform-admin request, which resolves no business, so a filter comparing the "
                + "current business would match none of its rows (docs/architecture.md § Support "
                + "sessions).");
    }

    private static IModel BuildModel()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var context = TestAppDbContext.Unrestricted(options);
        return context.Model;
    }

    [Fact]
    public void Every_persisted_entity_is_either_business_owned_or_a_declared_global()
    {
        var unowned = BuildModel()
            .GetEntityTypes()
            .Select(entityType => entityType.ClrType)
            .Where(clrType => !typeof(IBusinessOwned).IsAssignableFrom(clrType))
            .Where(clrType => !DeliberatelyGlobalEntities.Contains(clrType))
            .Where(clrType => !PlatformEntities.Contains(clrType))
            .Select(clrType => clrType.Name)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unowned.Length == 0,
            "These persisted entities have no business ownership and are not declared global: "
                + $"{string.Join(", ", unowned)}. Implement IBusinessOwned on a tenant-owned "
                + "entity, add it to DeliberatelyGlobalEntities with the reason it is not owned by "
                + "a business, or - if it is about no business at all - to PlatformEntities. See "
                + "docs/architecture.md and issues #64 and #848.");
    }

    /// <summary>
    /// Ownership without a query filter would be decoration. Each owned entity must actually be
    /// filtered, including the child entities whose ownership a parent already implies: EF does
    /// not apply a parent's filter to a direct query against the child's own DbSet.
    /// </summary>
    [Fact]
    public void Every_business_owned_entity_has_a_query_filter_and_an_index()
    {
        var model = BuildModel();

        var owned = model
            .GetEntityTypes()
            .Where(entityType => typeof(IBusinessOwned).IsAssignableFrom(entityType.ClrType))
            .ToArray();

        Assert.NotEmpty(owned);

        var unfiltered = owned
            .Where(entityType => entityType.GetDeclaredQueryFilters() is not { Count: > 0 })
            .Select(entityType => entityType.ClrType.Name)
            .ToArray();

        Assert.True(
            unfiltered.Length == 0,
            $"Business-owned entities without a global query filter: {string.Join(", ", unfiltered)}.");

        var unindexed = owned
            .Where(entityType => !entityType.GetIndexes().Any(index =>
                index.Properties.Any(property => property.Name == nameof(IBusinessOwned.BusinessId))))
            .Select(entityType => entityType.ClrType.Name)
            .ToArray();

        Assert.True(
            unindexed.Length == 0,
            $"Business-owned entities without an index on BusinessId: {string.Join(", ", unindexed)}.");
    }

    /// <summary>
    /// Every uniqueness rule on tenant-owned data must be scoped by business.
    ///
    /// A globally unique index in a shared database is a cross-tenant defect even though it leaks
    /// nothing: whichever business writes a value first silently denies it to every other one.
    /// Keys are covered too - a primary key that is a remote identifier, as NayaxSales'
    /// TransactionID once was, has exactly the same problem.
    /// </summary>
    [Fact]
    public void Every_unique_constraint_on_business_owned_data_includes_the_business()
    {
        var owned = BuildModel()
            .GetEntityTypes()
            .Where(entityType => typeof(IBusinessOwned).IsAssignableFrom(entityType.ClrType))
            .ToArray();

        var globallyUnique = owned
            .SelectMany(entityType => entityType
                .GetIndexes()
                .Where(index => index.IsUnique)
                .Where(index => index.Properties.All(p => p.Name != nameof(IBusinessOwned.BusinessId)))
                .Select(index =>
                    $"{entityType.ClrType.Name}({string.Join(", ", index.Properties.Select(p => p.Name))})"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            globallyUnique.Length == 0,
            "These unique indexes on business-owned entities are global rather than per-business, "
                + "so one business can deny another a legitimate row: "
                + $"{string.Join(", ", globallyUnique)}. Add BusinessId as the leading column.");
    }

    /// <summary>
    /// The local primary key must be the application's own, never a remote identifier whose
    /// uniqueness only holds inside the external account that issued it.
    /// </summary>
    [Fact]
    public void No_business_owned_entity_is_keyed_by_a_remote_identifier()
    {
        // Remote identifiers reaching the model today. A key built on one of these would be
        // unique only within a single Nayax operator account.
        string[] remoteIdentifierProperties = ["TransactionID", "MachineID", "MachineId", "SiteId", "NayaxProductId"];

        var offenders = BuildModel()
            .GetEntityTypes()
            .Where(entityType => typeof(IBusinessOwned).IsAssignableFrom(entityType.ClrType))
            .Select(entityType => new { entityType.ClrType.Name, Key = entityType.FindPrimaryKey() })
            .Where(x => x.Key is not null
                && x.Key.Properties.Any(p => remoteIdentifierProperties.Contains(p.Name, StringComparer.Ordinal)))
            .Select(x => $"{x.Name}({string.Join(", ", x.Key!.Properties.Select(p => p.Name))})")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These entities are keyed by a remote identifier, which is not guaranteed unique "
                + $"across operator accounts: {string.Join(", ", offenders)}. Give the entity its "
                + "own key and make the remote identifier unique per business instead.");
    }

    /// <summary>
    /// Documents the ownership decision for the areas issue #64 called out by name, so a change
    /// to any of them is a deliberate edit here rather than a silent drift.
    /// </summary>
    [Theory]
    [InlineData(typeof(Product))]
    [InlineData(typeof(Category))]
    [InlineData(typeof(Supplier))]
    [InlineData(typeof(Purchase))]
    [InlineData(typeof(PurchaseItem))]
    [InlineData(typeof(StockAdjustment))]
    [InlineData(typeof(SupplierOrder))]
    [InlineData(typeof(SupplierOrderLine))]
    [InlineData(typeof(SupplierOrderReceiptAllocation))]
    [InlineData(typeof(NayaxSales))]
    [InlineData(typeof(OperatingExpense))]
    [InlineData(typeof(SiteCommissionAgreement))]
    [InlineData(typeof(CommissionPayment))]
    [InlineData(typeof(NayaxProcessingFeeRate))]
    [InlineData(typeof(InventoryCostTransitionBaseline))]
    [InlineData(typeof(InventoryCostTransitionMachineStock))]
    [InlineData(typeof(InventoryCostTransitionPreviewDraft))]
    [InlineData(typeof(InventoryCostRepair))]
    [InlineData(typeof(ImportedFile))]
    [InlineData(typeof(ImportedReimbursement))]
    [InlineData(typeof(ImportedReimbursementDevice))]
    [InlineData(typeof(ImportedDevicePayment))]
    [InlineData(typeof(ImportedFee))]
    [InlineData(typeof(ImportedPaymentMethod))]
    public void The_named_entity_is_business_owned(Type entityType)
    {
        Assert.True(
            typeof(IBusinessOwned).IsAssignableFrom(entityType),
            $"{entityType.Name} is tenant-owned data and must implement IBusinessOwned.");
    }
}
