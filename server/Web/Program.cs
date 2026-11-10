using System.Net;
using System.Threading.RateLimiting;
using Azure.Core;
using Azure.Identity;
using Examinator.Web;
using Examinator.Web.Middleware;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32_768);
builder.Services.AddSingleton(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var environment = services.GetRequiredService<IHostEnvironment>();
    return new GatewaySettings(configuration, environment.IsDevelopment() &&
        configuration.GetValue<bool>("Development:AllowAnonymous"));
});
builder.Services.AddSingleton<TokenCredential>(_ => builder.Environment.IsDevelopment()
    ? new AzureCliCredential()
    : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(
        builder.Configuration["Azure:ManagedIdentityClientId"]
        ?? throw new InvalidOperationException("Configure the website managed identity client ID."))));
builder.Services.AddHttpClient<IGatewayClient, GatewayClient>(client => client.Timeout = TimeSpan.FromSeconds(110));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GatewayExceptionHandler>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("Prompts", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});
var app = builder.Build();
var gateway = app.Services.GetRequiredService<GatewaySettings>();
var localAnonymous = gateway.LocalAnonymous;
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/healthz")
    {
        await next(context);
        return;
    }
    if (localAnonymous)
    {
        if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    else if (!EasyAuthPrincipal.IsAllowed(
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL"].ToString(), gateway.TenantId, gateway.AllowedUserIds))
    {
        context.Response.StatusCode = 403;
        return;
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseRateLimiter();
app.MapGet("/healthz", () => TypedResults.Ok(new { status = "ok" }));
app.MapPost("/api/test", async Task<IResult> (
    GatewayPromptRequest request, HttpContext context, IGatewayClient client, CancellationToken cancellationToken) =>
{
    // A custom header and exact origin check prevent cross-site cookie-authenticated POSTs.
    if (context.Request.Headers["X-Examinator-Request"] != "1" ||
        (!localAnonymous && context.Request.Headers.Origin != gateway.PublicOrigin))
        return TypedResults.Problem(statusCode: 403, title: "A same-origin website request is required.");
    if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 2000)
        return TypedResults.Problem(statusCode: 400, title: "Enter between 1 and 2000 characters.");
    return await client.SendAsync(request, cancellationToken);
}).RequireRateLimiting("Prompts");
app.Map("/api/{**path}", () => TypedResults.NotFound());
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

namespace Examinator.Web
{
    public sealed class WebAssemblyMarker;
}
