using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// The migration attribute and target model for <see cref="AddPurchaseIdempotencyKey"/> (issue
    /// #1247).
    ///
    /// <para><strong>This file is hand-written, and it is deliberately not the usual generated copy
    /// of the model.</strong> The environment this change was made in has no <c>dotnet ef</c> tool,
    /// so <c>dotnet ef migrations add</c> could not emit the 4,000-line snapshot every other
    /// <c>.Designer.cs</c> in this folder carries. What a designer actually contributes is two
    /// things: the <see cref="MigrationAttribute"/> that gives the migration its id, which is here
    /// verbatim, and <see cref="Migration.TargetModel"/>, the model as of this migration - and this
    /// migration is the newest one, so that model is exactly
    /// <see cref="AppDbContextModelSnapshot"/>'s, which <em>was</em> updated by hand and is the file
    /// the next <c>dotnet ef migrations add</c> will diff against. Reading it from there says the
    /// same thing a generated copy would, without a 157 KB duplicate nobody reviewed line by
    /// line.</para>
    ///
    /// <para>Nothing in this repository reads a designer's model: production and every test apply
    /// migrations through <c>Migrate()</c>, which runs the <c>Up</c> operations, and
    /// <c>scripts/deployment-migration-preflight.mjs</c> scans migration <em>file names</em> and
    /// skips <c>.Designer.cs</c> entirely. The one thing that would read it is
    /// <c>dotnet ef migrations remove</c> on a <em>later</em> migration, which rewrites the snapshot
    /// from the previous migration's target model; a human regenerating this file with
    /// <c>dotnet ef migrations add</c>'s own output is welcome and changes no behaviour.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261116120000_AddPurchaseIdempotencyKey")]
    partial class AddPurchaseIdempotencyKey
    {
        /// <inheritdoc />
        public override IModel TargetModel => new AppDbContextModelSnapshot().Model;
    }
}
