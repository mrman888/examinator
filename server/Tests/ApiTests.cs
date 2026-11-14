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

    [Fact]
    public async Task ImportRoutesRequireGatewayAndFailClosed()
    {
        await using var app = new ApiFactory();
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/subjects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/catalog")).StatusCode);
        using var client = app.AuthenticatedClient();
        Assert.Contains("biology", await client.GetStringAsync("/api/subjects"));
        foreach (var path in new[] { "/api/catalog?subjectId=biology", "/api/imports", "/api/imports/events", "/api/imports/id", "/api/documents/id" })
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(path)).StatusCode);
        foreach (var path in new[] { "/api/imports", "/api/imports/id/confirm-definitions",
            "/api/imports/id/publish", "/api/imports/id/cancel", "/api/imports/id/retry", "/api/imports/id/reprocess" })
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PatchAsync("/api/imports/id/draft", JsonContent.Create(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.DeleteAsync("/api/imports/id")).StatusCode);
        Assert.Equal(0, app.Foundry.Calls);
    }

    [Fact]
    public async Task DeleteEndpointRequiresGatewayRemovesTheImportAndReportsConflicts()
    {
        await using var app = new ApiFactory(imports: true);
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/imports/id")).StatusCode);
        using var client = app.AuthenticatedClient();
        using var content = new MultipartFormDataContent();
        var form = ImportProcessorTests.Form();
        content.Add(new StringContent(form["metadata"].ToString()), "metadata");
        foreach (var file in form.Files)
        {
            using var bytes = new MemoryStream();
            await file.CopyToAsync(bytes);
            content.Add(new ByteArrayContent(bytes.ToArray()), file.Name, file.FileName);
        }
        using var uploaded = await client.PostAsync("/api/imports", content);
        var record = (await uploaded.Content.ReadFromJsonAsync<ImportRecord>())!;

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/imports/not-an-id")).StatusCode);
        using var deleted = await client.DeleteAsync($"/api/imports/{record.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/imports/{record.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/imports/{record.Id}")).StatusCode);
        Assert.Equal(0, app.Foundry.Calls);
    }

    [Fact]
    public async Task EnabledImportUploadAndProtectedRangeDeliveryUseRealEndpoints()
    {
        await using var app = new ApiFactory(imports: true);
        using var client = app.AuthenticatedClient();
        using var content = new MultipartFormDataContent();
        var form = ImportProcessorTests.Form();
        content.Add(new StringContent(form["metadata"].ToString()), "metadata");
        foreach (var file in form.Files)
        {
            using var bytes = new MemoryStream();
            await file.CopyToAsync(bytes);
            await bytes.WriteAsync(new byte[40_000]);
            content.Add(new ByteArrayContent(bytes.ToArray()), file.Name, file.FileName);
        }
        using var uploaded = await client.PostAsync("/api/imports", content);
        Assert.Equal(HttpStatusCode.Accepted, uploaded.StatusCode);
        var record = (await uploaded.Content.ReadFromJsonAsync<ImportRecord>())!;
        Assert.Equal($"/api/imports/{record.Id}", uploaded.Headers.Location!.OriginalString);
        Assert.Equal("queued", record.State);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{record.ExamDocumentId}");
        request.Headers.Range = new RangeHeaderValue(0, 4);
        using var document = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, document.StatusCode);
        Assert.Equal("%PDF-", await document.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/documents/not-an-id")).StatusCode);
        using var draft = await client.PatchAsJsonAsync($"/api/imports/{record.Id}/draft", new
        {
            revision = record.Revision, topics = record.Topics, questions = Array.Empty<DraftQuestion>(),
            padding = new string('x', 300_000)
        });
        Assert.Equal(HttpStatusCode.Conflict, draft.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/imports/{record.Id}/publish", new RevisionUpdate(record.Revision))).StatusCode);
        Assert.Equal(0, app.Foundry.Calls);
    }

    [Fact]
    public async Task ProgressEventsStreamRequiresGatewayAndStartsWithTheRetryFrame()
    {
        await using var app = new ApiFactory(imports: true);
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/imports/events")).StatusCode);
        using var client = app.AuthenticatedClient();
        using var response = await client.GetAsync("/api/imports/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-transform", response.Headers.CacheControl?.ToString());
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("retry: 3000", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal(0, app.Foundry.Calls);
    }

    [Fact]
    public async Task ProgressEventsStreamsAreCappedPerReplica()
    {
        await using var app = new ApiFactory(imports: true);
        using var client = app.AuthenticatedClient();
        var open = new List<HttpResponseMessage>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var response = await client.GetAsync("/api/imports/events", HttpCompletionOption.ResponseHeadersRead);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                open.Add(response);
            }
            using var rejected = await client.GetAsync("/api/imports/events", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }
        finally { open.ForEach(response => response.Dispose()); }
    }

    private sealed class ApiFactory(bool imports = false) : WebApplicationFactory<ApiAssemblyMarker>
    {
        public const string TenantId = "11111111-1111-1111-1111-111111111111";
        public const string ClientId = "22222222-2222-2222-2222-222222222222";
        public const string GatewayId = "33333333-3333-3333-3333-333333333333";
        private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = "test-key" };
        public FakeFoundry Foundry { get; } = new();
        private readonly MemoryImportStore store = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Azure:TenantId"] = TenantId,
                    ["Azure:ApiClientId"] = ClientId,
                    ["Azure:GatewayPrincipalId"] = GatewayId,
                    ["Development:AllowAnonymous"] = "true",
                    ["Import:Enabled"] = imports.ToString(),
                    ["Storage:BlobEndpoint"] = "https://blob.example/",
                    ["Storage:QueueEndpoint"] = "https://queue.example/",
                    ["Storage:TableEndpoint"] = "https://table.example/",
                    ["Foundry:ImportDeployment"] = "synthetic"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFoundryClient>();
                services.AddSingleton<IFoundryClient>(Foundry);
                if (imports)
                {
                    services.RemoveAll<IImportPersistence>();
                    services.AddSingleton<IImportPersistence>(store);
                    services.AddSingleton<IPdfDownloader, NoDownloader>();
                    services.AddScoped<IExamImports, ExamImports>();
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(new ImportProgressStreamOptions());
                    services.AddSingleton<ImportProgressStreamGate>();
                    services.AddTransient<ImportProgressStream>();
                }
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
