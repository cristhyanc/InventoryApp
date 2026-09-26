using InventoryApi.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace InventoryApi.Http.HealthChecks;

/// <summary>
/// Readiness dependency for issue #164: proves the API can actually reach the database rather
/// than only that the process is running. Scoped to the request's <see cref="AppDbContext"/> so
/// it exercises the same connection/provider the rest of the request would use; an exception from
/// <c>Database.CanConnectAsync</c> is caught by the health check framework itself and reported as
/// unhealthy, so no try/catch is needed here.
/// </summary>
public sealed class AppDbContextHealthCheck : IHealthCheck
{
    private readonly AppDbContext _db;

    public AppDbContextHealthCheck(AppDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var canConnect = await _db.Database.CanConnectAsync(cancellationToken);

        return canConnect
            ? HealthCheckResult.Healthy("The database is reachable.")
            : HealthCheckResult.Unhealthy("The database is not reachable.");
    }
}
