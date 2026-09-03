namespace InventoryApi.Services;

public enum NayaxPaymentType
{
    Card,
    Cash,
    Unknown
}

public static class PaymentMethodClassifier
{
    public static NayaxPaymentType Classify(string? paymentMethod, string? recognitionDescription = null)
    {
        var result = ClassifySingle(paymentMethod);
        return result == NayaxPaymentType.Unknown ? ClassifySingle(recognitionDescription) : result;
    }

    private static NayaxPaymentType ClassifySingle(string? paymentMethod)
    {
        var value = paymentMethod?.Trim().ToLowerInvariant() ?? string.Empty;
        return value switch
        {
            "cash" => NayaxPaymentType.Cash,
            "credit card" or "prepaid credit" => NayaxPaymentType.Card,
            _ when value.Contains("cash") => NayaxPaymentType.Cash,
            _ when value.Contains("credit") || value.Contains("card") => NayaxPaymentType.Card,
            _ => NayaxPaymentType.Unknown
        };
    }
}
