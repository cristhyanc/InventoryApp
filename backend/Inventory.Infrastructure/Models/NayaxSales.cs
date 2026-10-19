using System.Text.Json.Serialization;
using Inventory.Domain.Providers;

namespace Inventory.Infrastructure.Models;

public class NayaxSales : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The application's own primary key.
    ///
    /// <see cref="TransactionID"/> used to be the key, but it is a Nayax identifier from a remote
    /// operator account, not ours. Its uniqueness is only ever guaranteed within the account that
    /// issued it, so keying local rows by it would mean a second business's import could collide
    /// with - or silently overwrite - the first business's sales. Ownership of the row is ours to
    /// express, so the key is ours too, and the remote identifier is a unique value per business
    /// instead (see AppDbContext).
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The Nayax transaction identifier. Unique per business, not globally: treat it as an
    /// external identity, never as a local key.
    /// </summary>
    public long TransactionID { get; set; }

    public int? TransactionStatusId { get; set; }
    public long MachineID { get; set; }

    /// <summary>
    /// The <strong>local</strong> product this sale belongs to, decided once by the writer that
    /// stored it and never re-derived afterwards (issue #758, parent #329 decision A2c).
    ///
    /// <para>This is the one association costing, the cost replay, the timestamp repair, the reports
    /// and the machine-stock ledger read, so a sale is attributed to the same product everywhere and
    /// a product rename, a duplicate name or a database-generated product id cannot move it. It is a
    /// foreign key on <c>(BusinessId, ProductId)</c> against the products' <c>(BusinessId, Id)</c>
    /// business key, so a sale can never name another business's product, and deleting a referenced
    /// product is <em>restricted</em> - never cascaded, never nulled - because a sale's historical
    /// attribution is financial data.</para>
    ///
    /// <para><c>null</c> means <strong>unresolved</strong>: no reader may fall back to
    /// <see cref="NayaxProductId"/> or to <see cref="ProductName"/> to guess one. An unresolved sale
    /// is not costed and is consumed by no replay; it stays visible and is reported as unresolved.</para>
    /// </summary>
    public long? ProductId { get; set; }

    /// <summary>
    /// The provider the sale originated from. Every writer sets it and the migration set
    /// <see cref="ExternalProvider.Nayax"/> on every row that existed (issue #758); a future provider
    /// switch (#761) relies on knowing which provider issued a stored row's identifiers.
    /// </summary>
    public ExternalProvider Provider { get; set; } = ExternalProvider.Nayax;

    /// <summary>
    /// The provider's own product identifier for this sale, as the provider reported it - the Nayax
    /// <c>NayaxProductID</c>, or <c>null</c> when the payload or the export carried none.
    ///
    /// <para>It is a remote identity and <strong>never a local key</strong>. Before issue #758 this
    /// column's provenance was mixed: the uploaded export wrote the raw remote id, while the
    /// latest-sales synchronization wrote the <em>local</em> product id it had matched. Rows written
    /// since carry the raw provider id and say so through
    /// <see cref="ProviderProductIdIsRaw"/>; rows that predate it are provenance-unknown and nothing
    /// reads them as a product key. The local product is <see cref="ProductId"/>.</para>
    /// </summary>
    public long? NayaxProductId { get; set; }

    /// <summary>
    /// Whether <see cref="NayaxProductId"/> is known to hold the raw provider product id (issue
    /// #758). <c>true</c> on every row this application has written since that change; <c>false</c>
    /// means provenance-unknown - the value may be a raw provider id or the local product id the old
    /// synchronization wrote - so it must not be read as an identifier of anything.
    /// </summary>
    public bool ProviderProductIdIsRaw { get; set; }

    public string? MachineName { get; set; }
    public decimal SettlementValue { get; set; }
    public string? PaymentMethod { get; set; }
    public string? ProductName { get; set; }

    /// <summary>
    /// The instant the sale was authorized, meant to be a true UTC instant (issue #380).
    ///
    /// The column keeps the Nayax field name it was created from, but it does <em>not</em> hold the
    /// Nayax <c>MachineAuthorizationTime</c> payload value, which is machine-local wall-clock time:
    /// the latest-sales synchronization writes this from the authoritative
    /// <c>AuthorizationDateTimeGMT</c> field instead, and an uploaded export writes it from a valid
    /// value in that column, which may also correct a stored instant. Every report, dashboard period
    /// and costing replay compares it as a UTC instant and converts it to the
    /// business date through <c>IBusinessCalendar</c>. Three kinds of row are not verified UTC: rows
    /// ingested before that fix; rows the live synchronization stored between issues #380 and #471
    /// from an offset-free <c>AuthorizationDateTimeGMT</c> value, which were shifted by the host's
    /// own UTC offset; and new sales imported from an export without a usable GMT value, which carry
    /// the export's own unconverted value.
    ///
    /// Repairing them is the separate, explicit Nayax sale timestamp repair maintenance operation
    /// (issue #472): a business-scoped preview over authoritative source evidence, then an apply bound
    /// to it that writes this column and appends a <see cref="NayaxSaleTimestampRepair"/> audit row.
    /// That operation is the only thing in the application that rewrites a stored sale instant other
    /// than the uploaded export import; the latest-sales synchronization never does. See
    /// docs/architecture.md § Nayax sale timestamps.
    /// </summary>
    public DateTime MachineAuthorizationTime { get; set; }
    public decimal? NayaxProductCostPrice { get; set; }
    public decimal? UnitCostAtSale { get; set; }
    public decimal? CostOfGoodsSold { get; set; }
    public SaleCostingStatus CostingStatus { get; set; } = SaleCostingStatus.Pending;
    public SaleCostSource CostSource { get; set; } = SaleCostSource.Unknown;
}
