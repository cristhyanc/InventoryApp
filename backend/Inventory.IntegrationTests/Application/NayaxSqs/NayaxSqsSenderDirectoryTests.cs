using Inventory.Application.NayaxSqs;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1103's one cross-business lookup: which business an AWS IAM user sends Nayax sales for,
/// answered before any business scope exists.
///
/// <para><strong>It is the eighth reviewed <c>IgnoreQueryFilters</c> path and the first that is not
/// in a request</strong> (AGENTS.md § Tenant ownership and data isolation, under the owner's approval
/// of 5 November 2026), so what bounds it is what it may touch: one method, one statement, two columns
/// of the sender row - the ownership key and the enabled flag - and the business's own lifecycle
/// status from the structurally non-tenant-owned <c>Businesses</c> table. These cases assert that
/// bound as well as the behaviour, because a projection is the only thing standing between "which
/// business is this?" and "what does that business trade?".</para>
/// </summary>
public class NayaxSqsSenderDirectoryTests
{
    private const string SenderA = "AIDAFICTIONALSENDERAA";
    private const string SenderB = "AIDAFICTIONALSENDERBB";

    /// <summary>
    /// The lookup runs on a context that has resolved <strong>no business</strong>, which is the state
    /// the reader's own loop is in - and the state in which the central tenant filter matches none of
    /// the sender rows, which is why the statement has to ignore it.
    /// </summary>
    [Fact]
    public async Task A_recorded_sender_resolves_to_its_business_from_a_context_with_no_business()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderA);
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessB, SenderB);

        await using var denied = TestAppDbContext.Denied(fixture.Options);

        // The fail-closed state this lookup is written for: the ordinary filtered read sees nothing.
        Assert.Empty(await denied.BusinessNayaxSqsSenders.AsNoTracking().ToListAsync());

        var directory = new EfNayaxSqsSenderDirectory(denied);

        var attribution = await directory.FindBusinessBySenderAsync(SenderA, CancellationToken.None);

        Assert.NotNull(attribution);
        Assert.Equal(NayaxSqsSaleFixture.BusinessA, attribution.BusinessId);
        Assert.True(attribution.IsEnabled);
        Assert.Equal(BusinessStatus.Active, attribution.BusinessStatus);

        // And the other business's sender resolves to the other business, which is what makes the
        // mapping an answer rather than a coincidence.
        var other = await directory.FindBusinessBySenderAsync(SenderB, CancellationToken.None);
        Assert.Equal(NayaxSqsSaleFixture.BusinessB, other?.BusinessId);
    }

    /// <summary>
    /// A sender nothing has recorded is <see langword="null"/>, never a guess: no fuzzy match, no case
    /// folding, no prefix. The id is stored exactly as a Super Admin entered it and compared exactly,
    /// because a near-miss would attribute one business's sales to another.
    /// </summary>
    [Theory]
    [InlineData("AIDAFICTIONALSENDERZZ")]
    [InlineData("aidafictionalsenderaa")]
    [InlineData("AIDAFICTIONALSENDERA")]
    [InlineData("AIDAFICTIONALSENDERAAA")]
    [InlineData(" AIDAFICTIONALSENDERAA")]
    public async Task A_sender_that_is_not_exactly_a_recorded_one_resolves_to_nothing(string senderId)
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderA);

        await using var denied = TestAppDbContext.Denied(fixture.Options);

        Assert.Null(
            await new EfNayaxSqsSenderDirectory(denied)
                .FindBusinessBySenderAsync(senderId, CancellationToken.None));
    }

    /// <summary>
    /// Nothing at all to look up reads nothing at all: a message the queue reported no sender for is
    /// refused before the database is asked.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task No_sender_id_resolves_to_nothing(string? senderId)
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using var denied = TestAppDbContext.Denied(fixture.Options);

        Assert.Null(
            await new EfNayaxSqsSenderDirectory(denied)
                .FindBusinessBySenderAsync(senderId, CancellationToken.None));
    }

    /// <summary>
    /// A mapping that is switched off still resolves, and says so: the reader has to tell "nobody has
    /// this id" from "somebody has it and it is off", because the second is a refusal about a business
    /// a platform administrator has deliberately disabled.
    /// </summary>
    [Fact]
    public async Task A_disabled_mapping_resolves_and_reports_that_it_is_off()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderA, isEnabled: false);

        await using var denied = TestAppDbContext.Denied(fixture.Options);

        var attribution = await new EfNayaxSqsSenderDirectory(denied)
            .FindBusinessBySenderAsync(SenderA, CancellationToken.None);

        Assert.Equal(NayaxSqsSaleFixture.BusinessA, attribution?.BusinessId);
        Assert.False(attribution?.IsEnabled);
    }

    /// <summary>
    /// The business's lifecycle status comes from the same statement, so "whose sale is this, and is
    /// that business still trading?" is one answer rather than two reads that could disagree between
    /// them.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    public async Task The_businesss_lifecycle_status_is_reported_by_the_same_statement(
        BusinessStatus status)
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderA);
        await fixture.SetBusinessStatusAsync(NayaxSqsSaleFixture.BusinessA, status);

        await using var denied = TestAppDbContext.Denied(fixture.Options);

        var attribution = await new EfNayaxSqsSenderDirectory(denied)
            .FindBusinessBySenderAsync(SenderA, CancellationToken.None);

        Assert.Equal(status, attribution?.BusinessStatus);
    }

    #region What the lookup may touch

    /// <summary>
    /// The projection, asserted as a <em>set</em>: the ownership key, the enabled flag and the
    /// business's lifecycle status, and nothing else. No sender id is echoed back, no last-message
    /// instant, no revision and none of the row's own instants - and nothing about what the business
    /// trades could be added without this failing.
    /// </summary>
    [Fact]
    public void The_attribution_publishes_only_the_business_key_the_flag_and_the_status()
    {
        Assert.Equal(
            ["BusinessId", "BusinessStatus", "IsEnabled"],
            typeof(NayaxSqsSenderAttribution)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The port is one method and it takes a sender id and nothing else: no predicate, no column
    /// list, no page, no sort and no business id. A method that took any of those would be a way to
    /// ask this adapter a different question.
    /// </summary>
    [Fact]
    public void The_port_is_one_method_that_takes_a_sender_id_and_a_token()
    {
        var method = Assert.Single(typeof(INayaxSqsSenderDirectory).GetMethods());

        Assert.Equal(nameof(INayaxSqsSenderDirectory.FindBusinessBySenderAsync), method.Name);
        Assert.Equal(
            [typeof(string), typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    /// <summary>
    /// The adapter's only constructor dependency is the scope's own <see cref="AppDbContext"/>, so
    /// nothing can hand it a context factory, options, or - the one that matters - an unscoped
    /// context. It is the same thing <c>BusinessScopeCompositionTests</c> asserts of every other
    /// adapter that reaches across businesses.
    /// </summary>
    [Fact]
    public void The_adapter_takes_the_scopes_own_context_and_nothing_else()
    {
        Assert.Equal(
            [typeof(AppDbContext)],
            typeof(EfNayaxSqsSenderDirectory)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType));
    }

    /// <summary>
    /// The one thing a lookup must never do is change anything. The sender row - including the
    /// last-message instant the reader writes elsewhere - is exactly as it was afterwards.
    /// </summary>
    [Fact]
    public async Task The_lookup_writes_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, SenderA);

        await using (var denied = TestAppDbContext.Denied(fixture.Options))
        {
            await new EfNayaxSqsSenderDirectory(denied)
                .FindBusinessBySenderAsync(SenderA, CancellationToken.None);
        }

        await using var read = fixture.Unrestricted();
        var sender = await read.BusinessNayaxSqsSenders.AsNoTracking().SingleAsync();

        Assert.Equal(SenderA, sender.AwsSenderId);
        Assert.True(sender.IsEnabled);
        Assert.Null(sender.LastMessageReceivedAtUtc);
        Assert.Equal(1, sender.Revision);
    }

    #endregion
}
