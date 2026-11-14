using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Examinator.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Examinator.Tests;

public sealed class ImportUrlTests
{
    [Theory]
    [InlineData("http://www.alleexamens.nl/a.pdf")]
    [InlineData("ftp://www.alleexamens.nl/a.pdf")]
    [InlineData("file:///c:/a.pdf")]
    [InlineData("https://user:pass@www.alleexamens.nl/a.pdf")]
    [InlineData("https://www.alleexamens.nl:8443/a.pdf")]
    [InlineData("/relative/a.pdf")]
    [InlineData("")]
    [InlineData("not a url")]
    public void RejectsUnsafeOrInvalidUrls(string url) =>
        Assert.Equal(400, Assert.Throws<ImportProblem>(() => ImportUrlPolicy.Parse(url)).Status);

    [Fact]
    public void AcceptsPlainHttpsUrlWithDefaultPort() =>
        Assert.Equal("www.alleexamens.nl", ImportUrlPolicy.Parse("https://www.alleexamens.nl:443/a.pdf").Host);

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("93.184.216.35", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    public void ClassifiesAddresses(string address, bool expected) =>
        Assert.Equal(expected, ImportUrlPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task ResolutionFailsWhenAnyAddressIsNotPublic()
    {
        var problem = await Assert.ThrowsAsync<ImportProblem>(() => ImportUrlPolicy.ResolvePublicAsync("rebind.example",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.35"), IPAddress.Parse("169.254.169.254") }),
            CancellationToken.None));
        Assert.Equal(400, problem.Status);
        var ok = await ImportUrlPolicy.ResolvePublicAsync("ok.example",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.35") }), CancellationToken.None);
        Assert.Single(ok);
    }

    [Fact]
    public async Task ResolutionErrorsBecomeUnreachableProblem() =>
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() => ImportUrlPolicy.ResolvePublicAsync("x.example",
            (_, _) => throw new SocketException(), CancellationToken.None))).Status);

    [Fact]
    public async Task DownloadsPdfByContentType()
    {
        var downloader = Downloader(_ => Pdf("application/pdf", "%PDF-1.4 body"));
        Assert.Equal("%PDF-1.4 body", Encoding.ASCII.GetString(
            await downloader.DownloadAsync("https://www.alleexamens.nl/a.pdf", CancellationToken.None)));
    }

    [Fact]
    public async Task AcceptsPdfMagicBytesWithGenericContentType() =>
        Assert.NotEmpty(await Downloader(_ => Pdf("application/octet-stream", "%PDF-1.7"))
            .DownloadAsync("https://www.alleexamens.nl/a.pdf", CancellationToken.None));

