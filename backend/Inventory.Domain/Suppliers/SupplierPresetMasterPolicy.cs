using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Suppliers;

/// <summary>
/// How one platform supplier preset's prices are collected (issue #931, first task of #817): the
/// named collection route, or <see langword="null"/> for a supplier whose prices nothing collects.
///
/// <para><strong>One value per route, not per remote service.</strong> <see cref="BigW"/> is the
/// clearest case: issue #926 collects a BIG W price by name search through one service and falls back
/// to a direct SKU lookup through another, and that whole arrangement is <em>one</em> way of getting
/// a BIG W price. A preset says which route to take; which calls the route makes, in which order and
/// with which fallback, is the collector's own business and changes nothing here. Splitting a route
/// into one value per service would make this master list restate an implementation it cannot see.</para>
///
/// <para><strong>The names are settled here and consumed by the collector work of #924.</strong>
/// Numbered from 1, like every other persisted enum in this application, so a stored
/// <c>0</c> - which nothing writes - is never read as the first declared route by accident.</para>
///
/// <para>A supplier a person price-checks by hand has no connector at all, which is a real state and
/// the one Costco is in: it stays on the list, with its prices collected by nobody, until the owner
/// decides otherwise.</para>
/// </summary>
public enum SupplierPriceConnector
{
    /// <summary>
    /// The Varnmill route, which serves several retailers and therefore needs a connector key naming
    /// which one - <c>coles</c>, <c>woolworths</c> or <c>aldi</c> among the shipped records. It is
    /// the one route whose key is required, and that is the whole reason a connector key exists.
    /// </summary>
    Varnmill = 1,

    /// <summary>The Kmart route. One retailer, so there is nothing for a key to name.</summary>
    Kmart = 2,

    /// <summary>
    /// The BIG W route: a name search with a direct SKU lookup behind it (issue #926). One retailer
    /// and one route, so no key.
    /// </summary>
    BigW = 3,

    /// <summary>
    /// The JB Metro route, over that site's GraphQL API. The site it talks to is the preset's own
    /// <c>Url</c>, which is why a new JB Metro site is a new preset rather than new code and why one
    /// route serves all three shipped JB Metro records with no key between them.
    /// </summary>
    JbMetro = 4,
}

/// <summary>
/// The rules that make a platform supplier preset a <em>supplier master</em> for price checks (issue
/// #931, first task of parent #817): which states and territories it serves, whether the prices it
/// publishes include GST, and how those prices are collected.
///
/// <para>It sits beside <see cref="SupplierPresetPolicy"/> rather than inside it because the two
/// answer different questions. That one owns what a preset is <em>called</em> and how two names are
/// compared; this one owns what a preset <em>claims</em> - a market, a tax basis and a collection
/// route - and every rule here is about the consistency of those three with each other. Neither
/// reads or writes a business, a supplier or a price: a preset is platform configuration, and issue
/// #849's copy into an approved business deliberately carries none of these fields (a
/// <c>Supplier</c> has no served states, no price basis and no connector, and giving it one would be
/// a different decision).</para>
///
/// <para><strong>Three rules, and each of them exists because the alternative is a silent wrong
/// answer.</strong> Nationwide and a state list are mutually exclusive and one of them is required,
/// because "nationwide <em>and</em> NSW" has no meaning a price check could act on and "neither"
/// would be a supplier that serves nowhere while looking configured. The connector key is required
/// for <see cref="SupplierPriceConnector.Varnmill"/>, because that route cannot be taken without
/// knowing which retailer it is for, and it must be empty for every other route, because a key
/// nothing reads is a value that looks meaningful and is not.</para>
///
/// <para><strong>Whether prices include GST is a stored fact, never inferred.</strong> The
/// supermarkets, Kmart and BIG W publish GST-inclusive prices and JB Metro publishes ex-GST ones;
/// nothing here derives that from a name, a connector or an amount, exactly as AGENTS.md § Product
/// GST rules and supplier defaults refuses to derive a supplier's GST default from its registration
/// or its invoice. It is a <see cref="bool"/> because it is a property of the published price list,
/// not of a transaction: this policy calculates no GST at all and no amount passes through it.</para>
///
/// <para><strong>A declared value is the only value.</strong> A connector arrives from a JSON body as
/// an arbitrary integer and a served state as arbitrary text, so both are checked against the
/// vocabularies declared here before anything is stored - the boundary rule
/// <c>Inventory.Domain.Gst.GstClassifications</c> states for a classification, for the same reason:
/// an enum is a compile-time constraint and not a validation.</para>
/// </summary>
public static class SupplierPresetMasterPolicy
{
    /// <summary>
    /// The eight states and territories a preset may serve, as the codes that are stored, published
    /// and displayed.
    ///
    /// <para><strong>The code is the value, deliberately rather than an enum.</strong> "NSW" is the
    /// state's own name rather than a programmer's label for it, so a single spelling travels through
    /// the column, the API, the form and the migration with nothing to translate and nothing to drift
    /// - unlike <see cref="SupplierPriceConnector"/>, whose members name routes a reader of the
    /// database could not guess. The list is closed: a value outside it is refused, so "serves
    /// Jervis Bay" cannot be stored as a state nothing knows about.</para>
    ///
    /// <para>The order is this list's order, not alphabetical, and it is the order a stored value is
    /// written in - see <see cref="NormaliseServedStates"/>.</para>
    /// </summary>
    public static readonly string[] ServedStateCodes =
        ["NSW", "VIC", "QLD", "SA", "WA", "TAS", "ACT", "NT"];

