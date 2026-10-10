using Inventory.Application.Nayax;
using Inventory.Infrastructure.Data;

namespace InventoryApi.Bootstrap;

/// <summary>
/// The per-business pair <see cref="NayaxConnectionMigrator"/> migrates through (issue #519): the
/// tenant-scoped context whose transaction makes an apply atomic, and the issue-#518 store that
/// executes every credential statement on it.
///
/// The two are one value because they must be the same unit of work. The apply's transaction is
/// begun on <see cref="Db"/> and covers the store's writes only while <see cref="Store"/> is the
/// store built over that same context; a store over a second context would run outside the
/// transaction and could leave the credential stored with its status never written - the partial
/// state this type exists to make impossible.
/// </summary>
/// <param name="Db">
/// The context scoped to the one business the migration resolved. Scoped, never unrestricted: the
/// central query filters and <c>BusinessOwnershipEnforcer</c> stay in force for every statement
/// (AGENTS.md § Tenant ownership and data isolation).
/// </param>
/// <param name="Store">The issue-#518 connection store built over <paramref name="Db"/>.</param>
public sealed record NayaxConnectionMigrationTarget(AppDbContext Db, INayaxConnectionStore Store);
