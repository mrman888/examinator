using Microsoft.AspNetCore.Http.HttpResults;

namespace Examinator.Api;

/// <summary>A short prompt for testing the Foundry connection.</summary>
public sealed record TestPromptRequest(string? Prompt);

/// <summary>The model's plain-text reply.</summary>
public sealed record TestPromptResponse(string Text);

public static class TestEndpoints
{
    public static void MapTest(this WebApplication app, bool localAnonymous)
    {
        var endpoint = app.MapPost("/api/test", async Task<Results<Ok<TestPromptResponse>, ValidationProblem>> (
            TestPromptRequest request, IFoundryClient foundry, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 2000)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["prompt"] = ["Enter between 1 and 2000 characters."]
                });
            }
            var text = await foundry.ReplyAsync(request.Prompt.Trim(), cancellationToken);
            return TypedResults.Ok(new TestPromptResponse(text));
        })
            .WithName("TestFoundry")
            .WithSummary("Send a short test prompt to Azure Foundry")
            .WithDescription("Returns a plain-text model reply. Does not import or store exams.")
            .RequireRateLimiting("Inference")
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(429)
            .ProducesProblem(502)
            .ProducesProblem(504);
        if (!localAnonymous) endpoint.RequireAuthorization("WebsiteGateway");
    }
}
