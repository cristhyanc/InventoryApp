namespace Inventory.Domain.Providers;

/// <summary>
/// Which way provider data reached the application (issue #730, parent #329 decision P9).
///
/// <para>It is part of a coverage record's identity because the three routes answer different
/// questions: a sales synchronization and a sales file import both account for sale instants, while
/// a settlement import accounts for the reimbursement periods the provider itself reported. A report
/// figure therefore reads the sources that feed it and no others - sales figures do not become
/// complete because a settlement file arrived.</para>
///
/// <para>Numbered from 1, for the same reason <see cref="ExternalProvider"/> is: zero is what an
/// unwritten column and a default-constructed entity carry, and no source should be reachable by
/// forgetting to set one.</para>
/// </summary>
public enum ProviderDataSource
{
    /// <summary>The live rolling-window sales synchronization (<c>SyncLatestNayaxSales</c>).</summary>
    SalesSync = 1,

    /// <summary>
    /// An uploaded transaction export (<c>ImportNayaxSales</c>).
    ///
    /// <para>Part of the vocabulary, and read as a sales source wherever coverage is interpreted,
    /// but <b>nothing writes it today</b>: a transaction export declares neither its range nor its
    /// scope, so importing one proves no interval of business-wide coverage. See
    /// <c>ImportNayaxSales</c> for why, and do not reintroduce a range derived from the instants a
    /// workbook happens to contain.</para>
    /// </summary>
    SalesFileImport = 2,

    /// <summary>An imported settlement/reimbursement file (<c>ImportPendingReimbursementXmlFiles</c>).</summary>
    SettlementImport = 3,
}
