using System.Text.Json.Nodes;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// A Nayax SQS transaction message with the <strong>field names and structure of the first real
/// message</strong> the owner's queue delivered (recorded on issue #1102 on 5 November 2026), and
/// <strong>fictional values only</strong>.
///
/// <para>Nothing from a real transaction is committed here: the ids, the amounts, the machine and
/// product names, and every card, consumer, merchant and terminal value below are invented. What is
/// taken from the real message is only its shape - which keys arrive, at which layer, and with
/// which JSON kinds - because that is what the parser is being tested against and the one thing
/// <see cref="NayaxSqsExampleMessage"/> (the published documentation sample, kept for the
/// allow-list and privacy cases) cannot show:</para>
/// <list type="bullet">
///   <item><c>Data.Products[0]</c> carries <strong><c>"Product ID"</c></strong>, Nayax's own product
///   id. The published sample's product entry does not, which is why the parser used to resolve
///   every sale's product from the slot alone. The Transaction JSON reference does document the
///   field ("Product ID (Nayax Internal Identifier)", <c>Integer</c>) in its one flat field table,
///   which does not say which layer a field sits at; the real message is what puts it inside the
///   product entry.</item>
///   <item><c>Data</c> repeats both root identifiers as <c>"Transaction ID"</c> and
///   <c>"Machine ID"</c>, so a message whose two copies disagree is not a message about one
///   transaction.</item>
///   <item><c>"Cancel Type"</c> is <c>null</c> on a settled card vend - not <c>255</c> - and
///   <c>"Settlment Failed"</c> and <c>"Is Revalue Reward Transaction"</c> arrive as <c>null</c>
///   rather than <c>false</c>. Null means "not set", never a contradiction.</item>
///   <item><c>"Transaction Status ID"</c> is <c>12</c> with <c>"Transaction Status Name"</c>
///   <c>"Settled"</c> on an ordinary card vend, which is the one status value the application's
///   established ids are now confirmed against.</item>
///   <item>The GMT timestamps (<c>"Authorization Time"</c>, <c>"Settlement Time"</c>,
///   <c>"Updated DT"</c>) carry <strong>no <c>Z</c> designator</strong> and are eleven hours behind
///   <c>"Machine AuTime"</c>, the machine's Sydney wall clock - the exact shape issue #471's GMT
///   rule exists for.</item>
///   <item><c>"Cost Price"</c> is under <c>Data</c> rather than on the product entry.</item>
/// </list>
/// </summary>
internal static class NayaxSqsRealMessage
{
    /// <summary>
    /// The real message's shape with invented values: a settled <c>"Credit Card"</c> vend of one
    /// product out of slot 14 on a Sydney machine.
    /// </summary>
    public const string Received = """
        {
          "TransactionId": 4100000001,
          "RemoteStartTransactionId": null,
          "PaymentMethodId": 1,
          "SiteId": 4400001,
          "MachineTime": "2026-11-05T13:42:17.000",
          "Void": false,
          "MachineId": 4200001,
          "Data": {
            "Transaction ID": 4100000001,
            "Machine ID": 4200001,
            "Site ID": 4400001,
            "Machine Name": "Fictional Depot Machine 1",
            "Machine Group": "Fictional Drinks",
            "Transaction Status ID": 12,
            "Transaction Status Name": "Settled",
            "SeValue": 3.5000,
            "Payed Value": 3.5000,
            "Authorization Value": 3.5000,
            "Currency": "AUD",
            "Payment Method Description": "Credit Card",
            "Payment Method ID (1)": 1,
            "Recognition Description": "Credit Card",
            "Payment Service (Mobile using Credit Card)": "Credit Card using Credit Card",
            "Authorization Time": "2026-11-05T02:42:17.000",
            "Settlement Time": "2026-11-05T02:45:03.000",
            "Updated DT": "2026-11-05T02:45:03.000",
            "Machine AuTime": "2026-11-05T13:42:17.000",
            "Machine SeTime": "2026-11-05T13:45:03.000",
            "Cancel Type": null,
            "Settlment Failed": null,
            "Is Revalue Transaction": false,
            "Is Revalue Reward Transaction": null,
            "Is Multivend": false,
            "Is Refund Card": false,
            "Is Offline Transaction": false,
            "Cost Price": 1.2000,
            "Machine Price": 3.5000,
            "Card String": "6066000000009001",
            "Card First 4 Digits": "6066",
            "Card Last 4 Digits": "9001",
            "Card BIN": 606600,
            "Card Type": "Fictional Scheme Contactless",
            "Credit Card Type": "Fictional Scheme Debit",
            "Display Card Number": "606600XXXXXX9001",
            "Card Holder Name": "Fictional Cardholder",
            "Card ID": 6066001,
            "Consumer ID": 6067000000000001,
            "User Identity": "6067001",
            "Loyalty Card Number": "6067002",
            "Merchant ID": "FICTIONALMERCHANT6068",
            "Terminal ID": "FICTIONALTERMINAL6068",
            "Authorization Code": "FICAUTH6069",
            "Authorization RRN": "6069000000001",
            "PayServTransid": "6069000000002",
            "sePayServTransId": "6069000000003",
            "Device Number": "0000000060690004",
            "Device Transaction ID (DTID)": 6069005,
            "Products": [
              {
                "Product ID": 4300001,
                "Product Code in Map": 14,
                "Product PA Code": "B2",
                "Product Name": "Fictional Spring Water 600ml",
                "Product Group": "Fictional Drinks",
                "Product Catalog Number": "FIC-0600",
                "Product Volume Type": "600 ml"
              }
            ]
          }
        }
        """;

