using System.Diagnostics;
using System.Globalization;
using Inventory.Application.PlatformDiagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Inventory.Infrastructure.PlatformDiagnostics;

/// <summary>
/// The SQLite half of the platform diagnostics path (issue #336): one statement, read-only, bounded
/// and interruptible.
///
/// <para><strong>Four independent barriers, not one.</strong> A write, DDL or multi-statement
/// attempt has to defeat all of them, and each is enforced by SQLite rather than by inspecting the
/// text:</para>
/// <list type="number">
///   <item>the connection is opened <c>Mode=ReadOnly</c>, so the database file is never opened for
///   writing at all;</item>
///   <item><c>PRAGMA query_only</c> is set, so the connection refuses to start a write transaction
///   even if it had been opened writable;</item>
///   <item><c>SQLITE_LIMIT_ATTACHED</c> is set to zero, so <c>ATTACH</c> cannot reach another
///   database file - including the live one opened writable;</item>
///   <item><see cref="SqliteDiagnosticsAuthorizer"/> is installed, which denies every action code
///   that is not a read, every table and column outside the permitted surface, and every function
///   outside a small permitted set.</item>
/// </list>
///
/// <para><strong>The timeout interrupts SQLite, not the HTTP wait.</strong> A progress handler is
/// installed for the life of the query and aborts the virtual machine when the deadline passes or
/// the caller's token trips, and the token additionally calls <c>sqlite3_interrupt</c>. Both reach
/// into the running statement, which is what makes an expensive query that never produces a first
/// row interruptible - a plain <c>await</c> timeout would return while SQLite kept stepping, and
/// <c>CommandTimeout</c> alone only bounds lock waiting. The reader and the connection are
/// disposed on every path, so an interrupted query leaves nothing holding the database and the next
/// query opens cleanly.</para>
///
/// <para>Nothing here is request-scoped <c>AppDbContext</c> access, unrestricted or otherwise. The
/// adapter opens its own read-only connection, so the tenant query filters and
/// <c>BusinessOwnershipEnforcer</c> are not bypassed, weakened or involved: there is no EF model in
/// this path to bypass them on.</para>
/// </summary>
public sealed class SqliteDiagnosticsQueryExecutor : IDiagnosticsQueryExecutor
{
    /// <summary>
    /// Virtual-machine instructions between progress callbacks. Small enough that the deadline is
    /// honoured promptly, large enough that the callback is not the cost of the query.
    /// </summary>
    private const int ProgressHandlerInstructionInterval = 1_000;

    private readonly string _readOnlyConnectionString;
    private readonly PlatformDiagnosticsQueryLimits _limits;

    public SqliteDiagnosticsQueryExecutor(
        SqliteDiagnosticsOptions options,
        PlatformDiagnosticsQueryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);

