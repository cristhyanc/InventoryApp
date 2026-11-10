using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inventory.Application.ReceiptScanning;
using Inventory.Domain.ReceiptScanning;

namespace Inventory.Infrastructure.ReceiptScanning;

/// <summary>
/// The <see cref="IReceiptReader"/> adapter (issue #1163): one HTTP call to Anthropic's Messages API,
/// through a typed <c>HttpClient</c>, in the shape the Nayax Lynx client already has.
///
/// <para><strong>One call, no retry, no tools, no web access.</strong> The request carries the file,
/// this business's product and supplier names, the fixed system prompt and the fixed output schema -
/// and no <c>tools</c> member at all, so there is nothing for the model to call. The timeout is the
/// configured one, applied by the <c>HttpClient</c> itself; a timeout is reported and never retried,
/// because a scan costs money and the operator decides whether to spend it again.</para>
///
/// <para><strong>The key is a header and nothing else.</strong> It is read once at registration, sent
/// on the one request, and appears in no log, response, exception message or test snapshot - the same
/// rule a Nayax token follows (AGENTS.md § Nayax integration).</para>
///
/// <para><strong>Nothing upstream is echoed.</strong> An error body from the vendor can quote the
/// request, and a request here contains a receipt, so a failure becomes this application's own
/// message with the status code's class and nothing from the body.</para>
///
/// <para><strong>The vendor contract is this adapter's one unverified part.</strong> The request shape
/// - <c>output_config.format</c> with a JSON schema, a base64 image or document content block - and
/// the image, PDF-size and page limits are issue #1163's own specification; this repository's agents
/// have no access to Anthropic's documentation (only the Nayax documentation server, AGENTS.md
/// § Nayax contract verification), so none of it could be re-verified against the published
/// documentation here. Every limit is therefore a setting rather than a constant, and the feature
/// stays switched off until a human provisions the key.</para>
/// </summary>
public sealed class ClaudeReceiptReader : IReceiptReader
{
    /// <summary>The Messages API path, relative to the configured base address.</summary>
    public const string MessagesPath = "v1/messages";

    /// <summary>The header the key travels in.</summary>
    public const string ApiKeyHeader = "x-api-key";

    /// <summary>The header that pins the API version the request is written against.</summary>
    public const string VersionHeader = "anthropic-version";

    /// <summary>The API version this request shape is written against.</summary>
    public const string Version = "2023-06-01";

    /// <summary>
    /// How many output tokens one answer may use. A long receipt's worth of JSON lines, with room to
    /// spare; a truncated answer fails the schema and is reported as unreadable rather than used.
    /// </summary>
    public const int MaxOutputTokens = 8192;

    private readonly HttpClient _http;
    private readonly ReceiptScanningConfiguration _configuration;

    public ClaudeReceiptReader(HttpClient http, ReceiptScanningConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(configuration);

        _http = http;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public async Task<ReceiptReadResult> ReadAsync(
        ReceiptReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_configuration.ApiKey is not { } apiKey)
        {
            // The use case refuses before this, so reaching here is a composition error rather than
            // a state a caller can produce - and it must not become an unauthenticated call.
            throw new ReceiptReaderUnavailableException();
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, MessagesPath)
        {
            Content = JsonContent.Create(Body(request, _configuration.Model)),
        };
        message.Headers.Add(ApiKeyHeader, apiKey);
        message.Headers.Add(VersionHeader, Version);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The HttpClient's own timeout, which is the configured one. A cancelled request - the
            // caller gave up - is left to propagate as the cancellation it is.
            throw new ReceiptReaderTimeoutException();
        }
        catch (HttpRequestException exception)
        {
            throw new ReceiptReaderUnavailableException(
                ReceiptReaderUnavailableMessage, exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The status code and nothing from the body: an upstream error body can quote the
                // request, and the request held a receipt.
                throw new ReceiptReaderUnavailableException(
                    $"The receipt reader answered {(int)response.StatusCode}.");
            }

            try
            {
                var payload = await response.Content
                    .ReadFromJsonAsync<JsonNode>(cancellationToken)
                    .ConfigureAwait(false);

                return Read(payload);
            }
            catch (JsonException exception)
            {
                throw new ReceiptReaderUnavailableException(ReceiptReaderUnavailableMessage, exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ReceiptReaderTimeoutException();
            }
        }
    }

    /// <summary>What an unusable reader is reported as, with nothing from upstream in it.</summary>
    private const string ReceiptReaderUnavailableMessage = "The receipt reader could not be reached.";

    /// <summary>
    /// The request body (issue #1163): the model, the output schema, the system prompt and one user
    /// message holding the file and the business's catalogue. <strong>No <c>tools</c> member</strong> -
    /// structured output through <c>output_config.format</c> is the whole mechanism, and forced tool
    /// use is deliberately not used.
    /// </summary>
    internal static JsonObject Body(ReceiptReadRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxOutputTokens,
            ["system"] = ReceiptReadPrompt.System,
            ["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = ReceiptReadPrompt.SchemaName,
                    ["schema"] = ReceiptReadPrompt.Schema(),
                },
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        FileBlock(request.File),
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = ReceiptReadPrompt.Catalogue(request.Products, request.Suppliers),
                        },
                    },
                },
            },
        };
    }

    /// <summary>
    /// The receipt as a content block: an image block for a photograph, a document block for a PDF,
    /// with the media type the server derived from the extension rather than the one the client
    /// claimed.
    /// </summary>
    private static JsonObject FileBlock(ReceiptScanFile file) => new()
    {
        ["type"] = file.Kind is ReceiptScanMediaKind.Pdf ? "document" : "image",
        ["source"] = new JsonObject
        {
            ["type"] = "base64",
            ["media_type"] = file.MediaType,
            ["data"] = Convert.ToBase64String(file.Content),
        },
    };

    /// <summary>
    /// The answer's text and what the call cost. A response with no text block is not a completion
    /// this adapter can use, and is reported as an unusable reader rather than as a receipt nobody
    /// could read - the two are different failures and the operator is told different things.
    /// </summary>
    private static ReceiptReadResult Read(JsonNode? payload)
    {
        if (payload is not JsonObject root)
        {
            throw new ReceiptReaderUnavailableException(ReceiptReaderUnavailableMessage);
        }

        var text = root["content"] is JsonArray content
            ? string.Concat(content
                .OfType<JsonObject>()
                .Where(block => block["type"]?.GetValue<string>() == "text")
                .Select(block => block["text"]?.GetValue<string>() ?? string.Empty))
            : string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ReceiptReaderUnavailableException(ReceiptReaderUnavailableMessage);
        }

        var usage = root["usage"] as JsonObject;

        return new ReceiptReadResult(text, Tokens(usage, "input_tokens"), Tokens(usage, "output_tokens"));
    }

    private static int? Tokens(JsonObject? usage, string name) =>
        usage?[name] is JsonValue value && value.TryGetValue<int>(out var tokens) ? tokens : null;
}
