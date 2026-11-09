using System.IO.Compression;
using System.Text;

namespace InventoryApi.Tests.Infrastructure.Imports;

/// <summary>
/// Synthetic reimbursement report archives for the uploaded-ZIP import of issue #1149.
///
/// <para><strong>Every figure, account id and company name here is made up.</strong> What is real is
/// the <em>shape</em>: the owner checked the structure of their own Nayax email on 9 November 2026 -
/// <c>Reports_DD-MM-YY.zip</c> holding <c>MM-YYYY/Reimbursments.zip</c> (Nayax's own spelling)
/// holding a PDF, an XLSX and an XML per report day - and that is what these builders reproduce. No
/// production file, account id, figure or document is in this repository.</para>
/// </summary>
internal static class ReimbursementArchiveFixtures
{
    /// <summary>The made-up operator account id the fixture reports carry.</summary>
    public const string Account = "9900001";

    /// <summary>
    /// One reimbursement report, as the bare <c>row</c> fragment Nayax exports - which the parser
    /// has accepted since issue #299 by retrying it wrapped in a root element.
    /// </summary>
    /// <param name="start">The settled period's inclusive first day.</param>
    /// <param name="end">The settled period's inclusive last day.</param>
    /// <param name="total">The made-up settlement total.</param>
    public static string ReportXml(DateTime start, DateTime end, decimal total) =>
        $"""
        <row report_type="reimbursement" reimbursement_start_date="{start:yyyy-MM-dd}"
             reimbursement_end_date="{end:yyyy-MM-dd}" reimbursement_payout_date="{end.AddDays(7):yyyy-MM-dd}"
             company_name="Fictional Vending Co" customer_id="{Account}" is_reimbursement="1"
             is_invoice="0" active_devices="3" total_devices="4" total="{total}">
          <device entity_id="7001" machine_number="4200001" location="Fictional Depot"
                  total_billable_trans_count="118" total_billable_trans_amount="412.75"
                  processing_fee="12.38" service_fee="9.90" net_amount="390.47" />
          <fees fees_type_id="1" fee_type_descr="Processing" is_service="0" total_sum="12.38"
                total_sum_with_vat="13.62" vat_percentage="10" total_count="118" />
          <paymentMethods payment_method_id="2" payment_method_descr="Credit Card"
                          is_nayax_reimbursement="1" total_sales_count="118" total_sales_sum="412.75" />
        </row>
        """;

    /// <summary>
    /// A report that is well-formed XML and still cannot be parsed: it names one attribute twice, in
    /// two different namespaces, and the parser's raw-attribute record is keyed by the local name.
    /// Nothing Nayax sends looks like this - an uploaded file can, which is the point: it is one
    /// file's own parse failure, not a reason for the whole upload to fail.
    /// </summary>
    public const string AmbiguousAttributeNamesXml =
        """
        <row xmlns:a="urn:fictional:first" xmlns:b="urn:fictional:second"
             a:reimbursement_marker="1" b:reimbursement_marker="2" report_type="reimbursement"
             company_name="Fictional Vending Co" customer_id="9900001" total="876543.21" />
        """;

    /// <summary>XML that is not well-formed at all, carrying its own recognisable marker.</summary>
    public const string MalformedXml = "<row unterminated_marker=\"765432.19\"";

    /// <summary>
    /// The parts of the two unparsable fixtures above that must never reach a log sink: an attribute
    /// name out of the uploaded file - which a parser's own exception message quotes - a company name
    /// and a figure. A log line is not a place to publish settlement figures, and the exception the
    /// parser threw is a copy of the file by another route.
    /// </summary>
    public static IReadOnlyList<string> UnloggableXmlValues { get; } =
        [
            "reimbursement_marker",
            "876543.21",
            "Fictional Vending Co",
            "unterminated_marker",
            "765432.19",
        ];

    /// <summary>The report name Nayax uses: <c>report_&lt;account&gt;_&lt;yyyyMMdd&gt;.xml</c>.</summary>
    public static string ReportName(DateTime day, string extension) =>
        $"report_{Account}_{day:yyyyMMdd}{extension}";

    /// <summary>
    /// The real nested shape: an outer archive holding one folder per month, each holding an inner
    /// archive of one PDF, one XLSX and one XML per report day.
    /// </summary>
    /// <param name="months">
    /// The report days, grouped by the folder they live in. One entry per month in the real file.
    /// </param>
    public static byte[] NestedArchive(params (string Folder, DateTime[] Days)[] months)
    {
        ArgumentNullException.ThrowIfNull(months);

        return Zip(outer =>
        {
            foreach (var (folder, days) in months)
            {
                var inner = Zip(innerZip =>
                {
                    foreach (var day in days)
                    {
                        Write(innerZip, ReportName(day, ".pdf"), "a made-up PDF placeholder");
                        Write(innerZip, ReportName(day, ".xlsx"), "a made-up XLSX placeholder");
                        Write(
                            innerZip,
                            ReportName(day, ".xml"),
                            ReportXml(day, day.AddDays(6), total: 400m + day.Day));
                    }
                });

                Write(outer, $"{folder}/Reimbursments.zip", inner);
            }
        });
    }

    /// <summary>
    /// An archive holding the named entries directly, with the given contents. The shape an operator
    /// who unzipped a level by hand sends, and the one every limit case is built from.
    /// </summary>
    public static byte[] FlatArchive(params (string Name, string Content)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return Zip(zip =>
        {
            foreach (var (name, content) in entries) Write(zip, name, content);
        });
    }

    /// <summary>An archive built by hand, for the cases the two builders above do not cover.</summary>
    public static byte[] Zip(Action<ZipArchive> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            build(zip);
        }

        return buffer.ToArray();
    }

    /// <summary>Writes one text entry.</summary>
    public static void Write(ZipArchive zip, string name, string content) =>
        Write(zip, name, Encoding.UTF8.GetBytes(content));

    /// <summary>Writes one binary entry.</summary>
    public static void Write(ZipArchive zip, string name, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(zip);

        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    /// <summary>
    /// Writes one entry with no compression at all, so its compressed length equals its expanded
    /// length - which is how a case about an absolute size limit avoids also being a case about the
    /// compression ratio.
    /// </summary>
    public static void WriteStored(ZipArchive zip, string name, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(zip);

        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }
}
