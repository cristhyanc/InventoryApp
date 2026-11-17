using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Reporting.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The one-column GST registration read behind Bookkeeping's BAS summary (issue #1318), over a real
/// relational SQLite connection with three businesses stored throughout.
///
/// <para><c>Businesses</c> is one of the deliberately global tables, so no query filter scopes this
/// read: the primary key is what confines it to the resolved business. These tests are about that -
/// each business gets its own stored answer and never its neighbour's - and about the three states
/// the column really has, since "not recorded" is what most businesses carry and must never read as
/// a <c>No</c> or a <c>Yes</c>.</para>
/// </summary>
public class EfGstRegistrationStoreTests : IDisposable
{
    private const int Registered = 1;
    private const int NotRegistered = 2;
    private const int NeverRecorded = 3;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfGstRegistrationStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            Business(Registered, "Registered", gstRegistered: true),
            Business(NotRegistered, "Not registered", gstRegistered: false),
            Business(NeverRecorded, "Never recorded", gstRegistered: null));
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Business Business(int id, string name, bool? gstRegistered) => new()
    {
        Id = id,
        Name = name,
        TimeZoneId = "Australia/Sydney",
        Status = BusinessStatus.Active,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        GstRegistered = gstRegistered
    };

    private async Task<bool?> ReadAsync(int businessId)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        return await new EfGstRegistrationStore(db)
            .FindGstRegisteredAsync(BusinessId.From(businessId), CancellationToken.None);
    }

    [Theory]
    [InlineData(Registered, true)]
    [InlineData(NotRegistered, false)]
    [InlineData(NeverRecorded, null)]
    public async Task Each_business_gets_its_own_stored_answer(int businessId, bool? expected)
    {
        Assert.Equal(expected, await ReadAsync(businessId));
    }

    [Fact]
    public async Task A_business_that_does_not_exist_is_answered_as_nothing_recorded_rather_than_as_registered()
    {
        // Fail closed: a broken tenancy state must not produce a BAS estimate.
        Assert.Null(await ReadAsync(99));
    }

    [Fact]
    public async Task The_key_decides_the_answer_and_not_the_scope_the_context_was_opened_with()
    {
        // The resolved id is the whole of the scoping on this global table, so a read addressed to
        // one business cannot return another's answer even from a context opened for the other.
        await using var db = TestAppDbContext.For(_options, NotRegistered);

        var registered = await new EfGstRegistrationStore(db)
            .FindGstRegisteredAsync(BusinessId.From(Registered), CancellationToken.None);

        Assert.True(registered);
    }
}
