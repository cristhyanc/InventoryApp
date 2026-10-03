using Microsoft.Extensions.Logging;

namespace InventoryApi.Tests.Http;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that records every entry with its level, its rendered
/// message, its exception and its structured properties.
///
/// The structured properties matter as much as the message here (issue #165): the exception
/// handlers log through <see cref="Microsoft.Extensions.Logging"/> so Azure Monitor
/// OpenTelemetry can export each named property as a queryable custom dimension, and the KQL
/// queries documented in README.md read them by name. Asserting only the rendered string would
/// let a property be renamed or dropped without a test noticing.
/// </summary>
internal sealed class CapturingLogger<TCategoryName> : ILogger<TCategoryName>
{
    public List<LogEntry> Entries { get; } = new();

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var value in values)
            {
                properties[value.Key] = value.Value?.ToString();
            }
        }

        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, properties));
    }

    internal sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, string?> Properties);
}
