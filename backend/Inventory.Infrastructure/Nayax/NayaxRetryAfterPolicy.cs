using System.Globalization;
using System.Net.Http.Headers;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The one place a Nayax <c>429 Too Many Requests</c> response's <c>Retry-After</c> header becomes a
/// decision (issue #844): retry this request once after a stated wait, retry it once after the
/// handler's ordinary jittered backoff, or do not retry it at all.
///
/// Nayax documents the 429 itself for Lynx but publishes neither a numeric rate limit nor a
/// <c>Retry-After</c> contract (<c>devzone.nayax.com/docs/manage-data-operations/lynx-api/app-tokens</c>
/// § 429 Too Many Requests), so the header is parsed strictly by RFC 9110 § 10.2.3 - a delay in whole
/// seconds or an HTTP date - and an absent or unparseable value is the normal case rather than an
/// error: it means "wait the backoff we would have waited anyway", never "wait nothing" and never a
/// wait invented from a value this application could not read.
///
/// <see cref="MaxHonouredDelay"/> is the deliberate ceiling (decided 23 October 2026): a page
/// request must not hang while an upstream's own idea of a cool-off period is waited out, so a
/// longer requested wait ends the request with the upstream failure the caller already handles
/// instead of being silently shortened - retrying sooner than Nayax asked is the one thing this
/// policy must never do.
/// </summary>
public static class NayaxRetryAfterPolicy
{
    /// <summary>The longest <c>Retry-After</c> wait this application will sit out before retrying.</summary>
    public static readonly TimeSpan MaxHonouredDelay = TimeSpan.FromSeconds(10);

    private const string RetryAfterHeaderName = "Retry-After";

    /// <summary>
    /// The three date formats RFC 9110 § 5.6.7 allows in an HTTP-date: the preferred IMF-fixdate
    /// (<c>"R"</c>), and the two obsolete forms a lenient upstream may still send. Parsed exactly,
    /// so anything else is malformed rather than guessed at.
    /// </summary>
    private static readonly string[] HttpDateFormats =
    [
        "R",
        "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    ];

    /// <summary>
    /// Decides what a 429's <c>Retry-After</c> header asks for, against <paramref name="utcNow"/> -
    /// the current instant from the application's own clock port, so an HTTP date is interpreted
    /// deterministically and testably rather than from the host's own reading of the time.
    /// </summary>
    public static NayaxRetryAfterDecision Decide(HttpResponseHeaders headers, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!headers.TryGetValues(RetryAfterHeaderName, out var values))
        {
            return NayaxRetryAfterDecision.RetryAfterBaseBackoff;
        }

        var sent = values.ToArray();
        if (sent.Length != 1)
        {
            // Retry-After carries one value; several of them is a malformed header, not a choice
            // between them.
            return NayaxRetryAfterDecision.RetryAfterBaseBackoff;
        }

        var value = sent[0].Trim();

        return value.Length > 0 && value.All(char.IsAsciiDigit)
            ? FromDelaySeconds(value)
            : FromHttpDate(value, utcNow);
    }

    private static NayaxRetryAfterDecision FromDelaySeconds(string digits)
    {
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            // Digits only, yet too large for a 64-bit count of seconds: unambiguously longer than
            // the ceiling, so the same answer as any other over-long wait.
            return NayaxRetryAfterDecision.DoNotRetry;
        }

        if (seconds == 0)
        {
            // "Retry immediately" still gets the ordinary backoff: an upstream that is rate
            // limiting us is the last place to send a request with no delay at all.
            return NayaxRetryAfterDecision.RetryAfterBaseBackoff;
        }

        return seconds > (long)MaxHonouredDelay.TotalSeconds
            ? NayaxRetryAfterDecision.DoNotRetry
            : NayaxRetryAfterDecision.RetryAfter(TimeSpan.FromSeconds(seconds));
    }

    private static NayaxRetryAfterDecision FromHttpDate(string value, DateTime utcNow)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                HttpDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var retryAt))
        {
            return NayaxRetryAfterDecision.RetryAfterBaseBackoff;
        }

        var wait = retryAt.UtcDateTime - utcNow;

        if (wait > MaxHonouredDelay)
        {
            return NayaxRetryAfterDecision.DoNotRetry;
        }

        // A date that has already passed says nothing about how long to wait, so it is the same as
        // no header: the ordinary backoff, not an immediate retry.
        return wait > TimeSpan.Zero
            ? NayaxRetryAfterDecision.RetryAfter(wait)
            : NayaxRetryAfterDecision.RetryAfterBaseBackoff;
    }
}

/// <summary>
/// What <see cref="NayaxRetryAfterPolicy"/> decided about one 429 response.
/// <see cref="HonouredDelay"/> is the wait the upstream asked for and this application accepted;
/// <see langword="null"/> means the handler's own jittered backoff applies, which is why it is a
/// separate value from "do not retry".
/// </summary>
/// <param name="ShouldRetry">Whether this 429 may be retried at all.</param>
/// <param name="HonouredDelay">The upstream's own wait, or <see langword="null"/> for the base backoff.</param>
public readonly record struct NayaxRetryAfterDecision(bool ShouldRetry, TimeSpan? HonouredDelay)
{
    /// <summary>The 429 is returned to the caller as it stands.</summary>
    public static NayaxRetryAfterDecision DoNotRetry { get; } = new(false, null);

    /// <summary>The 429 is retried once, after the handler's ordinary jittered backoff.</summary>
    public static NayaxRetryAfterDecision RetryAfterBaseBackoff { get; } = new(true, null);

    /// <summary>The 429 is retried once, after the wait the upstream asked for.</summary>
    public static NayaxRetryAfterDecision RetryAfter(TimeSpan delay) => new(true, delay);
}
