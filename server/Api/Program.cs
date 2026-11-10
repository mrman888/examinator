using System.Net;
using System.Threading.RateLimiting;
using Azure.Core;
using Azure.Identity;
using Examinator.Api;
using Examinator.Api.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32_768);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = $"https://login.microsoftonline.com/{builder.Configuration["Azure:TenantId"]}/v2.0";
    options.Audience = builder.Configuration["Azure:ApiClientId"];
    options.MapInboundClaims = false;
});
builder.Services.AddAuthorization(options => options.AddPolicy("WebsiteGateway", policy =>
{
    policy.RequireAuthenticatedUser();
    policy.RequireClaim("tid", builder.Configuration["Azure:TenantId"] ?? "");
    policy.RequireClaim("oid", builder.Configuration["Azure:GatewayPrincipalId"] ?? "");
    policy.RequireClaim("roles", "Api.Invoke");
}));
builder.Services.AddSingleton<TokenCredential>(_ => builder.Environment.IsDevelopment()
    ? new AzureCliCredential()
    : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(
        builder.Configuration["Azure:ManagedIdentityClientId"]
        ?? throw new InvalidOperationException("Configure the API managed identity client ID."))));
builder.Services.AddHttpClient<IFoundryClient, FoundryClient>(client => client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddConcurrencyLimiter("Inference", limiter =>
    {
        limiter.PermitLimit = 1;
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});
var app = builder.Build();
var localAnonymous = app.Environment.IsDevelopment() &&
    app.Configuration.GetValue<bool>("Development:AllowAnonymous");
if (!localAnonymous && (!Guid.TryParse(app.Configuration["Azure:TenantId"], out _) ||
    !Guid.TryParse(app.Configuration["Azure:ApiClientId"], out _) ||
    !Guid.TryParse(app.Configuration["Azure:GatewayPrincipalId"], out _)))
{
    throw new InvalidOperationException("Configure Azure TenantId, ApiClientId and GatewayPrincipalId.");
}
app.UseExceptionHandler();
app.UseStatusCodePages();
if (localAnonymous)
{
    app.Use(async (context, next) =>
    {
        if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    });
}
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/healthz", () => TypedResults.Ok(new { status = "ok" })).AllowAnonymous();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapTest(localAnonymous);
app.Run();

namespace Examinator.Api
{
    public sealed class ApiAssemblyMarker;
}