    /// <summary>
    /// The card, consumer, merchant and terminal values this document carries. Every one of them is
    /// invented, and none of them may appear in an outcome, a reason or an exception: the
    /// application has no use for a consumer's identity or a processor's reference, so the safest
    /// place for either is nowhere.
    /// </summary>
    public static readonly string[] CardAndCustomerValues =
    [
        "6066000000009001",         // Card String
        "9001",                     // Card Last 4 Digits
        "606600",                   // Card BIN, and the masked display number's prefix
        "Fictional Scheme Contactless", // Card Type
        "Fictional Scheme Debit",   // Credit Card Type
        "606600XXXXXX9001",         // Display Card Number
        "Fictional Cardholder",     // Card Holder Name
        "6066001",                  // Card ID
        "6067000000000001",         // Consumer ID
        "6067001",                  // User Identity
        "6067002",                  // Loyalty Card Number
        "FICTIONALMERCHANT6068",    // Merchant ID
        "FICTIONALTERMINAL6068",    // Terminal ID
        "FICAUTH6069",              // Authorization Code
        "6069000000001",            // Authorization RRN
        "6069000000002",            // PayServTransid
        "6069000000003",            // sePayServTransId
        "0000000060690004",         // Device Number
        "6069005",                  // Device Transaction ID (DTID)
    ];

    /// <summary>
    /// The real message with <paramref name="dataFields"/> applied to its <c>Data</c> object: a
    /// <see langword="null"/> raw token removes the key, so a case can state the difference between
    /// a missing key, an explicit JSON <c>null</c> and a wrong type.
    /// </summary>
    public static string ReceivedWith(params (string Key, string? Json)[] dataFields) =>
        Edited(root => Apply(root["Data"]!.AsObject(), dataFields));

    /// <summary>
    /// The real message with <paramref name="productFields"/> applied to its single product entry,
    /// on the same terms as <see cref="ReceivedWith"/>.
    /// </summary>
    public static string ReceivedWithProduct(params (string Key, string? Json)[] productFields) =>
        Edited(root => Apply(
            root["Data"]!.AsObject()["Products"]!.AsArray()[0]!.AsObject(), productFields));

    /// <summary>
    /// The real message with one root field applied, which is how a message whose <c>Data</c> copy
    /// of an identifier disagrees with the root is written.
    /// </summary>
    public static string ReceivedWithRoot(string key, string? json) =>
        Edited(root => Apply(root, [(key, json)]));

    /// <summary>
    /// The real message with <paramref name="dataFields"/> applied to its <c>Data</c> object
    /// <em>and</em> <paramref name="productFields"/> to its single product entry, on the same terms
    /// as the two methods above. Needed by the refund cases of issue #1107, which have to vary the
    /// status and the product entry in one message. A separate name rather than an overload, so the
    /// <c>cref</c>s above stay unambiguous.
    /// </summary>
    public static string ReceivedWithDataAndProduct(
        (string Key, string? Json)[] dataFields, (string Key, string? Json)[] productFields) =>
        Edited(root =>
        {
            var data = root["Data"]!.AsObject();
            Apply(data, dataFields);
            Apply(data["Products"]!.AsArray()[0]!.AsObject(), productFields);
        });

    /// <summary>
    /// The document edited through <see cref="JsonNode"/> rather than by string surgery, so that a
    /// replaced key really replaces the original instead of producing a document carrying the key
    /// twice, and so the exact numeric text - the four-decimal amounts and the sixteen-digit card
    /// string - survives untouched.
    /// </summary>
    private static string Edited(Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(Received)!.AsObject();
        edit(root);
        return root.ToJsonString();
    }

    private static void Apply(JsonObject target, (string Key, string? Json)[] fields)
    {
        foreach (var (key, json) in fields)
        {
            if (json is null)
                target.Remove(key);
            else
                target[key] = JsonNode.Parse(json);
        }
    }
}
