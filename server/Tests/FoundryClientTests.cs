using System.Net;
using System.Text;
using Azure.Core;
using Examinator.Api;
using Microsoft.Extensions.Configuration;

namespace Examinator.Tests;

public sealed class FoundryClientTests
{
    [Fact]
    public async Task UsesEntraTokenAndConfiguredDeployment()
    {
        var credential = new TestCredential();
        var handler = new ReplyHandler("""{"choices":[{"message":{"content":"A short answer"}}]}""");
        using var http = new HttpClient(handler);
        var client = CreateClient(http, credential);
        Assert.Equal("A short answer", await client.ReplyAsync("Question", CancellationToken.None));
        Assert.Equal("https://cognitiveservices.azure.com/.default", credential.Scope);
        Assert.Equal("https://example.cognitiveservices.azure.com/openai/v1/chat/completions", handler.Url);
        Assert.Equal("Bearer", handler.Scheme);
        Assert.Contains("\"model\":\"exam-classifier\"", handler.Body);
        Assert.Contains("\"content\":\"Question\"", handler.Body);
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":null}""")]
    [InlineData("""{"choices":[{"message":{"content":""}}]}""")]
    [InlineData("""{"choices":[{"message":null}]}""")]
    [InlineData("""[]""")]
    public async Task MissingReplyIsAnError(string body)
    {
        using var http = new HttpClient(new ReplyHandler(body));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateClient(http, new TestCredential()).ReplyAsync("Question", CancellationToken.None));
    }

    private static FoundryClient CreateClient(HttpClient http, TokenCredential credential) => new(
        http, credential, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Foundry:Endpoint"] = "https://example.cognitiveservices.azure.com/",
            ["Foundry:Deployment"] = "exam-classifier"
        }).Build());

    private sealed class TestCredential : TokenCredential
    {
        public string? Scope { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scope = requestContext.Scopes.Single();
            return new AccessToken("synthetic-test-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class ReplyHandler(string reply) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Url { get; private set; }
        public string? Scheme { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Url = request.RequestUri!.ToString();
            Scheme = request.Headers.Authorization?.Scheme;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json")
            };
        }
    }
}
