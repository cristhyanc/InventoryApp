using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// The migration attribute and target model for <see cref="AddBusinessLegalAcceptances"/> (issue
    /// #1259).
    ///
    /// <para><strong>This file is hand-written, for the reason
    /// <see cref="AddPurchaseIdempotencyKey"/>'s designer is</strong>: the environment this change
    /// was made in has no <c>dotnet ef</c> tool, so <c>dotnet ef migrations add</c> could not emit
    /// the generated copy of the model. What a designer contributes is the
    /// <see cref="MigrationAttribute"/> that gives the migration its id, which is here verbatim, and
    /// <see cref="Migration.TargetModel"/>, the model as of this migration - and this migration is
    /// the newest one, so that model is exactly <see cref="AppDbContextModelSnapshot"/>'s, which was
    /// updated by hand and is the file the next <c>dotnet ef migrations add</c> will diff
    /// against.</para>
    ///
    /// <para>Nothing in this repository reads a designer's model: production and every test apply
    /// migrations through <c>Migrate()</c>, which runs the <c>Up</c> operations, and
    /// <c>scripts/deployment-migration-preflight.mjs</c> scans migration <em>file names</em> and
    /// skips <c>.Designer.cs</c> entirely. A human regenerating this file with
    /// <c>dotnet ef migrations add</c>'s own output is welcome and changes no behaviour.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261122000000_AddBusinessLegalAcceptances")]
    partial class AddBusinessLegalAcceptances
    {
        /// <inheritdoc />
        public override IModel TargetModel => new AppDbContextModelSnapshot().Model;
    }
}
