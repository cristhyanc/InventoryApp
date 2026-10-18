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
    /// is not ASCII. Those sales are not guessed at - they are refused by the second gate below, so a
    /// human resolves each one through the audited command rather than losing the attribution it had.
    /// </para>
    ///
    /// <para><strong>Two failure gates, for the two ways an attribution could be lost.</strong> Both
    /// abort the migration naming the rule rather than the rows, because the message reaches the
    /// startup log.</para>
    ///
    /// <list type="number">
    ///   <item><strong>Costed.</strong> A sale that carries a historical cost today and would come
    ///   out with no product. It would otherwise be silently dropped from every future cost replay
    ///   while still carrying COGS.</item>
    ///   <item><strong>Attributed by name today.</strong> A sale that is still unresolved, whose
    ///   stored number names no product of its business, and whose reported name matches exactly one
    ///   product - costed or not. Those are precisely the sales the pre-release readers attributed
    ///   through <c>ProductMatcher</c>'s name fallback and this step could not preserve, which is
    ///   the "costed <em>or attributed to a product today</em>" half of issue #758's gate. Leaving
    ///   one unresolved would move product-level revenue and machine-stock attribution, so the
    ///   release is refused until an audited decision records the product.</item>
    /// </list>
    ///
    /// <para>An unresolved sale that no product's name attributes and that carries no cost stops
    /// nothing: it is reported as unresolved, exactly as an unmatched sale already was. That is also
    /// what keeps the gate clearable - it only ever refuses a sale a human can name a product for.
    /// A conflicting sale is excluded from gate 2 by its own definition (its stored number does name
    /// a product directly), and is covered by gate 1 when it is costed.</para>
    ///
    /// <para><strong>One shape SQLite cannot decide.</strong> Two names that differ only in the case
    /// of a non-ASCII character match under .NET's fold and not under <c>NOCASE</c>, so gate 2
    /// cannot see that such a sale was attributed. <c>resolve-nayax-sale-products --dry-run</c>
    /// evaluates the real <c>ProductMatcher</c> rule in .NET and marks every unresolved sale a
    /// product's name attributes today, which is what the pre-deploy count issue #758 requires is
    /// read from.</para>
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

        /// <summary>
        /// A sale the pre-release readers attributed through the name fallback and this step did not
        /// preserve: still unresolved, its stored number names no product of its business - so the
        /// id branch of <c>ProductMatcher</c> did not answer, and the sale is not a conflicting one -
        /// and exactly one product's name matches. Over the all-ASCII zone the name step above has
        /// already resolved every such sale, so what is left here is what that step skipped.
        /// </summary>
        private const string AttributedByNameToday =
            $"""
            sale."ProductId" IS NULL
              AND sale."ProductName" IS NOT NULL
              AND {NormalizedSaleName} <> ''
              AND NOT EXISTS (
                  SELECT 1 FROM "Products" direct
                  WHERE direct."BusinessId" = sale."BusinessId"
                    AND direct."Id" = sale."NayaxProductId")
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

            RefuseWhenNotZero(
                migrationBuilder,
                "A Nayax sale that a product name attributes today would be left with no local "
                    + "product, so this release is refused rather than activated. Run "
                    + "'resolve-nayax-sale-products --dry-run' to list them, record an audited "
                    + "decision for each with --apply, and start again. Nothing was changed.",
                $"""
                SELECT COUNT(*) FROM "NayaxSales" sale
                WHERE {AttributedByNameToday}
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