    /// <summary>
    /// The longest accepted connector key, measured on the trimmed value. Generous for what the
    /// shipped records hold (<c>coles</c> is five characters) because the key is whatever the route
    /// it belongs to needs, and a route that needs a longer identifier must not need a migration.
    /// </summary>
    public const int MaxConnectorKeyLength = 100;

    /// <summary>
    /// The separator the stored served-state list uses. A single character with no whitespace, so the
    /// stored value is exactly as long as it needs to be and a reader of the database can see the
    /// codes.
    /// </summary>
    private const char ServedStateSeparator = ',';

    /// <summary>
    /// How long a stored served-state list can possibly be: every declared code, separated. It bounds
    /// the column rather than the rule - the rule is the closed vocabulary above, which no string
    /// length could express - and it is computed from that vocabulary so adding a ninth code could
    /// not leave the column too narrow for it.
    /// </summary>
    public static int MaxStoredServedStatesLength { get; } =
        ServedStateCodes.Sum(code => code.Length) + ServedStateCodes.Length - 1;

    /// <summary>What a caller who claimed neither nationwide service nor any state is told.</summary>
    public const string ServedStatesRequiredMessage =
        "A supplier preset must serve Australia nationwide, or at least one state or territory.";

    /// <summary>What a caller who claimed both nationwide service and a state list is told.</summary>
    public const string NationwideAndStatesMessage =
        "A supplier preset serves Australia nationwide or a list of states and territories, not both.";

    /// <summary>
    /// What a caller who submitted something that is not one of <see cref="ServedStateCodes"/> is
    /// told. Built from the vocabulary itself so it cannot drift from what is accepted.
    /// </summary>
    public static string UnknownServedStateMessage { get; } =
        "A supplier preset's served states must be "
            + string.Join(", ", ServedStateCodes)
            + ".";

    /// <summary>
    /// What a caller who chose a connector that is not one of the declared routes is told. It names
    /// the routes rather than the number submitted, which the caller already has.
    /// </summary>
    public static string UnknownConnectorMessage { get; } =
        "A supplier preset's price connector must be one of "
            + string.Join(", ", Enum.GetNames<SupplierPriceConnector>())
            + ", or none at all.";

    /// <summary>What a caller who chose Varnmill without a key is told.</summary>
    public static string ConnectorKeyRequiredMessage { get; } =
        $"The {nameof(SupplierPriceConnector.Varnmill)} price connector needs a connector key.";

    /// <summary>
    /// What a caller who submitted a key for a route that has none - or for no route at all - is
    /// told. It says which routes take one rather than only that this one does not.
    /// </summary>
    public static string ConnectorKeyNotAllowedMessage { get; } =
        $"Only the {nameof(SupplierPriceConnector.Varnmill)} price connector takes a connector key.";

    /// <summary>What a caller who submitted a key longer than the limit is told.</summary>
    public const string ConnectorKeyTooLongMessage =
        "A price connector key must be 100 characters or fewer.";

    /// <summary>
    /// Whether <paramref name="connector"/> is a declared collection route, or no route at all. No
    /// route is valid: it is the state a supplier somebody price-checks by hand is in.
    /// </summary>
    public static bool IsDeclaredConnector(SupplierPriceConnector? connector) =>
        connector is null || Enum.IsDefined(connector.Value);

