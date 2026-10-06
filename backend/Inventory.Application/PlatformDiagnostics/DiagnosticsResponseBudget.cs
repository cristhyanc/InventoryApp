using System.Text.Json;

namespace Inventory.Application.PlatformDiagnostics;

/// <summary>
/// Counts the serialised cost of a diagnostics result while it is being read, so the 1 MiB cap is
/// enforced on the bytes that will actually be sent rather than on a row count that stands in for
/// them (issue #336).
///
/// The cost of a cell is the exact JSON encoding of its value plus one byte of punctuation, so a
/// value whose escaping expands it - a control character, a non-ASCII character, an embedded quote -
/// is charged what it really costs rather than its character count. That matters because the caller
/// chooses the expressions in the select list, so the width of a row is caller-influenced even
/// though the data surface is not.
///
/// A row is charged before it is accepted. When charging it would exceed the budget, the read stops
/// and the response is marked truncated: a result that is a prefix must never be presented as
/// complete.
/// </summary>
public sealed class DiagnosticsResponseBudget
{
    /// <summary>Brackets and the separating comma around one row's cells.</summary>
    private const int RowPunctuationBytes = 3;

    private readonly int _budgetBytes;
    private int _spentBytes;

    public DiagnosticsResponseBudget(PlatformDiagnosticsQueryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _budgetBytes = limits.RowByteBudget;
    }

    public int SpentBytes => _spentBytes;

    /// <summary>
    /// Charges the column names against the budget. They are part of the body, so a wide result's
    /// header is not free; charging it first also means the row budget is what remains after it.
    /// </summary>
    public void ChargeColumns(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        foreach (var column in columns)
        {
            _spentBytes += CellCost(column);
        }
    }

    /// <summary>
    /// Charges one row if it fits, and answers whether it did. A row is all-or-nothing: half a row
    /// in the response would be a worse lie than a short one.
    /// </summary>
    public bool TryCharge(IReadOnlyList<string?> row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var cost = RowPunctuationBytes;
        foreach (var cell in row)
        {
            cost += CellCost(cell);
        }

        if (_spentBytes + cost > _budgetBytes)
        {
            return false;
        }

        _spentBytes += cost;
        return true;
    }

    /// <summary>
    /// The exact UTF-8 JSON length of the value, plus one byte for the comma that follows it.
    /// <c>null</c> serialises to <c>null</c>, which is four bytes, not zero.
    /// </summary>
    private static int CellCost(string? value) =>
        JsonSerializer.SerializeToUtf8Bytes(value).Length + 1;
}
