using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Step 2 of issue #758's data migration: the one-off, audited name resolution, and the failure
    /// gate that refuses to activate the release while a sale that is costed today would come out
    /// unresolved.
    ///
    /// <para><strong>Why it is a second migration.</strong> The gate below can stop the release, and
    /// a conflicting sale is settled only by an audited human decision. A migration that aborts rolls
    /// its own work back, so the tables an operator records that decision in - and the conflicts it
    /// is about - are created and filled by <c>AddNayaxSaleProductAssociation</c>, which always
    /// succeeds. The operator then runs <c>resolve-nayax-sale-products --apply</c>, which is a
    /// human-invoked command that applies no migration, and the next start gets past this one.</para>
    ///
    /// <para><strong>The name rule is the readers' current rule, not a new one.</strong> A sale is
    /// eligible only while it is still unresolved, is <em>not</em> conflicting, and carries a name;
    /// it is resolved only when exactly one product of its business normalizes to the same name under
    /// <c>Inventory.Domain.Reporting.ProductMatching.ProductMatcher</c>'s rule - everything before a
    /// <c>(</c>, trimmed, compared case-insensitively. Ambiguous and absent names stay null. Every
    /// resolution writes a <c>NayaxSaleProductResolutions</c> row carrying the old value, the new
    /// value and the rule, and only ever fills a null, which is what makes rerunning it a no-op.</para>
    ///
    /// <para><strong>Where SQL cannot promise the same answer, it does not answer.</strong> SQLite's
    /// <c>NOCASE</c> folds ASCII only and its <c>TRIM</c> removes the characters it is given, while
    /// .NET's <c>OrdinalIgnoreCase</c> and <c>Trim()</c> fold and trim the whole of Unicode. Over an
    /// all-ASCII catalogue the two rules are identical character for character; over a catalogue
    /// holding any non-ASCII product name they are not provably identical, and a backfill must never
    /// assign a product the current code would not. So the name step is skipped entirely for a
    /// business whose catalogue holds a non-ASCII product name, or for a sale whose own product name
    /// is not ASCII. Those sales stay unresolved - and if any of them is costed, the gate below stops
    /// the release so a human resolves it through the audited command rather than a guess.</para>
    ///
    /// <para><strong>The failure gate.</strong> A sale that carries a historical cost today and would
    /// come out with no product aborts the migration, naming the rule rather than the rows. Such a
    /// sale would otherwise be silently dropped from every future cost replay while still carrying
    /// COGS, which is the one outcome this slice exists to prevent. Uncosted unresolved sales do not
    /// stop anything: they are reported as unresolved, exactly as an unmatched sale already was.</para>
    ///
    /// <para><c>Down</c> deliberately does nothing. It cannot un-know which product a sale belongs
    /// to, and clearing the column would discard an attribution the forward step proved; the
    /// schema's own removal belongs to <c>AddNayaxSaleProductAssociation</c>.</para>
    /// </summary>
    public partial class ResolveNayaxSaleProductsByName : Migration
    {
        /// <summary>
        /// The guard the refusal is raised through: a one-column scratch table whose inserts a
        /// trigger aborts whenever the count being inserted is not zero. SQLite can only raise an
        /// error from a trigger, so this is how a migration says "fail loudly, with the reason".
        /// </summary>
        private const string GuardTable = "NayaxSaleProductResolutionGuard";

        /// <summary>
        /// <c>ProductMatcher.NormalizeName</c> in SQL: everything before the first <c>(</c>, trimmed
        /// of the ASCII whitespace <c>char.IsWhiteSpace</c> recognises below U+0085.
        /// </summary>
        private const string NormalizedSaleName =
            """
            TRIM(CASE WHEN INSTR(sale."ProductName", '(') > 0
                      THEN SUBSTR(sale."ProductName", 1, INSTR(sale."ProductName", '(') - 1)
                      ELSE sale."ProductName" END,
                 ' ' || CHAR(9) || CHAR(10) || CHAR(11) || CHAR(12) || CHAR(13))
            """;

        private const string NormalizedCandidateName =
            """
            TRIM(CASE WHEN INSTR(candidate."Name", '(') > 0
                      THEN SUBSTR(candidate."Name", 1, INSTR(candidate."Name", '(') - 1)
                      ELSE candidate."Name" END,
                 ' ' || CHAR(9) || CHAR(10) || CHAR(11) || CHAR(12) || CHAR(13))
            """;

        /// <summary>
        /// How many products of the sale's business normalize to the sale's normalized name. Exactly
        /// one is a match; none and several are both "unmapped", as they are in the Domain rule.
        /// </summary>
        private const string CandidateCount =
            $"""
            (SELECT COUNT(*) FROM "Products" candidate
             WHERE candidate."BusinessId" = sale."BusinessId"
               AND {NormalizedCandidateName} = {NormalizedSaleName} COLLATE NOCASE)
            """;

        private const string SingleCandidateId =
            $"""
            (SELECT candidate."Id" FROM "Products" candidate
             WHERE candidate."BusinessId" = sale."BusinessId"
               AND {NormalizedCandidateName} = {NormalizedSaleName} COLLATE NOCASE)
            """;

        /// <summary>
        /// Which sales the name step may resolve. Still unresolved; never a conflicting one; a name
        /// that is not blank once normalized; an ASCII name in an all-ASCII catalogue, so the SQL
        /// fold is provably the Domain fold; and exactly one candidate.
        ///
        /// <para>A sale whose stored id matched a product is already resolved and excluded by the
        /// first condition alone, so this is the readers' existing precedence: id first, then an
        /// unambiguous name.</para>
        /// </summary>
        private const string EligibleForNameResolution =
            $"""
            sale."ProductId" IS NULL
              AND sale."ProductName" IS NOT NULL
              AND {NormalizedSaleName} <> ''
              AND LENGTH({NormalizedSaleName}) = LENGTH(CAST({NormalizedSaleName} AS BLOB))
              AND NOT EXISTS (
                  SELECT 1 FROM "NayaxSaleProductConflicts" conflict
                  WHERE conflict."BusinessId" = sale."BusinessId"
                    AND conflict."NayaxSaleId" = sale."Id")
              AND NOT EXISTS (
                  SELECT 1 FROM "Products" candidate
                  WHERE candidate."BusinessId" = sale."BusinessId"
                    AND LENGTH(candidate."Name") <> LENGTH(CAST(candidate."Name" AS BLOB)))
              AND {CandidateCount} = 1
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The audit row is written first, from the same predicate, so a resolution can never be
            // applied without the evidence of why: the UPDATE below changes exactly the rows this
            // INSERT recorded, because nothing else runs between them.
            migrationBuilder.Sql(
                $"""
                INSERT INTO "NayaxSaleProductResolutions"
                    ("BusinessId", "NayaxSaleId", "TransactionId", "PreviousProductId", "NewProductId",
                     "Rule", "CandidateProductIds", "DecidedBy", "ResolvedAtUtc")
                SELECT sale."BusinessId", sale."Id", sale."TransactionID", NULL, {SingleCandidateId},
                       1, NULL, NULL, strftime('%Y-%m-%d %H:%M:%S', 'now')
                FROM "NayaxSales" sale
                WHERE {EligibleForNameResolution};
                """);

            migrationBuilder.Sql(
                $"""
                UPDATE "NayaxSales" AS sale SET "ProductId" = {SingleCandidateId}
                WHERE {EligibleForNameResolution};
                """);

            CreateGuard(migrationBuilder);

            RefuseWhenNotZero(
                migrationBuilder,
                "A Nayax sale that carries a historical cost today would be left with no local "
                    + "product, so this release is refused rather than activated. Run "
                    + "'resolve-nayax-sale-products --dry-run' to list them, record an audited "
                    + "decision for each with --apply, and start again. Nothing was changed.",
                """
                SELECT COUNT(*) FROM "NayaxSales"
                WHERE "ProductId" IS NULL
                  AND ("CostingStatus" IN (1, 2)
                       OR "UnitCostAtSale" IS NOT NULL
                       OR "CostOfGoodsSold" IS NOT NULL)
                """);

            DropGuard(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing. See the type remarks: this migration added no schema, and clearing a product
            // a sale was proved to belong to would be a loss, not an undo.
        }

        private static void CreateGuard(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql($"""CREATE TABLE "{GuardTable}" ("Offending" INTEGER NOT NULL);""");

        private static void DropGuard(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql($"""DROP TABLE "{GuardTable}";""");

        /// <summary>
        /// Aborts the migration with <paramref name="message"/> when <paramref name="countingSql"/>
        /// counts anything at all, the same scratch-table-and-trigger shape
        /// <c>AddProviderIdentityColumns</c> uses: a trigger is the only way SQLite raises an error
        /// with a message of its own. The message names the rule and no row data, because it reaches
        /// the startup log.
        /// </summary>
        private static void RefuseWhenNotZero(
            MigrationBuilder migrationBuilder, string message, string countingSql)
        {
            var trigger = $"TR_{GuardTable}_Refuse";

            migrationBuilder.Sql(
                $"""
                CREATE TRIGGER "{trigger}" BEFORE INSERT ON "{GuardTable}"
                WHEN NEW."Offending" <> 0
                BEGIN
                    SELECT RAISE(ABORT, '{message.Replace("'", "''")}');
                END;
                """);

            migrationBuilder.Sql($"""INSERT INTO "{GuardTable}" ("Offending") {countingSql};""");

            migrationBuilder.Sql($"""DROP TRIGGER "{trigger}";""");
        }
    }
}
