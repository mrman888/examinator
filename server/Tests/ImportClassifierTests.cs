using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Examinator.Api;
using Microsoft.Extensions.Configuration;

namespace Examinator.Tests;

public sealed class ImportClassifierTests
{
    private static readonly DraftQuestion Question = new("q1", 1, "Question", 1, 1, 1, 1, [], [], true, []);
    private static readonly TopicDefinition[] Topics = [new("biology-cells", "Cellen", "Celstructuur", [], [], [], null, null)];
    private static readonly ImportSourceBlock[] Blocks = [new("exam-block-1", 1, "A synthetic source")];

    [Fact]
    public async Task UsesStrictSchemaAndValidatesSources()
    {
        var result = new ClassificationResult("biology", "q1", 1,
            [new("biology-cells", "A reason", ["exam-block-1"])], false, []);
        var handler = new Handler(Response(result));
        using var http = new HttpClient(handler);
        var client = Model(http);
        var classified = await client.ClassifyAsync(Question, Topics, Blocks, [], CancellationToken.None);
        Assert.Equal("biology-cells", classified.Topics[0].TopicId);
        Assert.True(classified.NeedsReview);
        Assert.Contains(classified.Warnings, warning => warning.Contains("Visuele context"));
        Assert.Contains("\"strict\":true", handler.RequestBody);
        Assert.Contains("\"model\":\"approved-import-model\"", handler.RequestBody);
        Assert.Contains("json_schema", handler.RequestBody);
    }

    [Theory]
    [InlineData("unknown-topic", "exam-block-1")]
    [InlineData("biology-cells", "invented-block")]
    public async Task RejectsInventedReferences(string topic, string block)
    {
        var result = new ClassificationResult("biology", "q1", 1, [new(topic, "Reason", [block])], false, []);
        using var http = new HttpClient(new Handler(Response(result)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Model(http).ClassifyAsync(Question, Topics, Blocks, [], CancellationToken.None));
    }

    [Fact]
    public async Task EmptyTextNeverTriggersClassification()
    {
        var handler = new Handler("{}");
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Model(http).ClassifyAsync(Question, Topics,
            [new("exam-block-1", 1, "   ")], [], CancellationToken.None));
        Assert.Equal("", handler.RequestBody);
    }

