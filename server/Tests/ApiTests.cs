using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Examinator.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Examinator.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task HealthIsAnonymousButPromptsNeedAuthentication()
    {
        await using var app = new ApiFactory();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/test", new { prompt = "hello" })).StatusCode);
        Assert.Equal(0, app.Foundry.Calls);
    }

    [Theory]
    [InlineData("another-gateway", "Api.Invoke")]
    [InlineData(ApiFactory.GatewayId, "WrongRole")]
    public async Task RejectsWrongIdentityOrMissingRole(string oid, string role)
    {
        await using var app = new ApiFactory();
        using var client = app.AuthenticatedClient(oid, role);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/test", new { prompt = "hello" })).StatusCode);
        Assert.Equal(0, app.Foundry.Calls);
    }

    [Fact]
    public async Task RejectsWrongAudience()
    {
        await using var app = new ApiFactory();
        using var client = app.AuthenticatedClient(audience: "another-api");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/test", new { prompt = "hello" })).StatusCode);
    }

    [Fact]
    public async Task ValidGatewayGetsReply()
    {
        await using var app = new ApiFactory();
        using var client = app.AuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/test", new { prompt = " Explain cells " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Explain cells", app.Foundry.LastPrompt);
        Assert.Equal("Test reply", (await response.Content.ReadFromJsonAsync<TestPromptResponse>())?.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsEmptyPromptBeforeInference(string prompt)
    {
        await using var app = new ApiFactory();
        using var client = app.AuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/test", new { prompt })).StatusCode);
        Assert.Equal(0, app.Foundry.Calls);
    }

    [Fact]
    public async Task RejectsOversizePrompt()
    {
        await using var app = new ApiFactory();
        using var client = app.AuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/test", new { prompt = new string('x', 2001) })).StatusCode);
        Assert.Equal(0, app.Foundry.Calls);
    }

    [Fact]
    public async Task ProviderFailureIsExplicitAndDoesNotExposeContent()
    {
        await using var app = new ApiFactory();
        app.Foundry.Failure = new HttpRequestException("private provider content");
        using var client = app.AuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/test", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("private provider content", await response.Content.ReadAsStringAsync());
    }

    private sealed class ApiFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        public const string TenantId = "11111111-1111-1111-1111-111111111111";
        public const string ClientId = "22222222-2222-2222-2222-222222222222";
        public const string GatewayId = "33333333-3333-3333-3333-333333333333";
        private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = "test-key" };
        public FakeFoundry Foundry { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Azure:TenantId"] = TenantId,
                    ["Azure:ApiClientId"] = ClientId,
                    ["Azure:GatewayPrincipalId"] = GatewayId,
                    ["Development:AllowAnonymous"] = "true"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFoundryClient>();
                services.AddSingleton<IFoundryClient>(Foundry);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var config = new OpenIdConnectConfiguration
                    {
                        Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0"
                    };
                    config.SigningKeys.Add(key);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                });
            });
        }

        public HttpClient AuthenticatedClient(string oid = GatewayId, string role = "Api.Invoke", string audience = ClientId)
        {
            var client = CreateClient();
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0",
                Audience = audience,
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
                Claims = new Dictionary<string, object> { ["tid"] = TenantId, ["oid"] = oid, ["roles"] = new[] { role } }
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
    }

    private sealed class FakeFoundry : IFoundryClient
    {
        public int Calls { get; private set; }
        public string? LastPrompt { get; private set; }
        public Exception? Failure { get; set; }
        public Task<string> ReplyAsync(string prompt, CancellationToken cancellationToken)
        {
            Calls++;
            LastPrompt = prompt;
            return Failure is null ? Task.FromResult("Test reply") : Task.FromException<string>(Failure);
        }
    }
}
