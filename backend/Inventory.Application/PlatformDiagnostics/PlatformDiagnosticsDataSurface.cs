namespace Inventory.Application.PlatformDiagnostics;

/// <summary>One permitted table and the exact columns of it a diagnostics query may read.</summary>
/// <param name="Table">The physical table name, as mapped by <c>AppDbContext</c>.</param>
/// <param name="Columns">The physical column names, with no wildcard.</param>
public sealed record PlatformDiagnosticsTable(string Table, IReadOnlyList<string> Columns);

/// <summary>
/// The complete data surface a platform-admin diagnostics query may read (issue #336).
///
/// It is an allow-list of <em>table and column pairs</em>, not of tables: a table appearing here
/// exposes only the columns named with it, and there is no wildcard over current or future
/// columns. A new table, or a new column on a listed table, is therefore denied automatically
/// until a separately reviewed change adds it here - which is the point, because the next column
/// to arrive on one of these tables may well be a credential, an identity field, an imported raw
/// payload or free text.
///
/// The initial surface supports exactly the investigations issue #336 authorises: orphaned rows
/// and cross-business ownership mistakes. Every entry is an identity or foreign-key column, so
/// the surface carries no money, no quantity, no name, no note, no timestamp and no external
/// payload. <c>Businesses</c> deliberately exposes only <c>Id</c> - not <c>Name</c>, which is a
/// real trading name, and not <c>IsActive</c> or <c>CreatedAtUtc</c>.
///
/// Three families are absent on purpose and must stay absent: <c>BusinessMemberships</c> (Entra
/// <c>(tid, oid)</c> identity data), the <c>Nayax*</c> and <c>Imported*</c> tables (raw remote
/// payloads, and where future Nayax token configuration of issue #327 would land), and every
/// free-text or monetary column on the tables that are listed.
///
/// This type is the authoritative contract. <c>Inventory.Infrastructure</c> enforces it inside
/// SQLite itself; nothing may re-state it, widen it per request, or accept it from a caller.
/// </summary>
public static class PlatformDiagnosticsDataSurface
{
    /// <summary>
    /// The permitted pairs, verified against the physical mappings on <c>develop</c>:
    /// <c>Purchase</c> is mapped to <c>Receipts</c> and <c>PurchaseItem</c> to <c>ReceiptItems</c>
    /// by <c>AppDbContext.OnModelCreating</c>, and <c>PurchaseItem.ReceiptId</c> /
    /// <c>StockAdjustment.ReceiptItemId</c> keep their legacy names, so the physical names below
    /// are the "Receipt" spellings rather than the CLR "Purchase" ones.
    /// </summary>
    public static IReadOnlyList<PlatformDiagnosticsTable> Tables { get; } =
    [
        new("Businesses", ["Id"]),
        new("Categories", ["Id", "BusinessId"]),
        new("Suppliers", ["Id", "BusinessId"]),
        new("Products", ["Id", "BusinessId", "CategoryId", "SupplierId"]),
        new("Receipts", ["Id", "BusinessId", "SupplierId"]),
        new("ReceiptItems", ["Id", "BusinessId", "ReceiptId", "ProductId"]),
        new("StockAdjustments", ["Id", "BusinessId", "ProductId", "ReceiptItemId"]),
    ];

    /// <summary>
    /// SQLite compares unquoted and double-quoted identifiers case-insensitively for ASCII, so the
    /// allow-list must too: <c>products</c>, <c>Products</c> and <c>"PRODUCTS"</c> are one table,
    /// and matching them case-sensitively would leave a trivially reachable spelling undecided.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> ColumnsByTable =
        Tables.ToDictionary(
            table => table.Table,
            table => new HashSet<string>(table.Columns, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the table is on the surface at all.</summary>
    public static bool PermitsTable(string? table) =>
        table is not null && ColumnsByTable.ContainsKey(table);

    /// <summary>
    /// True only for a pair that is explicitly listed. An unknown table, an unlisted column on a
    /// listed table, and a blank name all answer <c>false</c>: this is the fail-closed half of the
    /// surface, and it is what makes a newly added column inaccessible without a reviewed change.
    /// </summary>
    public static bool Permits(string? table, string? column) =>
        table is not null
        && column is not null
        && ColumnsByTable.TryGetValue(table, out var columns)
        && columns.Contains(column);
}
