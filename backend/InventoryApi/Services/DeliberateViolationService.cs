// TEMPORARY, issue #154 acceptance criterion 7 only: a deliberate dependency violation used to
// prove the new architecture tests fail. Removed in the very next commit.
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public sealed class DeliberateViolationService
{
    private readonly AppDbContext _db;

    public DeliberateViolationService(AppDbContext db)
    {
        _db = db;
    }

    public DbSet<NayaxSales> Sales => _db.NayaxSales;

    public bool IsCard(string? paymentMethod) =>
        PaymentMethodClassifier.Classify(paymentMethod) == NayaxPaymentType.Card;
}

// A second copy of an authoritative Domain financial rule, inside the API.
public static class SiteCommissionCalculator
{
    public static decimal CommissionAmount(decimal sale) => sale * 0.1m;
}

// A controller that takes no use case and publishes an EF entity.
[ApiController]
[Route("api/deliberate-violation")]
public sealed class DeliberateViolationController : ControllerBase
{
    private readonly ILogger<DeliberateViolationController> _logger;

    public DeliberateViolationController(ILogger<DeliberateViolationController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public ActionResult<List<Product>> Get()
    {
        _logger.LogInformation("deliberate violation");
        return Ok(new List<Product>());
    }
}
