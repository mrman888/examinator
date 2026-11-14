using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Examinator.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public async Task ForwardsDeleteWithServerTokenAndWithoutBody()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var gateway = new ImportGateway(http, new Credential(), Settings());
        var context = new DefaultHttpContext();
        context.Request.Method = "DELETE";
        context.Request.Path = "/api/imports/abc";
        context.Request.Headers.Cookie = "sensitive=browser-cookie";
        context.Response.Body = new MemoryStream();

        await gateway.ForwardAsync(context, CancellationToken.None);
        Assert.Equal("DELETE", handler.Method);
        Assert.Equal("/api/imports/abc", handler.Path);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.False(handler.HasCookie);
        Assert.Equal("", handler.Body);
    }

    [Fact]
    public async Task ForwardsPdfUrlFieldsOfMultipartUploads()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var gateway = new ImportGateway(http, new Credential(), Settings());
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("https://www.alleexamens.nl/exam.pdf"), "examUrl");
        multipart.Add(new StringContent("https://www.alleexamens.nl/answers.pdf"), "answersUrl");
        var body = await multipart.ReadAsByteArrayAsync();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/imports";
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();

        await gateway.ForwardAsync(context, CancellationToken.None);
        Assert.Contains("name=examUrl", handler.Body);
        Assert.Contains("https://www.alleexamens.nl/exam.pdf", handler.Body);
        Assert.Contains("name=answersUrl", handler.Body);
        Assert.Contains("https://www.alleexamens.nl/answers.pdf", handler.Body);
    }

    [Fact]
    public async Task StreamsEventFramesBeforeTheUpstreamEndsAndCancelsUpstreamOnClientAbort()
    {
        var upstream = new Pipe();
        var source = new TrackingStream(upstream.Reader.AsStream());
        var handler = new EventsHandler(() => Events(source));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var gateway = new ImportEventsGateway(http, new Credential(), Settings(), NullLogger<ImportEventsGateway>.Instance);
        var context = EventsContext();
        context.Request.Headers["Last-Event-ID"] = "7";
        context.Request.Headers.Cookie = "sensitive=browser-cookie";
        var body = new FlushRecorder();
        context.Response.Body = body;
        using var abort = new CancellationTokenSource();

        var forwarding = gateway.ForwardAsync(context, abort.Token);
        await upstream.Writer.WriteAsync(Encoding.UTF8.GetBytes("event: ping\ndata: {\"serverTime\":\"x\"}\n\n"));
        await body.WaitForFlushed("event: ping", TimeSpan.FromSeconds(5));
        Assert.False(forwarding.IsCompleted);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Contains("no-transform", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("7", handler.LastEventId);
        Assert.Contains("text/event-stream", handler.Accept);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.False(handler.HasCookie);
        Assert.Equal(ImportEventsGateway.Path, handler.Path);

        abort.Cancel();
        await forwarding.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(source.Disposed);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "application/problem+json", 503)]
    [InlineData(HttpStatusCode.TooManyRequests, "application/problem+json", 429)]
    [InlineData(HttpStatusCode.OK, "application/json", 502)]
    public async Task NonEventStreamUpstreamBecomesAProblemResponse(HttpStatusCode status, string contentType, int expected)
    {
        var handler = new EventsHandler(() => new HttpResponseMessage(status)
        {
            Content = new StringContent("{}", Encoding.UTF8, contentType)
        });
        using var http = new HttpClient(handler);
        var gateway = new ImportEventsGateway(http, new Credential(), Settings(), NullLogger<ImportEventsGateway>.Instance);
        var context = EventsContext();
        context.Response.Body = new MemoryStream();
        await gateway.ForwardAsync(context, CancellationToken.None);
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.Contains("problem+json", context.Response.ContentType);
    }

    private static HttpResponseMessage Events(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        response.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache, no-transform");
        response.Headers.TryAddWithoutValidation("X-Accel-Buffering", "no");
        return response;
    }

    private static DefaultHttpContext EventsContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider()
        };
        context.Request.Method = "GET";
        context.Request.Path = ImportEventsGateway.Path;
        return context;
    }

    private sealed class EventsHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastEventId { get; private set; }
        public string Accept { get; private set; } = "";
        public string? Path { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public bool HasCookie { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastEventId = request.Headers.TryGetValues("Last-Event-ID", out var values) ? values.Single() : null;
            Accept = request.Headers.Accept.ToString();
            Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization;
            HasCookie = request.Headers.Contains("Cookie");
            return Task.FromResult(respond());
        }
    }

    private sealed class TrackingStream(Stream inner) : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Records only what was flushed, to prove frames are not held back in a buffer.</summary>
    private sealed class FlushRecorder : MemoryStream
    {
        private readonly Lock gate = new();
        private string flushed = "";
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            lock (gate) flushed = Encoding.UTF8.GetString(ToArray());
            return Task.CompletedTask;
        }
        public async Task WaitForFlushed(string text, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (gate) if (flushed.Contains(text, StringComparison.Ordinal)) return;
                await Task.Delay(5);
            }
            throw new TimeoutException($"'{text}' was not flushed.");
        }
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
        public string? Method { get; private set; }
        public string? Path { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public bool HasCookie { get; private set; }
        public bool HasPrincipal { get; private set; }
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(cancellationToken);
            Method = request.Method.Method;
            Path = request.RequestUri!.AbsolutePath;
            Range = request.Headers.Range?.ToString();
            Authorization = request.Headers.Authorization;
            HasCookie = request.Headers.Contains("Cookie");
            HasPrincipal = request.Headers.Contains("X-MS-CLIENT-PRINCIPAL");
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 10);
            return response;
        }
    }
}
