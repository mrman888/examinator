using System.Net;
using System.Net.Http.Headers;
using Azure.Core;
using Examinator.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Examinator.Tests;

public sealed class ImportGatewayTests
{
    [Fact]
    public async Task ProxiesPdfRangeWithServerTokenAndNoBrowserIdentityHeaders()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var gateway = new ImportGateway(http, new Credential(), Settings());
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/documents/abc";
        context.Request.Headers.Range = "bytes=0-3";
        context.Request.Headers.Cookie = "sensitive=browser-cookie";
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = "browser-value";
        context.Response.Body = new MemoryStream();

        await gateway.ForwardAsync(context, CancellationToken.None);
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 0-3/10", context.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes=0-3", handler.Range);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.False(handler.HasCookie);
        Assert.False(handler.HasPrincipal);
        Assert.Equal(4, context.Response.Body.Length);
    }

    private static GatewaySettings Settings() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Azure:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["Azure:ApiClientId"] = "22222222-2222-2222-2222-222222222222",
            ["Gateway:AllowedUserIds:0"] = "33333333-3333-3333-3333-333333333333",
            ["Gateway:PublicOrigin"] = "https://website.example",
            ["Gateway:ApiBaseUrl"] = "https://internal.example"
        }).Build(), false);

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Range { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public bool HasCookie { get; private set; }
        public bool HasPrincipal { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Range = request.Headers.Range?.ToString();
            Authorization = request.Headers.Authorization;
            HasCookie = request.Headers.Contains("Cookie");
            HasPrincipal = request.Headers.Contains("X-MS-CLIENT-PRINCIPAL");
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 10);
            return Task.FromResult(response);
        }
    }
}
