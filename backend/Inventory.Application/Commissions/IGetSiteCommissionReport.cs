namespace Inventory.Application.Commissions;

public interface IGetSiteCommissionReport
{
    Task<SiteCommissionReport> Handle(
        DateTime from,
        DateTime to,
        long? siteId,
        CancellationToken cancellationToken);
}
