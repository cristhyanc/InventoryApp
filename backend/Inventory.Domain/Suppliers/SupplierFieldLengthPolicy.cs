using Inventory.Domain.FieldLengths;

namespace Inventory.Domain.Suppliers;

/// <summary>
/// The longest name, contact name, phone, email and address a supplier may be given (issue #1286,
/// audit #1226 finding DB-03), measured after trimming. The supplier URL keeps its own existing
/// limit. A create is judged on what it submits; an update is judged against the stored row, so a
/// supplier that already holds a longer value can still be saved with a different field changed
/// (<see cref="FieldLengthLimit.ChangeRefusal"/>). Stored values are never shortened.
/// </summary>
public static class SupplierFieldLengthPolicy
{
    /// <summary>The longest supplier name.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The longest contact name.</summary>
    public const int MaxContactNameLength = 200;

    /// <summary>The longest phone number.</summary>
    public const int MaxPhoneLength = 50;

    /// <summary>The longest email address (the practical SMTP limit).</summary>
    public const int MaxEmailLength = 254;

    /// <summary>The longest address.</summary>
    public const int MaxAddressLength = 500;

    /// <summary>The field names the refusals use.</summary>
    public const string NameLabel = "Supplier name";

    /// <summary>The field names the refusals use.</summary>
    public const string ContactNameLabel = "Contact name";

    /// <summary>The field names the refusals use.</summary>
    public const string PhoneLabel = "Phone";

    /// <summary>The field names the refusals use.</summary>
    public const string EmailLabel = "Email";

    /// <summary>The field names the refusals use.</summary>
    public const string AddressLabel = "Address";

    /// <summary>
    /// The request field a refusal from this policy is about - <c>name</c>, <c>contactName</c>,
    /// <c>phone</c>, <c>email</c> or <c>address</c> - or <c>null</c> when the message is not one of
    /// this policy's, so the API can report the refusal against the field the person typed in.
    /// </summary>
    public static string? RefusedField(string message) =>
        message switch
        {
            _ when message == FieldLengthLimit.TooLongMessage(NameLabel, MaxNameLength) => "name",
            _ when message == FieldLengthLimit.TooLongMessage(ContactNameLabel, MaxContactNameLength) => "contactName",
            _ when message == FieldLengthLimit.TooLongMessage(PhoneLabel, MaxPhoneLength) => "phone",
            _ when message == FieldLengthLimit.TooLongMessage(EmailLabel, MaxEmailLength) => "email",
            _ when message == FieldLengthLimit.TooLongMessage(AddressLabel, MaxAddressLength) => "address",
            _ => null,
        };

    /// <summary>The first refusal for a new supplier's text, or <c>null</c> when every field fits.</summary>
    public static string? ValidateCreate(SupplierText submitted) =>
        FieldLengthLimit.Refusal(NameLabel, submitted.Name, MaxNameLength)
        ?? FieldLengthLimit.Refusal(ContactNameLabel, submitted.ContactName, MaxContactNameLength)
        ?? FieldLengthLimit.Refusal(PhoneLabel, submitted.Phone, MaxPhoneLength)
        ?? FieldLengthLimit.Refusal(EmailLabel, submitted.Email, MaxEmailLength)
        ?? FieldLengthLimit.Refusal(AddressLabel, submitted.Address, MaxAddressLength);

    /// <summary>The first refusal for an update's text judged against the stored supplier.</summary>
    public static string? ValidateUpdate(SupplierText stored, SupplierText submitted) =>
        FieldLengthLimit.ChangeRefusal(NameLabel, stored.Name, submitted.Name, MaxNameLength)
        ?? FieldLengthLimit.ChangeRefusal(ContactNameLabel, stored.ContactName, submitted.ContactName, MaxContactNameLength)
        ?? FieldLengthLimit.ChangeRefusal(PhoneLabel, stored.Phone, submitted.Phone, MaxPhoneLength)
        ?? FieldLengthLimit.ChangeRefusal(EmailLabel, stored.Email, submitted.Email, MaxEmailLength)
        ?? FieldLengthLimit.ChangeRefusal(AddressLabel, stored.Address, submitted.Address, MaxAddressLength);
}

/// <summary>A supplier's free-text fields judged by length, as stored or as submitted (issue #1286).</summary>
public readonly record struct SupplierText(
    string? Name, string? ContactName, string? Phone, string? Email, string? Address);
