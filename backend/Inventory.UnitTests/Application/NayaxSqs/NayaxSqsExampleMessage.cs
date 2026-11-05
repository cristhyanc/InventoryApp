using System.Text.Json.Nodes;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// The Nayax SQS transaction message exactly as the Nayax developer portal publishes it
/// (<c>docs/manage-data-operations/amazon-sqs/set-up-nayax-core/transaction-json-reference</c>, read
/// through the Nayax documentation MCP server on 5 November 2026), used by the issue #1102 tests.
///
/// <para>It is the published documentation sample and every value in it is fictional - a simulator
/// machine, a test actor hierarchy and made-up card and consumer numbers - so it is a contract
/// fixture rather than business data. It is kept verbatim on purpose: the point of
/// <c>NayaxSqsTransactionPrivacyTests</c> is that none of the card and consumer values this document
/// genuinely contains can reach an outcome, a reason or an exception, and a fixture trimmed down to
/// the fields the parser reads would prove nothing at all.</para>
///
/// <para>The example predates the selectable status columns, so it carries no
/// <c>Transaction Status ID</c>, <c>Transaction Status Name</c> or
/// <c>Is Revalue Reward Transaction</c>: Nayax Core's Transactions Report tab is where an operator
/// selects those, and parent #1100's setup step says to select them.
/// <see cref="PublishedWith"/> adds them the way a real message carries them - as keys of the same
/// <c>Data</c> object.</para>
/// </summary>
internal static class NayaxSqsExampleMessage
{
    /// <summary>The published example message, verbatim.</summary>
    public const string Published = """
        {
          "TransactionId": 3804536984,
          "RemoteStartTransactionId": null,
          "PaymentMethodId": 2,
          "SiteId": 6,
          "MachineTime": "2020-04-20T10:34:05.063",
          "Void": false,
          "MachineId": 54265,
          "Data": {
            "Card String": "0077020491",
            "Settlement Time (Date only)": "2020-04-20",
            "Settlement Time (Time only)": "07:34:15",
            "SeValue": 1.0000,
            "Operator Identifier": "500905",
            "Machine Group": "Snack's",
            "Raw ENI Loyalty Num": null,
            "Machine Name": "Oleg's Simulator 1",
            "Machine AuTime": "2020-04-20T10:34:05.063",
            "Machine SeTime": "2020-04-20T10:34:15.433",
            "Currency": "EUR",
            "Brand": null,
            "CLI": null,
            "Extra Charge": 0.0000,
            "Payment Service (Mobile using Credit Card)": "Prepaid Credit using Prepaid Credit",
            "Payment Method ID (1)": 2,
            "Recognition Method ID (3)": 3,
            "Catalog Number": "",
            "Device Number": "0000000000500902",
            "Actor Hierarchy": "Dually / Nayax Test Bunny / Connect 2020 Rocks /",
            "Payment Method Description": "Prepaid Credit",
            "Recognition Description": "Prepaid Credit",
            "Card First 4 Digits": "0077",
            "Card Last 4 Digits": null,
            "Card Type": "MIFARE received as Hexadecimal",
            "Transaction ID": 3804536984,
            "Site ID": 6,
            "Authorization Time": "2020-04-20T07:34:05.063",
            "Authorization Value": 6.0000,
            "PayServTransid": "200420073405",
            "sePayServTransId": null,
            "Settlement Time": "2020-04-20T07:34:15.433",
            "Cancel Type": null,
            "Is Revalue Transaction": false,
            "Preselection Status": 0,
            "Is Phone Registration": false,
            "Is Multivend": false,
            "Settlment Failed": null,
            "Sale ID": -1,
            "Sale Value": 0.0000,
            "Updated DT": "2020-04-20T07:34:15.433",
            "Constant Preauthorization Value": null,
            "Is Partial Confirmation": null,
            "Authorization Code": null,
            "Authorization Date and Time": null,
            "Authorization RRN": "200420073405",
            "Event Code": null,
            "Guest Name": null,
            "Token": null,
            "Zip Code": null,
            "Billing Provider ID": null,
            "AVS Only": null,
            "BOD Transaction Key": null,
            "Disable Debit Cards": null,
            "Force Transactions Terminal": null,
            "Use Phone Transaction": null,
            "License ID": null,
            "Merchant ID": null,
            "Billing Site ID": null,
            "Terminal ID": null,
            "User Password": null,
            "With ZIP": null,
            "Use Phone Contactless": false,
            "Use Phone Contact": false,
            "Debit Card Prefix": null,
            "Actor Description": "Connect 2020 Rocks",
            "Institute Description": "NAYAX US",
            "Location Code": 234234,
            "Location Description": "Booth 134",
            "Operator Institute Code": 4,
            "Area Description": "Connect 2020 Rocks",
            "OP Button Code": "Z1",
            "Barcode": null,
            "Cost Price": null,
            "Card Price": null,
            "Prepaid Price": null,
            "Machine Price": 4.0000,
            "Cash Price": null,
            "Default Price": null,
            "Actor Code": 12,
            "Display Card Number": "0077020491",
            "Card Holder Name": "Automatically Created",
            "User Identity": "1",
            "Billing Provider Name": null,
            "Is Offline Transaction": null,
            "Is EMV Transaction": null,
            "Machine AuTime (Date only)": "2020-04-20",
            "Machine AuTime (Time only)": "10:34:05",
            "Machine SeTime (Date only)": "2020-04-20",
            "Machine SeTime (Time only)": "10:34:15",
            "Updated DT (Date only)": "2020-04-20",
            "Updated DT (Time only)": "07:34:15",
            "Customer Type": 1,
            "Actor ID": 32622,
            "Client Id": "",
            "Contract Name": null,
            "Payout Day": null,
            "Contract Id": null,
            "Airport Id": null,
            "Is Refund Card": false,
            "Contract Number": null,
            "Airport Code": null,
            "Payed Value": 1.0000,
            "Consumer ID": 1906814004668638903,
            "Discount Card ID": 340897206534728,
            "Discount Card Number": "1424243531",
            "Discount Card User Identity": "",
            "Discount Card Physical Type ID": 30000531,
            "Discount Card Activation Date": null,
            "Discount Card Expiration Date": null,
            "Products": [
              {
                "Product(Product Name(MDB Code,PA Code))": "Bounty(0,A1)",
                "Product Group": "Snacks",
                "Product Code in Map": 0,
                "Product PA Code": "A1",
                "Product Volume Type": "600 ml",
                "Product Name": "Bounty",
                "Product VAT Id": null,
                "Product Tax Value": null,
                "Product Tax Code": null,
                "Product Vat Amount": null,
                "Product Net Price": null,
                "Product External Prepaid Price": null,
                "Product Group Code": null,
                "Product Group Sub Code": null,
                "Product Retail Price": null,
                "Product Discount Percentage": 0.00,
                "Product Discount Amount": 0.0000,
                "Product Bruto": 1.0000,
                "Product Catalog Number": ""
              }
            ]
          }
        }
        """;

