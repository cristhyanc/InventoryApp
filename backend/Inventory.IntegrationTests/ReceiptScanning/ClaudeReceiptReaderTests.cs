using System.Net;
using System.Text;
using System.Text.Json;
using Inventory.Application.ReceiptScanning;
using Inventory.Domain.ReceiptScanning;
using Inventory.Infrastructure.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.ReceiptScanning;

/// <summary>
/// The reader adapter's request and response handling (issue #1163), against a fake HTTP handler -
/// never a live call, which issue #1163 excludes.
///
/// <para><strong>The request shape is the assertion.</strong> The model comes from configuration, the
/// output schema is present, there is no <c>tools</c> member at all, and the only business data in the
/// body is this business's product and supplier names. That is the criterion "the adapter sends only
/// this business's products and suppliers, verified by a test on the built request".</para>
/// </summary>
public class ClaudeReceiptReaderTests
{
    private const string ApiKey = "fake-key-not-a-real-credential";

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("not a real photograph");

    /// <summary>
    /// The request carries the configured model, the schema, no tools, and the file as a base64
    /// image block with the media type the server derived.
    /// </summary>
    [Fact]
    public async Task The_request_carries_the_configured_model_the_schema_and_no_tools()
    {
        var handler = new RecordingHandler(Completion("{}"));
        await Reader(handler, model: "a-configured-model").ReadAsync(Request(), CancellationToken.None);

        var body = handler.Body;
        Assert.Equal("a-configured-model", body.GetProperty("model").GetString());
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.False(body.TryGetProperty("tool_choice", out _));

        var format = body.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        // Exactly `type` and `schema`: the Messages API rejects any other member of
        // output_config.format (a `name` was answered 400 "Extra inputs are not permitted").
        Assert.Equal(
            new[] { "schema", "type" },
            format.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());

        var file = body.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("image", file.GetProperty("type").GetString());
        Assert.Equal("base64", file.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/jpeg", file.GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(
            Convert.ToBase64String(Content),
            file.GetProperty("source").GetProperty("data").GetString());
    }

    /// <summary>
    /// The schema publishes exactly the declared vocabularies, so the shape the model is asked for
    /// and the shape the parser accepts cannot drift apart.
    /// </summary>
    [Fact]
    public async Task The_schema_publishes_the_declared_vocabularies()
    {
        var handler = new RecordingHandler(Completion("{}"));
        await Reader(handler).ReadAsync(Request(), CancellationToken.None);

        var lineSchema = handler.Body
            .GetProperty("output_config").GetProperty("format").GetProperty("schema")
            .GetProperty("properties").GetProperty("lines").GetProperty("items");
        var line = lineSchema.GetProperty("properties");

        Assert.Equal(
            ReceiptLineKinds.All.Select(ReceiptLineKinds.Name),
            line.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(token => token.GetString()!));
        Assert.Equal(
            ReceiptPackSizes.All.Select(ReceiptPackSizes.Name),
            line.GetProperty("packSize").GetProperty("enum").EnumerateArray().Select(token => token.GetString()!));
        Assert.Equal(
            ReceiptGstAnswers.All,
            line.GetProperty("priceIncludesGst").GetProperty("enum").EnumerateArray().Select(token => token.GetString()!));

        // Values the receipt may not print are nullable, not optional: an optional property makes
        // the structured-output grammar far larger, and this schema was refused as "Schema is too
        // complex" while most of its properties were optional.
        Assert.Equal("null", line.GetProperty("productId").GetProperty("type")[1].GetString());
    }