    /// <summary>
    /// Whether <paramref name="connector"/> is a route that is identified by a connector key.
    /// <see cref="SupplierPriceConnector.Varnmill"/> is the one that is, because it serves several
    /// retailers through one service; every other route, and no route, is not.
    /// </summary>
    public static bool TakesConnectorKey(SupplierPriceConnector? connector) =>
        connector == SupplierPriceConnector.Varnmill;

    /// <summary>
    /// The served states as they are to be stored: the empty string for a nationwide preset, and
    /// otherwise the submitted codes trimmed, upper-cased, de-duplicated and written in
    /// <see cref="ServedStateCodes"/>'s own order, separated by commas.
    ///
    /// <para>The order is the vocabulary's rather than the submission's so that one claim has exactly
    /// one stored spelling: "ACT and NSW" and "NSW and ACT" are the same market, and two spellings of
    /// it would be two values every later reader and every test would have to compare loosely. A
    /// repeated code is the same claim too, so it is folded rather than refused - a form that offers
    /// eight checkboxes cannot produce one, and a caller who sends one has still said something
    /// unambiguous.</para>
    ///
    /// <para>A nationwide preset stores no list at all, rather than all eight codes: "serves
    /// everywhere" is a different claim from "serves these eight", and collapsing the one into the
    /// other would lose the distinction the next state or territory to be added would need.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// Nationwide was claimed together with a state list, neither was claimed, or a submitted value is
    /// not one of <see cref="ServedStateCodes"/>. Nothing is stored.
    /// </exception>
    public static string NormaliseServedStates(bool servesNationwide, IEnumerable<string?>? states)
    {
        var submitted = (states ?? [])
            .Select(state => (state ?? string.Empty).Trim())
            .Where(state => state.Length > 0)
            .Select(state => state.ToUpperInvariant())
            .ToList();

        if (servesNationwide && submitted.Count > 0)
        {
            throw new DomainValidationException(NationwideAndStatesMessage);
        }

        if (servesNationwide)
        {
            return string.Empty;
        }

        if (submitted.Count == 0)
        {
            throw new DomainValidationException(ServedStatesRequiredMessage);
        }

        if (submitted.Exists(state => !ServedStateCodes.Contains(state, StringComparer.Ordinal)))
        {
            throw new DomainValidationException(UnknownServedStateMessage);
        }

        return string.Join(
            ServedStateSeparator,
            ServedStateCodes.Where(code => submitted.Contains(code, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The served states a stored value names, in <see cref="ServedStateCodes"/>'s own order: the
    /// inverse of <see cref="NormaliseServedStates"/>, and the only way a stored list is read back.
    ///
    /// <para>It is deliberately tolerant of what it finds, because what it reads is a column and not a
    /// submission: a blank or absent value is no states at all, and an unrecognised code - which only
    /// a direct edit of the database could produce - is dropped rather than published as a state this
    /// application cannot describe. A preset in that condition reads as serving nowhere, which is
    /// visibly wrong on the page and is corrected by editing it, where the rules above apply in
    /// full.</para>
    /// </summary>
    public static IReadOnlyList<string> ReadServedStates(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var found = stored
            .Split(ServedStateSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(code => code.ToUpperInvariant())
            .ToList();

        return ServedStateCodes
            .Where(code => found.Contains(code, StringComparer.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// The connector key as it is to be stored: trimmed, and <see langword="null"/> where the route
    /// takes none. Blank is "no key", so it is stored as <see langword="null"/> rather than as an
    /// empty string - "no key" has one spelling, exactly as "no website" does.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The connector is not a declared route, the Varnmill route was chosen without a key, a key was
    /// submitted for a route that takes none, or the key is longer than
    /// <see cref="MaxConnectorKeyLength"/> once trimmed. Nothing is stored.
    /// </exception>
    public static string? NormaliseConnectorKey(SupplierPriceConnector? connector, string? key)
    {
        if (!IsDeclaredConnector(connector))
        {
            throw new DomainValidationException(UnknownConnectorMessage);
        }

        var trimmed = (key ?? string.Empty).Trim();

        if (!TakesConnectorKey(connector))
        {
            // Including when there is no connector at all: a key belongs to the route that reads it,
            // and a route nothing takes reads nothing.
            return trimmed.Length == 0
                ? null
                : throw new DomainValidationException(ConnectorKeyNotAllowedMessage);
        }

        if (trimmed.Length == 0)
        {
            throw new DomainValidationException(ConnectorKeyRequiredMessage);
        }

        if (trimmed.Length > MaxConnectorKeyLength)
        {
            throw new DomainValidationException(ConnectorKeyTooLongMessage);
        }

        return trimmed;
    }
}