    [Fact]
    public async Task RejectsNonPdfContent() =>
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() => Downloader(_ => Pdf("text/html", "<html>"))
            .DownloadAsync("https://www.alleexamens.nl/a", CancellationToken.None))).Status);

    [Fact]
    public async Task RejectsHttpErrors() =>
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() => Downloader(_ => new HttpResponseMessage(HttpStatusCode.NotFound))
            .DownloadAsync("https://www.alleexamens.nl/a.pdf", CancellationToken.None))).Status);

    [Fact]
    public async Task AbortsStreamingBodyBeyondTwentyMiB()
    {
        var downloader = Downloader(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new EndlessStream("%PDF-"u8.ToArray()))
        });
        Assert.Equal(413, (await Assert.ThrowsAsync<ImportProblem>(() =>
            downloader.DownloadAsync("https://www.alleexamens.nl/a.pdf", CancellationToken.None))).Status);
    }

    [Fact]
    public async Task RejectsDeclaredOversizeContentLength()
    {
        var downloader = Downloader(_ =>
        {
            var response = Pdf("application/pdf", "%PDF-");
            response.Content.Headers.ContentLength = PdfExtraction.MaximumFileBytes + 1;
            return response;
        });
        Assert.Equal(413, (await Assert.ThrowsAsync<ImportProblem>(() =>
            downloader.DownloadAsync("https://www.alleexamens.nl/a.pdf", CancellationToken.None))).Status);
    }

    [Fact]
    public async Task FollowsRedirectsAndRevalidatesEachHop()
    {
        var seen = new List<string>();
        var downloader = Downloader(request =>
        {
            seen.Add(request.RequestUri!.ToString());
            return request.RequestUri.AbsolutePath switch
            {
                "/a" => Redirect("/b"),
                "/b" => Redirect("https://cdn.example/c.pdf"),
                _ => Pdf("application/pdf", "%PDF-ok")
            };
        });
        await downloader.DownloadAsync("https://www.alleexamens.nl/a", CancellationToken.None);
        Assert.Equal(["https://www.alleexamens.nl/a", "https://www.alleexamens.nl/b", "https://cdn.example/c.pdf"], seen);
    }

    [Theory]
    [InlineData("http://www.alleexamens.nl/x.pdf")]
    [InlineData("https://user@www.alleexamens.nl/x.pdf")]
    [InlineData("https://www.alleexamens.nl:444/x.pdf")]
    public async Task RejectsUnsafeRedirectTargets(string target) =>
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() => Downloader(_ => Redirect(target))
            .DownloadAsync("https://www.alleexamens.nl/a", CancellationToken.None))).Status);

    [Fact]
    public async Task StopsAfterThreeRedirects()
    {
        var calls = 0;
        var downloader = Downloader(_ => { calls++; return Redirect("/next"); });
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() =>
            downloader.DownloadAsync("https://www.alleexamens.nl/a", CancellationToken.None))).Status);
        Assert.Equal(ImportUrlPolicy.MaximumRedirects + 1, calls);
    }

    [Fact]
    public async Task NetworkFailuresBecomeDutchProblem() =>
        Assert.Equal("De link is niet bereikbaar.", (await Assert.ThrowsAsync<ImportProblem>(() =>
            Downloader(_ => throw new HttpRequestException("boom"))
                .DownloadAsync("https://www.alleexamens.nl/a.pdf", CancellationToken.None))).Message);

    [Fact]
    public async Task UrlsGoThroughTheSameValidationAsUploadedFiles()
    {
        var downloader = new FakeDownloader();
        var service = ImportProcessorTests.Service(new MemoryImportStore(), downloader);
        var template = ImportProcessorTests.Form();
        downloader.Bytes = await Bytes(template.Files["exam"]);
        var record = await service.CreateAsync(UrlForm(template), CancellationToken.None);
        Assert.Equal("queued", record.State);
        Assert.Equal(["https://www.alleexamens.nl/exam.pdf", "https://www.alleexamens.nl/answers.pdf"], downloader.Urls);

        downloader.Bytes = "<html>no pdf</html>"u8.ToArray();
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() =>
            service.CreateAsync(UrlForm(template), CancellationToken.None))).Status);
        downloader.Bytes = new byte[PdfExtraction.MaximumFileBytes + 1];
        Assert.Equal(413, (await Assert.ThrowsAsync<ImportProblem>(() =>
            service.CreateAsync(UrlForm(template), CancellationToken.None))).Status);
    }

    [Fact]
    public async Task RequiresExactlyOneSourcePerField()
    {
        var service = ImportProcessorTests.Service(new MemoryImportStore(), new FakeDownloader());
        var template = ImportProcessorTests.Form();
        var both = Combine(template, ["exam", "answers"], ("examUrl", "https://www.alleexamens.nl/e.pdf"));
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() => service.CreateAsync(both, CancellationToken.None))).Status);
        var neither = Combine(template, ["exam"]);
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() => service.CreateAsync(neither, CancellationToken.None))).Status);
    }

    private static async Task<byte[]> Bytes(IFormFile? file)
    {
        using var stream = new MemoryStream();
        await file!.CopyToAsync(stream);
        return stream.ToArray();
    }

    private static FormCollection UrlForm(IFormCollection template) =>
        Combine(template, [], ("examUrl", "https://www.alleexamens.nl/exam.pdf"),
            ("answersUrl", "https://www.alleexamens.nl/answers.pdf"));

    private static FormCollection Combine(IFormCollection template, string[] files, params (string Key, string Value)[] fields)
    {
        var values = new Dictionary<string, StringValues> { ["metadata"] = template["metadata"], ["contextText"] = "" };
        foreach (var (key, value) in fields) values[key] = value;
        var collection = new FormFileCollection();
        foreach (var name in files) collection.Add(template.Files[name]!);
        return new FormCollection(values, collection);
    }

    private static PdfDownloader Downloader(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new FakeHandler(respond)));

    private static HttpResponseMessage Pdf(string contentType, string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes(body)) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeDownloader : IPdfDownloader
    {
        public byte[] Bytes { get; set; } = [];
        public List<string> Urls { get; } = [];
        public Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
        {
            Urls.Add(url);
            return Task.FromResult(Bytes);
        }
    }

    private sealed class EndlessStream(byte[] prefix) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++, position++)
                buffer[offset + i] = position < prefix.Length ? prefix[position] : (byte)'x';
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
