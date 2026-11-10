using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Mvc;

namespace Examinator.Web;

/// <summary>A short prompt submitted through the website.</summary>
public sealed record GatewayPromptRequest(string? Prompt);

/// <summary>A plain-text response from the private API.</summary>
public sealed record GatewayPromptResponse(string Text);

public interface IGatewayClient
{
    Task<IResult> SendAsync(GatewayPromptRequest prompt, CancellationToken cancellationToken);
}

public sealed class GatewayClient(HttpClient client, TokenCredential credential, GatewaySettings settings) : IGatewayClient
{
    public async Task<IResult> SendAsync(GatewayPromptRequest prompt, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.ApiBaseUrl, "/api/test"))
        {
            Content = JsonContent.Create(prompt)
        };
        if (!settings.LocalAnonymous)
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext([$"api://{settings.ApiClientId}/.default"]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            return TypedResults.Problem(statusCode: status is 400 or 429 or 504 ? status : 502,
                title: status switch
                {
                    429 => "Too many requests. Please wait a minute and try again.",
                    504 => "The model request timed out. Please try again.",
                    _ => "The private API could not complete the request."
                });
        }
        var result = await response.Content.ReadFromJsonAsync<GatewayPromptResponse>(cancellationToken);
        if (result is null || string.IsNullOrWhiteSpace(result.Text))
            throw new JsonException("The private API returned no text.");
        return TypedResults.Ok(result);
    }
}
