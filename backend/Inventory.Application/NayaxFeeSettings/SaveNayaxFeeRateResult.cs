namespace Inventory.Application.NayaxFeeSettings;

public sealed class SaveNayaxFeeRateResult
{
    private SaveNayaxFeeRateResult(NayaxFeeRateRecord? record, string? validationError)
    {
        Record = record;
        ValidationError = validationError;
    }

    public NayaxFeeRateRecord? Record { get; }

    public string? ValidationError { get; }

    public bool IsValid => ValidationError is null;

    public static SaveNayaxFeeRateResult Success(NayaxFeeRateRecord record) => new(record, null);

    public static SaveNayaxFeeRateResult Invalid(string validationError) => new(null, validationError);
}
