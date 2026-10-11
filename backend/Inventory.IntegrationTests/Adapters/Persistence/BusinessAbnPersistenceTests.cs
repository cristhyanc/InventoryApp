using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The ABN a business may record on its profile is checksum-validated on the way in, on whichever
/// path writes it (issue #540), and the optional columns beside it store "not recorded" as
/// <c>null</c> rather than as something invented.
///
/// Enforced on <c>SaveChanges</c> for the same reason the business time zone is
/// (<c>PerBusinessTimeZonePersistenceTests</c>): the ABN ends up on tax invoices and in the GST
/// accounting aid, so a number that cannot be an ABN must be refused by the one central place
/// every write passes through, not by whichever endpoint happened to remember.
/// </summary>
public class BusinessAbnPersistenceTests : IDisposable
{
    private const string ValidAbn = "51824753556";

    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public BusinessAbnPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Business NewBusiness(string? abn = null) =>
        new()
        {
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            Abn = abn,
            CreatedAtUtc = CreatedAt,
        };

    [Fact]
    public async Task A_well_formed_ABN_is_stored()
    {
        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            db.Businesses.Add(NewBusiness(ValidAbn));
            await db.SaveChangesAsync();
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(ValidAbn, (await verify.Businesses.SingleAsync()).Abn);
    }

    /// <summary>
    /// A business with no ABN recorded is the ordinary case - the application's existing one, and
    /// every business created before the application form - so <c>null</c> is accepted and the
    /// profile columns beside it stay empty.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_ABN_is_stored_with_every_profile_column_empty()
    {
        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            db.Businesses.Add(NewBusiness());
            await db.SaveChangesAsync();
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var business = await verify.Businesses.SingleAsync();

        Assert.Null(business.Abn);
        Assert.Null(business.ContactEmail);
        Assert.Null(business.GstRegistered);
        Assert.Null(business.AppliedAtUtc);
        Assert.Null(business.DecidedAtUtc);
        Assert.Null(business.RejectionReason);
    }

    /// <summary>
    /// A failing check digit, the wrong number of digits, a non-digit, a value still carrying the
    /// spaces a person typed, and a blank string are all refused, and nothing is stored. The
    /// spaced form is refused deliberately: normalising inside a <c>SaveChanges</c> hook would
    /// rewrite what a caller submitted, so the write path that accepted it normalises first.
    /// </summary>
    [Theory]
    [InlineData("51824753557")]
    [InlineData("5182475355")]
    [InlineData("518247535560")]
    [InlineData("5182475355X")]
    [InlineData("51 824 753 556")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_ABN_that_is_not_well_formed_is_refused_and_nothing_is_stored(string abn)
    {
        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            db.Businesses.Add(NewBusiness(abn));

            var error = await Assert.ThrowsAsync<DomainValidationException>(() => db.SaveChangesAsync());
            Assert.Contains(AustralianBusinessNumber.InvalidMessage, error.Message, StringComparison.Ordinal);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await verify.Businesses.ToListAsync());
    }

    /// <summary>
    /// And on an update, not only on insert: a business that already exists cannot be edited into
    /// holding a number that is not an ABN, and the stored value is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task An_existing_business_cannot_be_updated_to_an_invalid_ABN()
    {
        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            db.Businesses.Add(NewBusiness(ValidAbn));
            await db.SaveChangesAsync();
        }

        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var business = await db.Businesses.SingleAsync();
            business.Abn = "51824753557";

            await Assert.ThrowsAsync<DomainValidationException>(() => db.SaveChangesAsync());
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(ValidAbn, (await verify.Businesses.SingleAsync()).Abn);
    }

    /// <summary>
    /// The lifecycle status and the profile values round-trip through the real schema, including
    /// the three-state GST registration flag: "not registered" is a recorded fact and must not come
    /// back as "unknown".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_profile_values_round_trip(bool gstRegistered)
    {
        var appliedAt = new DateTime(2026, 7, 1, 2, 3, 4, DateTimeKind.Utc);
        var decidedAt = new DateTime(2026, 7, 3, 5, 6, 7, DateTimeKind.Utc);

        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            db.Businesses.Add(new Business
            {
                Name = "Applicant Vending Co",
                Status = BusinessStatus.Rejected,
                ContactEmail = "owner@example.com",
                GstRegistered = gstRegistered,
                Abn = ValidAbn,
                AppliedAtUtc = appliedAt,
                DecidedAtUtc = decidedAt,
                RejectionReason = "The ABN provided belongs to another entity.",
                CreatedAtUtc = CreatedAt,
            });
            await db.SaveChangesAsync();
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var business = await verify.Businesses.SingleAsync();

        Assert.Equal(BusinessStatus.Rejected, business.Status);
        Assert.Equal("owner@example.com", business.ContactEmail);
        Assert.Equal(gstRegistered, business.GstRegistered);
        Assert.Equal(ValidAbn, business.Abn);
        Assert.Equal(appliedAt, business.AppliedAtUtc);
        Assert.Equal(decidedAt, business.DecidedAtUtc);
        Assert.Equal("The ABN provided belongs to another entity.", business.RejectionReason);
    }
}
