using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;

namespace Examinator.Api;

public interface IFoundryClient
{
    Task<string> ReplyAsync(string prompt, CancellationToken cancellationToken);
}

public sealed class FoundryClient : IFoundryClient
{
    private readonly HttpClient client;
    private readonly TokenCredential credential;
    private readonly Uri completionUri;
    private readonly string deployment;

    public FoundryClient(HttpClient client, TokenCredential credential, IConfiguration configuration)
    {
        this.client = client;
        this.credential = credential;
        var endpoint = configuration["Foundry:Endpoint"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidOperationException("Configure a valid HTTPS Foundry:Endpoint.");
        completionUri = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/openai/v1/chat/completions");
        deployment = configuration["Foundry:Deployment"]
            ?? throw new InvalidOperationException("Configure Foundry:Deployment.");
    }

    public async Task<string> ReplyAsync(string prompt, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, completionUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = JsonContent.Create(new
        {
            model = deployment,
            messages = new[]
            {
                new { role = "system", content = "You are a helpful educational assistant. Reply concisely in the user's language." },
                new { role = "user", content = prompt }
            },
            max_completion_tokens = 2048,
            reasoning_effort = "low"
        });
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (body.RootElement.ValueKind != JsonValueKind.Object ||
            !body.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            choices[0].ValueKind != JsonValueKind.Object ||
            !choices[0].TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
        {
            throw new InvalidDataException("Foundry returned no text reply.");
        }
        return content.GetString()!;
    }
}
