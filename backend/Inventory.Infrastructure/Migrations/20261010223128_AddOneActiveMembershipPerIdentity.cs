using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Makes "a person belongs to one business at a time" a database rule (issue #522), and gives
    /// every membership the instant its state last changed.
    ///
    /// Three steps, in this order:
    ///
    /// <list type="number">
    ///   <item><b>A check that can only refuse.</b> If any identity already holds more than one
    ///   active membership, the migration aborts with a message naming what to fix and changes
    ///   nothing at all - no column, no index, no row. It never picks one membership to keep:
    ///   choosing which business a person stays in is a human decision about access to financial
    ///   data, and an unattended guess would either strand somebody or hand them the wrong
    ///   ledger. See docs/tenant-rollout.md § One active membership per identity.</item>
    ///   <item><b><c>StatusChangedAtUtc</c>, filled from <c>CreatedAtUtc</c>.</b> That is the only
    ///   instant a pre-existing row records, so it is the honest value: a revoked row's state
    ///   changed at some unrecorded time, and inventing "now" for it would claim a change this
    ///   deployment made. Nothing is inferred about who changed it or why.</item>
    ///   <item><b>The filtered unique index on the active rows.</b> Additive, and additive in the
    ///   strong sense: no table is rebuilt, no row is read into a new table, and the two existing
    ///   indexes - the per-(actor, business) unique one and the unfiltered lookup one the
    ///   per-request membership resolution uses - are untouched.</item>
    /// </list>
    ///
    /// The abort is a <c>RAISE(ABORT, ...)</c> on a temporary table's insert trigger rather than a
    /// constraint violation, because that is the only construct SQLite offers whose error message
    /// is written here rather than assembled from a constraint name. The temporary table and its
    /// trigger live in the <c>temp</c> schema, exist for the length of this one statement batch,
    /// and are dropped again; nothing in the database schema records that they were there.
    ///
    /// Reversible: <c>Down</c> drops the index and the column. It does not try to recreate the
    /// duplicate memberships the upgrade refused to resolve, because it never resolved any.
    /// </summary>
    public partial class AddOneActiveMembershipPerIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: refuse an upgrade the data cannot support, before anything is changed.
            //
            // The count is of identities, not rows, and it is computed with the same NOCASE
            // columns the index will use, so two spellings of one GUID pair count as the one
            // identity they are. An identity with three active memberships is counted once: the
            // message is the same either way, and the operator reads the rows, not the number.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "OneActiveMembershipGuard" (
                    "ConflictingIdentities" INTEGER NOT NULL
                );

                CREATE TEMP TRIGGER "OneActiveMembershipGuardAborts"
                BEFORE INSERT ON "OneActiveMembershipGuard"
                WHEN NEW."ConflictingIdentities" > 0
                BEGIN
                    SELECT RAISE(ABORT, 'Cannot apply AddOneActiveMembershipPerIdentity: at least one identity (DirectoryTenantId, ObjectId) holds more than one active BusinessMembership, and this migration will not choose which one to keep. Revoke all but one active membership per person, then run the migration again. Nothing has been changed. See docs/tenant-rollout.md.');
                END;

                INSERT INTO "OneActiveMembershipGuard" ("ConflictingIdentities")
                SELECT COUNT(*) FROM (
                    SELECT 1
                    FROM "BusinessMemberships"
                    WHERE "IsActive" = 1
                    GROUP BY "DirectoryTenantId", "ObjectId"
                    HAVING COUNT(*) > 1
                );

                DROP TRIGGER "OneActiveMembershipGuardAborts";
                DROP TABLE "OneActiveMembershipGuard";
                """);

            // Step 2: the new column. The scaffolded default is the placeholder a non-nullable
            // column needs in order to be added to an existing table at all; the UPDATE below
            // replaces it on every existing row, and every write path states the value itself.
            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAtUtc",
                table: "BusinessMemberships",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.Sql("""
                UPDATE "BusinessMemberships" SET "StatusChangedAtUtc" = "CreatedAtUtc";
                """);

            // Step 3: the rule itself. The filter is on the active rows only, so revoked rows -
            // the membership history - stay as they are and may exist for several businesses.
            migrationBuilder.CreateIndex(
                name: "IX_BusinessMemberships_DirectoryTenantId_ObjectId_Active",
                table: "BusinessMemberships",
                columns: new[] { "DirectoryTenantId", "ObjectId" },
                unique: true,
                filter: "\"IsActive\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BusinessMemberships_DirectoryTenantId_ObjectId_Active",
                table: "BusinessMemberships");

            migrationBuilder.DropColumn(
                name: "StatusChangedAtUtc",
                table: "BusinessMemberships");
        }
    }
}
