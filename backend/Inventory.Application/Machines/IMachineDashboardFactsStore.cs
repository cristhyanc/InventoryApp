using Inventory.Domain.Machines;

namespace Inventory.Application.Machines;

/// <summary>One rolling dashboard period's already-resolved gross revenue and direct-profit facts.</summary>
public readonly record struct MachineDashboardPeriodFacts(
    decimal GrossRevenue,
    MachineDashboardDirectProfitInputs ProfitInputs);

/// <summary>
/// A machine's complete dashboard facts as of a reference instant: gross revenue and direct-profit
/// inputs for each of the six rolling comparison periods, and the facts needed to choose its overall
/// profitability status message.
/// </summary>
public sealed record MachineDashboardFacts(
    MachineDashboardPeriodFacts Today,
    MachineDashboardPeriodFacts CurrentWeek,
    MachineDashboardPeriodFacts PreviousComparableWeek,
    MachineDashboardPeriodFacts LastWeek,
    MachineDashboardPeriodFacts MonthToDate,
    MachineDashboardPeriodFacts TwoWeeksAgo,
    MachineProfitabilityStatusInputs StatusInputs);

/// <summary>
/// Narrow persistence/business-service port for the machine dashboard, owned by the Application
/// layer. Its temporary EF Core implementation composes the still-legacy
/// <c>EffectiveFinancialConfiguration</c>/<c>SiteCommissionCalculator</c>/<c>PaymentMethodClassifier</c>/
/// <c>NayaxTransactionStatusClassifier</c>/<c>INayaxProcessingFeeService</c> (see <c>docs/architecture.md</c>).
/// </summary>
public interface IMachineDashboardFactsStore
{
    Task<MachineDashboardFacts> GetFactsAsync(
        long machineId, long? siteId, DateTime now, CancellationToken cancellationToken);
}