    [Theory]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"content":"{}"}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"refusal":"No","content":null}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"{}"}}]}""")]
    public async Task RejectsIncompleteOrRefusedOutput(string response)
    {
        using var http = new HttpClient(new Handler(response));
        await Assert.ThrowsAsync<InvalidDataException>(() => Model(http).ClassifyAsync(Question, Topics, Blocks, [], CancellationToken.None));
    }

    [Fact]
    public async Task ImportModelSendsRoleLabelledImagesWithIndependentTopicInstructions()
    {
        var result = new ClassificationResult("biology", "q1", 1,
            [new("biology-cells", "Celstructuur is nodig.", ["exam-block-1"])], false, []);
        var handler = new Handler(Response(result));
        using var http = new HttpClient(handler);
        var model = Model(http);
        var classified = await model.ClassifyAsync(Question, Topics, Blocks,
            [new(1, "data:image/png;base64,c3ludGhldGlj", "exam"),
             new(1, "data:image/png;base64,c3ludGhldGlj", "answers")], default);
        Assert.Single(classified.Topics);
        using var request = JsonDocument.Parse(handler.RequestBody);
        var messages = request.RootElement.GetProperty("messages");
        Assert.Contains("Trefwoorden zijn hints", messages[0].GetProperty("content").GetString());
        Assert.Contains("Hoofdstukken/paragrafen zijn metadata", messages[0].GetProperty("content").GetString());
        var parts = messages[1].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(2, parts.Count(part => part.GetProperty("type").GetString() == "image_url"));
        Assert.Contains(parts, part => part.GetProperty("type").GetString() == "text" &&
            part.GetProperty("text").GetString()!.Contains("Document: answers"));
    }

    [Theory]
    [InlineData("physics", 2025)]
    [InlineData("biology", 2024)]
    public async Task ImportModelRejectsWrongDocumentIdentity(string subjectId, int year)
    {
        var inventory = new DocumentInventory(subjectId, "havo", year, 1, "HA-1021-a-25-1", "exam", 1,
            [new(1, "Celstructuur", 1, 1)]);
        using var http = new HttpClient(new Handler(Response(inventory)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Model(http).InventoryAsync(
            new("biology", "havo", 2025, 1, "", "", Topics),
            new([new(1, 200, 200, false, true, [])]), [], "exam", default));
    }

    [Fact]
    public async Task ImportModelAcceptsYearAndTimevakReadFromDocuments()
    {
        var inventory = new DocumentInventory("biology", "havo", 2025, 2, "HA-1021-a-25-2", "exam", 1,
            [new(1, "Celstructuur", 1, 1)]);
        using var http = new HttpClient(new Handler(Response(inventory)));
        var result = await Model(http).InventoryAsync(new("biology", "havo", 0, 0, "", "", Topics),
            new([new(1, 200, 200, false, true, [])]), [], "exam", default);
        Assert.Equal(2025, result.Year);
        Assert.Equal(2, result.Timevak);
    }

    [Fact]
    public async Task ImportModelRejectsIncompleteNumberInventory()
    {
        var inventory = new DocumentInventory("biology", "havo", 2025, 1, "HA-1021-a-25-1", "exam", 2,
            [new(1, "Celstructuur", 1, 1), new(3, "Andere vraag", 1, 1)]);
        using var http = new HttpClient(new Handler(Response(inventory)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Model(http).InventoryAsync(
            new("biology", "havo", 2025, 1, "", "", Topics),
            new([new(1, 200, 200, false, true, [])]), [], "exam", default));
    }

    [Theory]
    [InlineData("unknown-topic", "exam-block-1")]
    [InlineData("biology-cells", "invented-block")]
    public async Task ImportModelRejectsInventedReferences(string topic, string block)
    {
        var result = new ClassificationResult("biology", "q1", 1, [new(topic, "Reden", [block])], false, []);
        using var http = new HttpClient(new Handler(Response(result)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Model(http).ClassifyAsync(Question, Topics, Blocks, [], default));
    }

    [Fact]
    public async Task RetriesThrottledRequestsAfterRetryAfterDelay()
    {
        var result = new ClassificationResult("biology", "q1", 1,
            [new("biology-cells", "Celstructuur is nodig.", ["exam-block-1"])], false, []);
        var handler = new SequenceHandler(
            Throttled(TimeSpan.FromSeconds(7)), Throttled(null), Ok(Response(result)));
        using var http = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var model = new ImportModel(http, new Credential(), Configuration())
        {
            Delay = (delay, _) => { delays.Add(delay); return Task.CompletedTask; }
        };
        var classified = await model.ClassifyAsync(Question, Topics, Blocks, [], default);
        Assert.Single(classified.Topics);
        Assert.Equal(3, handler.Requests);
        Assert.Equal([TimeSpan.FromSeconds(7), ImportModel.DefaultRetryDelay], delays);
    }

    [Fact]
    public async Task ReportsTheThrottledWaitAsProgressAndResumesThePhase()
    {
        var result = new ClassificationResult("biology", "q1", 1,
            [new("biology-cells", "Celstructuur is nodig.", ["exam-block-1"])], false, []);
        using var http = new HttpClient(new SequenceHandler(Throttled(TimeSpan.FromSeconds(7)), Ok(Response(result))));
        var store = new MemoryImportStore();
        var reporter = new ImportProgressReporter(store, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportProgressReporter>.Instance);
        var id = Guid.NewGuid().ToString("N");
        await reporter.StartAsync(id, 1, 1, 64, default);
        await reporter.PhaseAsync("tagging", "Vraag 1 van 1: onderwerpen toekennen", default, 1, 1, "vraag");
        ImportProgress? during = null;
        var model = new ImportModel(http, new Credential(), Configuration(), reporter)
        {
            Delay = (_, _) => { during = store.Progress(id); return Task.CompletedTask; }
        };
        await model.ClassifyAsync(Question, Topics, Blocks, [], default);
        Assert.Equal("throttled", during!.Phase);
        Assert.Equal("Wachten op modelcapaciteit, nieuwe poging over 7 s", during.Message);
        var after = store.Progress(id)!;
        Assert.Equal("tagging", after.Phase);
        Assert.Equal("Vraag 1 van 1: onderwerpen toekennen", after.Message);
    }

    [Fact]
    public async Task ReportsPersistentThrottlingAsCapacityProblem()
    {
        var handler = new SequenceHandler(Enumerable.Range(0, ImportModel.MaximumRateLimitRetries + 1)
            .Select(_ => Throttled(TimeSpan.FromMinutes(10))).ToArray());
        using var http = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var model = new ImportModel(http, new Credential(), Configuration())
        {
            Delay = (delay, _) => { delays.Add(delay); return Task.CompletedTask; }
        };
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            model.ClassifyAsync(Question, Topics, Blocks, [], default));
        Assert.Contains("HTTP 429", exception.Message);
        Assert.Contains("capaciteit", exception.Message);
        Assert.Equal(ImportModel.MaximumRateLimitRetries + 1, handler.Requests);
        Assert.All(delays, delay => Assert.Equal(ImportModel.MaximumRetryDelay, delay));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReportsOtherProviderFailuresWithoutRetrying(HttpStatusCode status)
    {
        var handler = new SequenceHandler(new HttpResponseMessage(status), Ok("{}"));
        using var http = new HttpClient(handler);
        var model = new ImportModel(http, new Credential(), Configuration())
        {
            Delay = (_, _) => throw new InvalidOperationException("No retry expected.")
        };
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            model.ClassifyAsync(Question, Topics, Blocks, [], default));
        Assert.Contains($"HTTP {(int)status}", exception.Message);
        Assert.Equal(1, handler.Requests);
    }

    private static HttpResponseMessage Throttled(TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is { } delay) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        return response;
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses[Requests++]);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Foundry:Endpoint"] = "https://example.cognitiveservices.azure.com/",
        ["Foundry:ImportDeployment"] = "approved-import-model"
    }).Build();

    private static ImportModel Model(HttpClient http) => new(http, new Credential(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Foundry:Endpoint"] = "https://example.cognitiveservices.azure.com/",
            ["Foundry:ImportDeployment"] = "approved-import-model"
        }).Build());

    private static string Response(object result) => JsonSerializer.Serialize(new
    {
        choices = new[]
        {
            new
            {
                finish_reason = "stop",
                message = new { content = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }
            }
        }
    });

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class Handler(string response) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
