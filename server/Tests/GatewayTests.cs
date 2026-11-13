using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Examinator.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Examinator.Tests;

public sealed class GatewayTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string User = "22222222-2222-2222-2222-222222222222";

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("e30=")]
    public void InvalidIdentityIsDenied(string principal) =>
        Assert.False(EasyAuthPrincipal.IsAllowed(principal, Tenant, [User]));

    [Fact]
    public void OnlyApprovedUserInCorrectTenantIsAllowed()
    {
        Assert.True(EasyAuthPrincipal.IsAllowed(Principal(User), Tenant, [User]));
        Assert.False(EasyAuthPrincipal.IsAllowed(Principal("another-user"), Tenant, [User]));
        Assert.False(EasyAuthPrincipal.IsAllowed(Principal(User), "another-tenant", [User]));
    }

    [Fact]
    public void ProductionConfigurationFailsClosedWithoutAllowlist()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Azure:TenantId"] = Tenant,
                ["Azure:ApiClientId"] = "33333333-3333-3333-3333-333333333333",
                ["Gateway:PublicOrigin"] = "https://website.example",
                ["Gateway:ApiBaseUrl"] = "https://internal.example"
            }).Build();
        Assert.Throws<InvalidOperationException>(() => new GatewaySettings(configuration, false));
    }

    [Fact]
    public void ProductionConfigurationRejectsHttpBackend()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Azure:TenantId"] = Tenant,
                ["Azure:ApiClientId"] = "33333333-3333-3333-3333-333333333333",
                ["Gateway:AllowedUserIds:0"] = User,
                ["Gateway:PublicOrigin"] = "https://website.example",
                ["Gateway:ApiBaseUrl"] = "http://internal.example"
            }).Build();
        Assert.Throws<InvalidOperationException>(() => new GatewaySettings(configuration, false));
    }

    [Fact]
    public async Task HealthAnonymousButGatewayRequiresApprovedIdentity()
    {
        await using var app = new GatewayFactory();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/test", new { prompt = "hello" })).StatusCode);
        Assert.Equal(0, app.Backend.Calls);
    }

    [Theory]
    [InlineData("https://malicious.example", "1")]
    [InlineData("https://website.example", "")]
    [InlineData("", "1")]
    public async Task RejectsCrossSiteOrMissingCsrfHeader(string origin, string header)
    {
        await using var app = new GatewayFactory();
        using var client = app.ApprovedClient();
        if (origin.Length > 0) client.DefaultRequestHeaders.Add("Origin", origin);
        if (header.Length > 0) client.DefaultRequestHeaders.Add("X-Examinator-Request", header);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/test", new { prompt = "hello" })).StatusCode);
        Assert.Equal(0, app.Backend.Calls);
    }

    [Fact]
    public async Task ApprovedSameOriginRequestGetsBackendResponse()
    {
        await using var app = new GatewayFactory();
        using var client = app.ApprovedClient();
        client.DefaultRequestHeaders.Add("Origin", "https://website.example");
        client.DefaultRequestHeaders.Add("X-Examinator-Request", "1");
        using var response = await client.PostAsJsonAsync("/api/test", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Gateway reply", (await response.Content.ReadFromJsonAsync<GatewayPromptResponse>())?.Text);
        Assert.Equal(1, app.Backend.Calls);
    }

    [Fact]
    public async Task GatewayRateLimitsBeforeMorePaidCalls()
    {
        await using var app = new GatewayFactory();
        using var client = app.ApprovedClient();
        client.DefaultRequestHeaders.Add("Origin", "https://website.example");
        client.DefaultRequestHeaders.Add("X-Examinator-Request", "1");
        for (var index = 0; index < 10; index++)
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsJsonAsync("/api/test", new { prompt = "hello" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync("/api/test", new { prompt = "hello" })).StatusCode);
        Assert.Equal(10, app.Backend.Calls);
    }

    [Fact]
    public async Task ImportRoutesFailClosedAndMutationsRequireCsrf()
    {
        await using var app = new GatewayFactory();
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync("/api/subjects")).StatusCode);
        using var client = app.ApprovedClient();
        Assert.Contains("biology", await client.GetStringAsync("/api/subjects"));
        foreach (var path in new[] { "/api/catalog?subjectId=biology", "/api/imports", "/api/imports/id", "/api/documents/id" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("unavailable", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        var actions = new[] { "/api/imports", "/api/imports/id/confirm-definitions",
            "/api/imports/id/publish", "/api/imports/id/cancel", "/api/imports/id/retry" };
        foreach (var path in actions)
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PatchAsync("/api/imports/id/draft", JsonContent.Create(new { }))).StatusCode);

        client.DefaultRequestHeaders.Add("Origin", "https://website.example");
        client.DefaultRequestHeaders.Add("X-Examinator-Request", "1");
        foreach (var path in actions)
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PatchAsync("/api/imports/id/draft", JsonContent.Create(new { }))).StatusCode);
        Assert.Equal(0, app.Backend.Calls);
    }

    private static string Principal(string user) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new
        {
            auth_typ = "aad",
            claims = new[] { new { typ = "tid", val = Tenant }, new { typ = "oid", val = user } }
        })));

    private sealed class GatewayFactory : WebApplicationFactory<WebAssemblyMarker>
    {
        public FakeBackend Backend { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Azure:TenantId"] = Tenant,
                    ["Azure:ApiClientId"] = "33333333-3333-3333-3333-333333333333",
                    ["Gateway:AllowedUserIds:0"] = User,
                    ["Gateway:PublicOrigin"] = "https://website.example",
                    ["Gateway:ApiBaseUrl"] = "https://internal.example",
                    ["Development:AllowAnonymous"] = "true"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGatewayClient>();
                services.AddSingleton<IGatewayClient>(Backend);
                services.RemoveAll<IImportGateway>();
                services.AddSingleton<IImportGateway, FakeImportGateway>();
            });
        }

        public HttpClient ApprovedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Principal(User));
            return client;
        }
    }

    private sealed class FakeBackend : IGatewayClient
    {
        public int Calls { get; private set; }
        public Task<IResult> SendAsync(GatewayPromptRequest prompt, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IResult>(TypedResults.Ok(new GatewayPromptResponse("Gateway reply")));
        }
    }

    private sealed class FakeImportGateway : IImportGateway
    {
        public async Task ForwardAsync(HttpContext context, CancellationToken cancellationToken)
        {
            if (context.Request.Path == "/api/subjects")
                await TypedResults.Ok(new[] { new { id = "biology", title = "Biologie" } }).ExecuteAsync(context);
            else
                await TypedResults.Problem(statusCode: 503, title: "Exam import is unavailable.").ExecuteAsync(context);
        }
    }
}