    /// <summary>
    /// The card and consumer values the published example genuinely carries. No outcome, reason or
    /// exception text may contain any of them: the application has no use for a consumer's identity,
    /// so the safest place for it is nowhere.
    /// </summary>
    public static readonly string[] CardAndCustomerValues =
    [
        "0077020491",                 // Card String, and Display Card Number
        "0077",                       // Card First 4 Digits
        "MIFARE received as Hexadecimal", // Card Type
        "Automatically Created",      // Card Holder Name
        "1906814004668638903",        // Consumer ID
        "340897206534728",            // Discount Card ID
        "1424243531",                 // Discount Card Number
        "30000531",                   // Discount Card Physical Type ID
        "0000000000500902",           // Device Number
    ];

    /// <summary>
    /// The published example with <paramref name="dataFields"/> set on its <c>Data</c> object: a key
    /// the example does not carry is added, as the selectable Nayax Core columns arrive, and one it
    /// does carry is replaced. Each value is the raw JSON token the field arrives as, so a case can
    /// state the difference between a missing key, an explicit <c>null</c> and a wrong type.
    ///
    /// <para>The document is edited through <see cref="JsonNode"/> rather than by string surgery so
    /// that a replaced key really replaces the original rather than producing a document with the
    /// key twice, and so that the example's exact numeric text - the four-decimal amounts and the
    /// nineteen-digit consumer id - survives untouched.</para>
    /// </summary>
    public static string PublishedWith(params (string Key, string Json)[] dataFields)
    {
        var root = JsonNode.Parse(Published)!.AsObject();
        var data = root["Data"]!.AsObject();

        foreach (var (key, json) in dataFields)
            data[key] = JsonNode.Parse(json);

        return root.ToJsonString();
    }
}