        _readOnlyConnectionString = BuildReadOnlyConnectionString(options.ConnectionString);
        _limits = limits;
    }

    /// <summary>
    /// Rewrites whatever the host configured into a read-only connection string. The mode is set
    /// here rather than taken from configuration so that no appsettings value, environment variable
    /// or App Service setting can open this connection writable.
    ///
    /// Pooling is turned off deliberately. An authorizer and a progress handler are installed on
    /// the native connection for the life of one query and removed afterwards; a connection that is
    /// never reused cannot carry either of them - or the after-effects of an interrupted statement -
    /// into the next query, which is what makes "an interrupted query leaves the next one clean" a
    /// property of the design rather than of the cleanup code being correct.
    /// </summary>
    private static string BuildReadOnlyConnectionString(string configuredConnectionString)
    {
        var builder = new SqliteConnectionStringBuilder(configuredConnectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        return builder.ToString();
    }

    public async Task<DiagnosticsQueryExecution> ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var authorizer = new SqliteDiagnosticsAuthorizer();
        var stopwatch = Stopwatch.StartNew();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_limits.MaxDuration);

        await using var connection = new SqliteConnection(_readOnlyConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);

            // Before the authorizer: it denies SQLITE_PRAGMA, which is the point - the only
            // PRAGMA this connection ever runs is this one, and it runs while nothing a caller
            // submitted has been prepared.
            await using (var queryOnly = connection.CreateCommand())
            {
                queryOnly.CommandText = "PRAGMA query_only = ON;";
                await queryOnly.ExecuteNonQueryAsync(cancellationToken);
            }

            var handle = connection.Handle
                ?? throw new InvalidOperationException(
                    "The diagnostics SQLite connection exposed no handle, so the read-only "
                        + "authorizer and interrupt protections could not be installed.");

            raw.sqlite3_limit(handle, raw.SQLITE_LIMIT_ATTACHED, 0);

            // Held in locals so the delegates stay reachable for as long as SQLite may call them.
            delegate_authorizer authorizeCallback = authorizer.Authorize;
            delegate_progress progressCallback = _ =>
                stopwatch.Elapsed >= _limits.MaxDuration || deadline.IsCancellationRequested ? 1 : 0;

            raw.sqlite3_set_authorizer(handle, authorizeCallback, null);
            raw.sqlite3_progress_handler(handle, ProgressHandlerInstructionInterval, progressCallback, null);

            using var interrupt = deadline.Token.Register(() => raw.sqlite3_interrupt(handle));

            try
            {
                return await ReadAsync(connection, sql, deadline.Token);
            }
            finally
            {
                // Removed before the connection closes, so no callback can outlive the query it
                // was installed for.
                raw.sqlite3_set_authorizer(handle, (delegate_authorizer)null!, null);
                raw.sqlite3_progress_handler(handle, 0, (delegate_progress)null!, null);
            }
        }
        catch (SqliteException exception)
        {
            return Translate(exception, authorizer, stopwatch.Elapsed, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Interrupted(stopwatch.Elapsed, cancellationToken);
        }
    }

    private async Task<DiagnosticsQueryExecution> ReadAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        // Lock waiting is not the mechanism that enforces the duration ceiling - the progress
        // handler is - but it must not be able to outlast it either, so it is capped at the same
        // ceiling rounded up to the whole second this property takes.
        command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(_limits.MaxDuration.TotalSeconds));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new List<string>(reader.FieldCount);
        for (var field = 0; field < reader.FieldCount; field++)
        {
            columns.Add(reader.GetName(field));
        }

        var budget = new DiagnosticsResponseBudget(_limits);
        budget.ChargeColumns(columns);

        var rows = new List<IReadOnlyList<string?>>();
        DiagnosticsTruncationReason? truncation = null;

        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= _limits.MaxRows)
            {
                truncation = DiagnosticsTruncationReason.RowLimit;
                break;
            }

            var row = new string?[reader.FieldCount];
            for (var field = 0; field < reader.FieldCount; field++)
            {
                row[field] = ReadCell(reader, field);
            }

            if (!budget.TryCharge(row))
            {
                truncation = DiagnosticsTruncationReason.ResponseByteLimit;
                break;
            }

            rows.Add(row);
        }

        return new DiagnosticsQueryExecution(
            truncation is null ? DiagnosticsQueryOutcome.Succeeded : DiagnosticsQueryOutcome.Truncated,
            DiagnosticsQueryDenialReason.None,
            truncation is null
                ? null
                : "The result was cut short by a server limit and is not the complete answer.",
            columns,
            rows,
            truncation);
    }

    /// <summary>
    /// Renders one cell as text. The permitted surface is integer identity and foreign-key columns,
    /// but a caller chooses the select list, so a computed real, text or blob value has to render
    /// deterministically: invariant formatting, and hex for a blob rather than lossy text.
    /// </summary>
    private static string? ReadCell(SqliteDataReader reader, int field)
    {
        if (reader.IsDBNull(field))
        {
            return null;
        }

        var value = reader.GetValue(field);

        return value switch
        {
            byte[] blob => Convert.ToHexString(blob),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// Maps a provider failure onto the outcome an operator needs to see. An authorizer refusal is
    /// reported with the identifier that was refused; an interrupt is reported as a timeout or a
    /// cancellation depending on which one actually tripped, never as an empty success.
    /// </summary>
    private DiagnosticsQueryExecution Translate(
        SqliteException exception,
        SqliteDiagnosticsAuthorizer authorizer,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        if (authorizer.DeniedDescription is { } denial)
        {
            return DiagnosticsQueryExecution.Rejected(
                authorizer.DeniedSchemaAccess
                    ? DiagnosticsQueryDenialReason.ForbiddenSchemaAccess
                    : DiagnosticsQueryDenialReason.ForbiddenOperation,
                denial);
        }

        if (exception.SqliteErrorCode == raw.SQLITE_AUTH)
        {
            return DiagnosticsQueryExecution.Rejected(
                DiagnosticsQueryDenialReason.ForbiddenOperation,
                "The statement attempted an operation the read-only diagnostics connection does "
                    + "not permit.");
        }

        if (exception.SqliteErrorCode is raw.SQLITE_INTERRUPT or raw.SQLITE_ABORT)
        {
            return Interrupted(elapsed, cancellationToken);
        }

        if (exception.SqliteErrorCode is raw.SQLITE_READONLY or raw.SQLITE_PERM)
        {
            return DiagnosticsQueryExecution.Rejected(
                DiagnosticsQueryDenialReason.ForbiddenOperation,
                "The diagnostics connection is read-only and refused the statement.");
        }

        // The provider's own message - "no such column", a syntax position - is what the operator
        // who typed the statement needs. It is returned to that one authorised caller and is never
        // written to the audit event, which carries the query shape fingerprint alone.
        return new DiagnosticsQueryExecution(
            DiagnosticsQueryOutcome.Failed,
            DiagnosticsQueryDenialReason.None,
            exception.Message,
            [],
            [],
            null);
    }

    private DiagnosticsQueryExecution Interrupted(TimeSpan elapsed, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested && elapsed < _limits.MaxDuration
            ? new DiagnosticsQueryExecution(
                DiagnosticsQueryOutcome.Cancelled,
                DiagnosticsQueryDenialReason.None,
                "The request was cancelled before the query finished.",
                [],
                [],
                null)
            : new DiagnosticsQueryExecution(
                DiagnosticsQueryOutcome.TimedOut,
                DiagnosticsQueryDenialReason.None,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The query exceeded the {0:0.###} second limit and was interrupted.",
                    _limits.MaxDuration.TotalSeconds),
                [],
                [],
                null);
}
