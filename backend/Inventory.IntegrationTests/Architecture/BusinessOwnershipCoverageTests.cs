using Inventory.Domain.Subscriptions;
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
    /// <para><see cref="PlatformOptionVisibility"/> is the second, and the reviewed ownership decision
    /// on issue #873. The structural reason is the setting itself: whether the one switchable Admin
    /// option is offered is a single shared application-wide answer, so there is no business whose
    /// key the row could carry and no filter a business could match it by. One row, one boolean, and
    /// nothing about anybody's data.</para>
    ///
    /// <para><see cref="MasterProduct"/>, <see cref="SupplierListing"/> and
    /// <see cref="ProductMatchSuggestion"/> are the third, fourth and fifth, and the reviewed
    /// ownership decision on issue #923 (parent #817). The structural reason is the same for all
    /// three and is the whole point of the parent issue: a product is looked up at a supplier
    /// <em>once</em> for every business, so a brand, a name, a size, a supplier's shelf and a
    /// matcher's opinion about them are facts about products and about no business at all. None of
    /// them has a <c>BusinessId</c> to carry, and a <see cref="ProductMatchSuggestion"/> in
    /// particular names a folded product description rather than the business whose product produced
    /// it - which is what keeps one business from being able to learn that another is tracking
    /// anything. What a business has instead is its own tenant-owned
    /// <see cref="ProductPriceTracking"/> row, which is asserted to be owned in
    /// <see cref="The_named_entity_is_business_owned"/> below. See docs/architecture.md § Supplier
    /// price catalogue.</para>
    ///
    /// <para><see cref="PriceCollectorRun"/> and <see cref="SupplierPriceObservation"/> are the sixth
    /// and seventh, and the reviewed ownership decision on issue #925 (parent #817). The structural
    /// reason is the one above, one step further on: a product is looked up at a supplier once for
    /// every business, so <em>what that supplier charged</em>, and the run that read it, are facts
    /// about that supplier's shelf and about no business either. Neither has a <c>BusinessId</c> to
    /// carry, and an observation names the listing it is a price of rather than anybody who wanted
    /// that price - which is what keeps one business from learning that another is tracking anything.
    /// What a business does with those prices is its own <see cref="ProductPriceTracking"/> mapping
    /// and the comparison the summary of #930 will run for it. See docs/architecture.md § Supplier
    /// price observations and comparison.</para>
    ///
    /// <para><see cref="PriceCollectorWorkItem"/> is the eighth, and the reviewed ownership decision on
    /// issue #924 (parent #817). The structural reason is the same again: one issued piece of work is
    /// a product description - a master product, or the folded search text of one nothing has matched
    /// yet - and a supplier's shelf, so it is about no business either. There is no
    /// <c>BusinessId</c> to carry and no business the work could be attributed to; what a business has
    /// instead is still its own tenant-owned <see cref="ProductPriceTracking"/> mapping, and the
    /// collector that reads the work list cannot learn which business any mapping came from. See
    /// docs/architecture.md § Collector work list.</para>
    ///
    /// <para><see cref="PriceCollectorResultRow"/> and
    /// <see cref="SupplierListingPendingObservation"/> are the ninth and tenth, and the reviewed
    /// ownership decision on issue #937 (parent #817), whose own "Migration, data and deployment
    /// impact" declares an additive platform table if a separate pending store were needed. The
    /// structural reason is the one above, one step further on again: a reading a collector
    /// submitted, the decision this application made about it, and a reading that was held back
    /// rather than priced are all facts about a supplier's shelf and about no business. A result
    /// row names the run, the issued work item and the supplier's own product id - a product
    /// description and a shelf, never anybody who wanted the search - and a held reading names the
    /// listing it is a reading of. Neither has a <c>BusinessId</c> to carry. See
    /// docs/architecture.md § Collector results.</para>
    ///
    /// <para><see cref="SubscriptionGrandfathering"/> is the eleventh, and the reviewed ownership
    /// decision on issue #966, whose own "Migration, data and deployment impact" declares an additive
    /// table for the grandfathering record. The structural reason is the one
    /// <see cref="PlatformOptionVisibility"/> states: the step runs <em>once, over every business</em>,
    /// so its cutoff instant and its completion marker are one answer for the whole application and
    /// there is no business whose key the row could carry or whose filter could match it. What the
    /// step writes <em>per business</em> is an ordinary tenant-owned <see cref="BusinessPlanGrant"/>
    /// row, stamped and filtered centrally with nothing added - and that row, with
    /// <see cref="BusinessBillingSubscription"/> beside it, is asserted to be owned in
    /// <see cref="The_named_entity_is_business_owned"/> below. It is also the one platform table no
    /// request writes at all: its writer is the human-run <c>grandfather-business-plans</c> command,
    /// and the per-request read of the completion marker is bounded to that one marker. See
    /// docs/architecture.md § Business subscriptions and plan entitlements.</para>
    ///
    /// <para>A platform table is still only <em>writable</em> by the platform-admin surface, which
    /// carries a <em>denied</em> business scope; that is enforced by the endpoint's authorization, not
    /// by the ownership mechanism, because there is no ownership here to enforce. <strong>Reading has
    /// two documented exceptions, each reviewed on its own issue.</strong> Issue #873's one boolean:
    /// <c>GET /api/me/access</c> publishes it to every member, because an option the application
    /// switched off has to stop being offered to the people it was switched off for, and that read
    /// reaches one column and nothing else. And issue #935's "Available to add" suppliers:
    /// <c>GET /api/suppliers/available-presets</c> publishes the id, name and website of the enabled
    /// <see cref="SupplierPreset"/> rows that serve the caller's own business's state and that it
    /// holds no copy of, so a record the platform added later is offered to a business rather than
    /// pushed into it. Neither read takes a caller-supplied filter, and neither publishes anything
    /// about any business - see AGENTS.md § Tenant ownership and data isolation,
    /// docs/architecture.md § Switchable Admin option visibility and § Supplier records by business
    /// state.</para>
    /// </summary>
    private static readonly HashSet<Type> PlatformEntities =
    [
        typeof(SupplierPreset),
        typeof(PlatformOptionVisibility),
        typeof(MasterProduct),
        typeof(SupplierListing),
        typeof(ProductMatchSuggestion),
        typeof(PriceCollectorRun),
        typeof(SupplierPriceObservation),
        typeof(PriceCollectorWorkItem),
        typeof(PriceCollectorResultRow),
        typeof(SupplierListingPendingObservation),
        typeof(SubscriptionGrandfathering),
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
    /// The platform list is closed in the same way and for the same reason: a table claiming to be
    /// about no business is a tenancy decision, not an implementation detail. There are eleven, each
    /// added by its own reviewed issue - the supplier presets (#848), the option-visibility setting
    /// (#873), the shared price catalogue's master products, supplier listings and match suggestions
    /// (#923), the collector runs and observed prices of #925, the collector work items of #924, the
    /// submitted result rows and held readings of #937, and the subscription grandfathering record of
    /// #966 - and a twelfth one arriving is the change this assertion exists to make visible.
    /// </summary>
    [Fact]
    public void Exactly_eleven_entities_are_declared_platform_tables() =>
        Assert.Equal(
            [
                "MasterProduct",
                "PlatformOptionVisibility",
                "PriceCollectorResultRow",
                "PriceCollectorRun",
                "PriceCollectorWorkItem",
                "ProductMatchSuggestion",
                "SubscriptionGrandfathering",
                "SupplierListing",
                "SupplierListingPendingObservation",
                "SupplierPreset",
                "SupplierPriceObservation",
            ],
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

    /// <summary>
    /// Exactly two foreign keys point from a tenant-owned table at a platform table: issue #934's,
    /// where a business's <see cref="Supplier"/> records which <see cref="SupplierPreset"/> it was
    /// copied from, and issue #923's, where a business's <see cref="ProductPriceTracking"/> records
    /// which shared <see cref="MasterProduct"/> its product is.
    ///
    /// <para>They are accepted here <strong>by name</strong> rather than by exception, because each is
    /// a tenancy decision and not an implementation detail. What makes them safe is what they do
    /// <em>not</em> do: the referencing row is still tenant-owned, filtered and stamped exactly as
    /// before; the platform row is still about no business; nothing is read back out of the business;
    /// and the key is <c>Restrict</c>, so the platform cannot delete a row a business depends on and
    /// nothing is cascaded or nulled into a business's data. A <em>third</em> such key, or one in the
    /// other direction - a platform table referencing a tenant-owned one, which would give a row about
    /// no business a dependency on one business's data - is a decision somebody has to make here.</para>
    /// </summary>
    [Fact]
    public void Exactly_two_foreign_keys_run_from_tenant_owned_data_to_a_platform_table()
    {
        var model = BuildModel();

        var crossing = model
            .GetEntityTypes()
            .Where(entityType => typeof(IBusinessOwned).IsAssignableFrom(entityType.ClrType))
            .SelectMany(entityType => entityType.GetForeignKeys())
            .Where(key => PlatformEntities.Contains(key.PrincipalEntityType.ClrType))
            .ToArray();

        var described = crossing
            .Select(key =>
                $"{key.DeclaringEntityType.ClrType.Name}({string.Join(", ", key.Properties.Select(p => p.Name))})"
                    + $" -> {key.PrincipalEntityType.ClrType.Name} {key.DeleteBehavior}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "ProductPriceTracking(MasterProductId) -> MasterProduct Restrict",
                "Supplier(SupplierPresetId) -> SupplierPreset Restrict",
            ],
            described);

        // Optional, so a supplier the business created itself carries no link at all - the normal
        // case, and the one the lock rules read as "this row is the business's own" - and a mapping
        // still waiting for a match names no master product.
        Assert.All(crossing, key => Assert.All(key.Properties, property => Assert.True(property.IsNullable)));
    }

    /// <summary>
    /// And nothing points the other way. A platform table is about no business, so a key from one
    /// into tenant-owned data would make a shared row depend on a single business's data - and would
    /// be unwritable under the denied scope the platform-admin surface carries.
    /// </summary>
    [Fact]
    public void No_platform_table_references_tenant_owned_data()
    {
        var model = BuildModel();

        var offenders = PlatformEntities
            .Select(type => model.FindEntityType(type)!)
            .SelectMany(entityType => entityType.GetForeignKeys())
            .Where(key => typeof(IBusinessOwned).IsAssignableFrom(key.PrincipalEntityType.ClrType))
            .Select(key => $"{key.DeclaringEntityType.ClrType.Name} -> {key.PrincipalEntityType.ClrType.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The one copy per preset per business rule of issue #934, asserted in the model: a filtered
    /// unique index led by <c>BusinessId</c>. Led by it because two businesses legitimately hold a
    /// copy of the same preset, and filtered because every unlinked supplier would otherwise
    /// collide with every other one.
    /// </summary>
    [Fact]
    public void A_business_may_hold_only_one_copy_of_each_supplier_preset()
    {
        var supplier = BuildModel().FindEntityType(typeof(Supplier))!;

        var unique = Assert.Single(supplier.GetIndexes().Where(index => index.IsUnique));

        Assert.Equal(
            [nameof(IBusinessOwned.BusinessId), nameof(Supplier.SupplierPresetId)],
            unique.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(@"""SupplierPresetId"" IS NOT NULL", unique.GetFilter());
    }

    /// <summary>
    /// A business holds at most one <em>grandfathered</em> grant (issue #966), as a filtered unique
    /// index led by the business key: led by it because two businesses legitimately hold one each,
    /// and filtered because the complimentary grants of #982 are a list rather than a single row.
    ///
    /// <para>The filter is SQL, so it carries the source's numeric value as a literal. This test is
    /// what pins that literal to the declared enum: renumbering
    /// <see cref="ManualPlanGrantSource.Grandfathered"/> would otherwise leave an index filtered on a
    /// value nothing writes, which would silently stop refusing a second grandfathered grant.</para>
    /// </summary>
    [Fact]
    public void A_business_may_hold_only_one_grandfathered_plan_grant()
    {
        var grant = BuildModel().FindEntityType(typeof(BusinessPlanGrant))!;

        var unique = Assert.Single(grant.GetIndexes().Where(index => index.IsUnique));

        Assert.Equal(
            [nameof(IBusinessOwned.BusinessId), nameof(BusinessPlanGrant.Source)],
            unique.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(
            FormattableString.Invariant($@"""Source"" = {(int)ManualPlanGrantSource.Grandfathered}"),
            unique.GetFilter());
    }

    /// <summary>
    /// And a business holds at most one billing subscription, as a unique index on the business key
    /// itself - so "the business's subscription" has one answer rather than being whichever row a
    /// read happened to return first.
    /// </summary>
    [Fact]
    public void A_business_may_hold_only_one_billing_subscription()
    {
        var subscription = BuildModel().FindEntityType(typeof(BusinessBillingSubscription))!;

        var unique = Assert.Single(subscription.GetIndexes().Where(index => index.IsUnique));

        Assert.Equal(
            [nameof(IBusinessOwned.BusinessId)],
            unique.Properties.Select(property => property.Name).ToArray());
        Assert.Null(unique.GetFilter());
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
    // The one tenant-owned table of the shared price catalogue (issue #923): the three tables beside
    // it are about products and are declared in PlatformEntities above, and this one is a business
    // saying it wants one of its own products priced - which is exactly what tenant ownership is for.
    [InlineData(typeof(ProductPriceTracking))]
    // The two tenant-owned halves of the business subscription state (issue #966): the plan a
    // business paid for and the plans a person granted it are facts about that one business, while
    // the grandfathering record beside them is about none and is declared in PlatformEntities above.
    [InlineData(typeof(BusinessBillingSubscription))]
    [InlineData(typeof(BusinessPlanGrant))]
    public void The_named_entity_is_business_owned(Type entityType)
    {
        Assert.True(
            typeof(IBusinessOwned).IsAssignableFrom(entityType),
            $"{entityType.Name} is tenant-owned data and must implement IBusinessOwned.");
    }
}
