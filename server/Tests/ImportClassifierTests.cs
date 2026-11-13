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
