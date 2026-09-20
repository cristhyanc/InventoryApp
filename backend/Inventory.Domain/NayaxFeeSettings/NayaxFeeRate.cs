namespace Inventory.Domain.NayaxFeeSettings;

/// <summary>
/// A configured Nayax processing fee rate: non-negative, with no more than four decimal places.
/// </summary>
public readonly struct NayaxFeeRate
{
    public const string ValidationErrorMessage = "Fee must be required, non-negative, and have no more than four decimal places.";

    public decimal Value { get; }

    private NayaxFeeRate(decimal value)
    {
        Value = value;
    }

    public static bool TryCreate(decimal value, out NayaxFeeRate rate)
    {
        if (value < 0m || decimal.Round(value, 4) != value)
        {
            rate = default;
            return false;
        }

        rate = new NayaxFeeRate(value);
        return true;
    }
}