    /// <summary>
    /// Every object in the schema requires every one of its properties. The Messages API answered
    /// 400 "Schema is too complex" (after more than two minutes) while most were optional.
    /// </summary>
    [Fact]
    public async Task Every_schema_object_requires_every_property()
    {
        var handler = new RecordingHandler(Completion("{}"));
        await Reader(handler).ReadAsync(Request(), CancellationToken.None);

        var schema = handler.Body.GetProperty("output_config").GetProperty("format").GetProperty("schema");
        var line = schema.GetProperty("properties").GetProperty("lines").GetProperty("items");

        foreach (var objectSchema in new[] { schema, line })
        {
            Assert.Equal(
                objectSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal),
                objectSchema.GetProperty("required").EnumerateArray().Select(token => token.GetString()!).Order(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// A PDF is sent as a document block rather than an image block, with its own media type.
    /// </summary>
    [Fact]
    public async Task A_pdf_is_sent_as_a_document_block()
    {
        var handler = new RecordingHandler(Completion("{}"));
        await Reader(handler).ReadAsync(
            Request(new ReceiptScanFile("application/pdf", ReceiptScanMediaKind.Pdf, Content, 2)),
            CancellationToken.None);

        var file = handler.Body.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("document", file.GetProperty("type").GetString());
        Assert.Equal("application/pdf", file.GetProperty("source").GetProperty("media_type").GetString());
    }

    /// <summary>
    /// The one thing about the business in the request is its product and supplier names, with their
    /// ids. The criterion is asserted on the built body, not on the port's input: an adapter that
    /// quietly added a cost or a stock level would fail here.
    /// </summary>
    [Fact]
    public async Task Only_the_product_and_supplier_names_are_sent()
    {
        var handler = new RecordingHandler(Completion("{}"));
        await Reader(handler).ReadAsync(Request(), CancellationToken.None);

        var text = handler.Body.GetProperty("messages")[0].GetProperty("content")[1]
            .GetProperty("text").GetString()!;

        Assert.Contains("91: Cola 375mL", text, StringComparison.Ordinal);
        Assert.Contains("7: Metro Drinks Wholesale", text, StringComparison.Ordinal);

        // Nothing else about either row travels: the port carries two columns each, so there is
        // nothing here for a cost, a stock level or a contact address to come from.
        var whole = handler.RawBody!;
        Assert.DoesNotContain("averageUnitCost", whole, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quantityInStock", whole, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The key travels in its header, on the one request, and the API version is pinned.</summary>
    [Fact]
    public async Task The_key_and_the_api_version_travel_in_their_headers()
    {
        var handler = new RecordingHandler(Completion("{}"));
        await Reader(handler).ReadAsync(Request(), CancellationToken.None);

        Assert.Equal(ApiKey, handler.Headers![ClaudeReceiptReader.ApiKeyHeader]);
        Assert.Equal(ClaudeReceiptReader.Version, handler.Headers[ClaudeReceiptReader.VersionHeader]);
        Assert.Equal(1, handler.Requests);
    }

    /// <summary>
    /// The answer's text block is what the parser is given, unchanged, and the token counts are read
    /// for the log line.
    /// </summary>
    [Fact]
    public async Task The_answers_text_and_token_counts_are_read()
    {
        var json = "{\"lines\":[]}";
        var handler = new RecordingHandler(Completion(json, inputTokens: 1234, outputTokens: 56));

        var result = await Reader(handler).ReadAsync(Request(), CancellationToken.None);

        Assert.Equal(json, result.Json);
        Assert.Equal(1234, result.InputTokens);
        Assert.Equal(56, result.OutputTokens);
    }

    /// <summary>Several text blocks are one answer, concatenated in order.</summary>
    [Fact]
    public async Task Several_text_blocks_are_one_answer()
    {
        var handler = new RecordingHandler(Response(
            """
            { "content": [ { "type": "text", "text": "{\"lines\":" },
                           { "type": "text", "text": "[]}" } ] }
            """));

        Assert.Equal("{\"lines\":[]}", (await Reader(handler).ReadAsync(Request(), CancellationToken.None)).Json);
    }

    /// <summary>
    /// A refusal from upstream is reported as an unusable reader with the status code and
    /// <strong>nothing from the body</strong>: an upstream error body can quote the request, and the
    /// request held a receipt.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refusal_is_an_unusable_reader_and_quotes_nothing_from_the_body(HttpStatusCode status)
    {
        var handler = new RecordingHandler(new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"error\":{\"message\":\"COLA 375ML X 24 at 30.00\"}}"),
        });

        var exception = await Assert.ThrowsAsync<ReceiptReaderUnavailableException>(
            () => Reader(handler).ReadAsync(Request(), CancellationToken.None));

        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("COLA", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Requests);
    }

    /// <summary>
    /// A transport failure is an unusable reader, and still exactly one call: there is no resilience
    /// handler on this client, deliberately, because a retried scan is a second payment.
    /// </summary>
    [Fact]
    public async Task A_transport_failure_is_an_unusable_reader_and_is_not_retried()
    {
        var handler = new RecordingHandler(() => throw new HttpRequestException("no route to host"));

        await Assert.ThrowsAsync<ReceiptReaderUnavailableException>(
            () => Reader(handler).ReadAsync(Request(), CancellationToken.None));

        Assert.Equal(1, handler.Requests);
    }

    /// <summary>
    /// The client's own timeout - which is the configured one - is a timeout rather than an unusable
    /// reader, because the operator is told something different about it.
    /// </summary>
    [Fact]
    public async Task The_configured_timeout_is_reported_as_a_timeout()
    {
        var handler = new RecordingHandler(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Completion("{}");
        });

        await Assert.ThrowsAsync<ReceiptReaderTimeoutException>(
            () => Reader(handler, timeout: TimeSpan.FromMilliseconds(50))
                .ReadAsync(Request(), CancellationToken.None));
    }

    /// <summary>
    /// An answer with no text block is not a completion this adapter can use, and is reported as an
    /// unusable reader rather than as a receipt nobody could read - two different failures, and the
    /// operator is told different things.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"content\": []}")]
    [InlineData("{\"content\": [{\"type\": \"thinking\", \"thinking\": \"hmm\"}]}")]
    [InlineData("[]")]
    public async Task An_answer_with_no_text_block_is_an_unusable_reader(string json) =>
        await Assert.ThrowsAsync<ReceiptReaderUnavailableException>(
            () => Reader(new RecordingHandler(Response(json))).ReadAsync(Request(), CancellationToken.None));

    /// <summary>
    /// With no key configured the adapter makes no call at all. The use case refuses first, so this
    /// is the second line: an unconfigured reader must never produce an unauthenticated request.
    /// </summary>
    [Fact]
    public async Task With_no_key_configured_no_request_is_made()
    {
        var handler = new RecordingHandler(Completion("{}"));

        await Assert.ThrowsAsync<ReceiptReaderUnavailableException>(
            () => Reader(handler, apiKey: null).ReadAsync(Request(), CancellationToken.None));

        Assert.Equal(0, handler.Requests);
    }

    private static ClaudeReceiptReader Reader(
        RecordingHandler handler,
        string? apiKey = ApiKey,
        string model = "claude-sonnet-5-5",
        TimeSpan? timeout = null)
    {
        var configuration = ReceiptScanningConfiguration.From(new ReceiptScanningOptions
        {
            ApiKey = apiKey,
            Model = model,
            TimeoutSeconds = 60,
        });

        var http = new HttpClient(handler)
        {
            BaseAddress = configuration.BaseAddress,
            Timeout = timeout ?? configuration.Timeout,
        };

        return new ClaudeReceiptReader(http, configuration);
    }

    private static ReceiptReadRequest Request(ReceiptScanFile? file = null) =>
        new(
            file ?? new ReceiptScanFile("image/jpeg", ReceiptScanMediaKind.Image, Content, null),
            [new ReceiptScanProduct(91, "Cola 375mL")],
            [new ReceiptScanSupplier(7, "Metro Drinks Wholesale")]);

    private static HttpResponseMessage Completion(
        string json,
        int inputTokens = 1200,
        int outputTokens = 400) =>
        Response(JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = json } },
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
        }));

    private static HttpResponseMessage Response(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// The transport, substituted: it records the request it was asked to send and answers whatever
    /// the test set, so nothing in this file reaches a network.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _answer;

        public RecordingHandler(HttpResponseMessage response)
            : this(_ => Task.FromResult(response))
        {
        }

        public RecordingHandler(Func<HttpResponseMessage> answer)
            : this(_ => Task.FromResult(answer()))
        {
        }

        public RecordingHandler(Func<CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        /// <summary>How many requests the adapter asked to send.</summary>
        public int Requests { get; private set; }

        /// <summary>The last request body as text.</summary>
        public string? RawBody { get; private set; }

        /// <summary>The last request body, parsed. Reading it before a request is a test's own error.</summary>
        public JsonElement Body => JsonDocument.Parse(
            RawBody ?? throw new InvalidOperationException("No request was sent.")).RootElement;

        /// <summary>The last request's headers.</summary>
        public Dictionary<string, string>? Headers { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Requests++;
            RawBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(',', header.Value),
                StringComparer.OrdinalIgnoreCase);

            return await _answer(cancellationToken);
        }
    }
}
