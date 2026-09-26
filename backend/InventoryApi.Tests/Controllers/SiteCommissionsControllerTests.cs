using Inventory.Domain.Exceptions;
using InventoryApi.Controllers;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// SiteCommissionsController.SaveAgreement no longer returns Conflict(...) directly for an
/// overlapping agreement (issue #59): it throws DomainConflictException, which
/// DomainExceptionHandler now maps centrally to the same 409 this action used to build directly.
/// Its unrelated manual bad-request validation is unchanged.
/// </summary>
public class SiteCommissionsControllerTests
{
    private static AppDbContext CreateDbContext(int businessId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return TestAppDbContext.For(options, businessId);
    }

    private static SiteCommissionsController CreateController(AppDbContext db) =>
        new(db, new Mock<ISiteCommissionService>().Object);

    private static SiteCommissionAgreementDto Dto(long siteId, DateTime from, DateTime? to) =>
        new(siteId, from, to, 0.1m, CommissionFrequency.Monthly, CommissionBasis.GrossSales, 14);

    [Fact]
    public async Task SaveAgreement_throws_domain_conflict_exception_for_an_overlapping_agreement()
    {
        using var db = CreateDbContext(1);
        db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
        {
            SiteId = 1,
            EffectiveFrom = new DateTime(2026, 1, 1),
            EffectiveTo = null,
            CommissionRate = 0.1m,
            Frequency = CommissionFrequency.Monthly,
            Basis = CommissionBasis.GrossSales,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            controller.SaveAgreement(Dto(1, new DateTime(2026, 2, 1), null), CancellationToken.None));
    }

    [Fact]
    public async Task SaveAgreement_still_returns_bad_request_for_invalid_values()
    {
        using var db = CreateDbContext(1);
        var controller = CreateController(db);

        var result = await controller.SaveAgreement(Dto(1, new DateTime(2026, 2, 1), new DateTime(2026, 1, 1)), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Commission agreement values are invalid.", badRequest.Value);
    }
}
