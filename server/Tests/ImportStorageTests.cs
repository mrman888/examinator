using System.Net;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Data.Tables;
using System.Text;
using System.Text.Json;
using Examinator.Api;

namespace Examinator.Tests;

public sealed class ImportStorageTests
{
    [Fact]
    public async Task WritesAreConditionalAndReturnStorageRevision()
    {
        var handler = new BlobHandler();
        using var http = new HttpClient(handler);
        var transport = new HttpClientTransport(http);
        var credential = new Credential();
        var records = new BlobContainerClient(new Uri("https://storage.example/exam-imports"), credential,
            new BlobClientOptions { Transport = transport, Retry = { MaxRetries = 0 } });
        var documents = new BlobContainerClient(new Uri("https://storage.example/exam-documents"), credential,
            new BlobClientOptions { Transport = transport });
        var queue = new QueueClient(new Uri("https://storage.example/exam-imports"), credential,
            new QueueClientOptions { Transport = transport });
        var now = new DateTimeOffset(2026, 11, 14, 15, 2, 0, TimeSpan.Zero);
        var storage = new ImportStorage(records, documents, queue) { Time = new ManualTime(now) };
        var record = new ImportRecord(Guid.NewGuid().ToString("N"), "queued",
            new("biology", "havo", 2025, 1, "", "", []), [], [], [], null, "",
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var created = await storage.WriteAsync(record, null, CancellationToken.None);
        Assert.Equal("*", handler.IfNoneMatch);
        Assert.Equal("\"test-etag\"", created.Revision);
        await storage.WriteAsync(created with { State = "review" }, created.Revision, CancellationToken.None);
        Assert.Equal("\"test-etag\"", handler.IfMatch);
        Assert.DoesNotContain("test-etag", handler.Body);
        Assert.Contains("\"state\":\"review\"", handler.Body);
        Assert.Equal(now, created.UpdatedAt);
        Assert.Contains("\"updatedAt\":\"2026-11-14T15:02:00+00:00\"", handler.Body);
        await storage.WriteAsync(created with { Timing = new ImportTiming(null, null, null, null, null, false, []) }, created.Revision, default);
        Assert.Contains("\"timing\":null", handler.Body);
    }

    [Fact]
    public void DocumentNamesCannotEscapeGeneratedIds()
    {
        var credential = new Credential();
        var storage = new ImportStorage(new Uri("https://storage.example/"), new Uri("https://queue.example/"), credential);
        Assert.Throws<ArgumentException>(() => storage.Document("../other"));
        Assert.Contains("/original.pdf", storage.Document(Guid.NewGuid().ToString("N")).Uri.AbsolutePath);
        Assert.Throws<ArgumentException>(() => new ImportStorage(new Uri("http://storage.example/"),
            new Uri("https://queue.example/"), credential));
    }

    [Theory]
    [InlineData("review", 0)]
    [InlineData("cancelled", 0)]
    [InlineData("published", 1)]
    public async Task CatalogPointerDoesNotExposeDraftUntilConditionalPublicationCommits(string state, int publishedCount)
    {
        var topic = ImportProcessorTests.Topic;
        var id = Guid.NewGuid().ToString("N");
        var record = new ImportRecord(id, state, new("biology", "havo", 2025, 1, "", "", [topic]),
            [topic], [new("biology-havo-2025-1-q1", 1, "Celstructuur", 1, 1, 1, 1, [topic.Id], [], false, [])],
            [], null, "\"v1\"", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var handler = new CatalogHandler(record);
        using var http = new HttpClient(handler);
        var transport = new HttpClientTransport(http);
        var credential = new Credential();
        var options = new BlobClientOptions { Transport = transport, Retry = { MaxRetries = 0 } };
        var storage = new ImportStorage(
            new BlobContainerClient(new Uri("https://blob.example/exam-imports"), credential, options),
            new BlobContainerClient(new Uri("https://blob.example/exam-documents"), credential, options),
            new QueueClient(new Uri("https://queue.example/exam-imports"), credential),
            new BlobContainerClient(new Uri("https://blob.example/exam-results"), credential, options),
            new TableClient(new Uri("https://table.example"), "ExamCatalog", credential,
                new TableClientOptions { Transport = transport, Retry = { MaxRetries = 0 } }));
        var catalog = await storage.CatalogAsync("biology", default);
        Assert.Equal(publishedCount, catalog.Exams.Length);
        Assert.Equal(publishedCount, catalog.Questions.Length);
        Assert.Equal(publishedCount, handler.ManifestReads);
        if (publishedCount > 0)
        {
            Assert.Equal($"/api/documents/{record.ExamDocumentId}", catalog.Questions[0].ExamFile);
            Assert.Equal($"/api/documents/{record.AnswerDocumentId}", catalog.Exams[0].AnswerFile);
        }
    }

    [Fact]
    public async Task ProgressIsWrittenUnconditionallyAndReadsAreConditional()
    {
        var handler = new ConditionalHandler();
        using var http = new HttpClient(handler);
        var options = new BlobClientOptions { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } };
        var credential = new Credential();
        var storage = new ImportStorage(
            new BlobContainerClient(new Uri("https://storage.example/exam-imports"), credential, options),
            new BlobContainerClient(new Uri("https://storage.example/exam-documents"), credential, options),
            new QueueClient(new Uri("https://storage.example/exam-imports"), credential));
        var id = Guid.NewGuid().ToString("N");
        var progress = new ImportProgress(id, 1, 3, "tagging", 2, 5, "vraag", 4, 64, "Vraag 2 van 5: onderwerpen toekennen",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        await storage.WriteProgressAsync(progress, default);
        var write = handler.Requests.Single();
        Assert.Equal(("PUT", $"/exam-imports/progress/{id}.json", "", ""), (write.Method, write.Path, write.IfMatch, write.IfNoneMatch));

        handler.Body = JsonSerializer.Serialize(progress, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var read = await storage.ReadProgressAsync(id, null, default);
        Assert.Equal(progress, read!.Value);
        Assert.Equal("\"p1\"", read.ETag);
        Assert.Null(await storage.ReadProgressAsync(id, "\"p1\"", default));
        Assert.Equal("\"p1\"", handler.Requests.Last().IfNoneMatch);

        handler.Body = JsonSerializer.Serialize(Sample(id), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var record = await storage.ReadIfChangedAsync(id, null, default);
        Assert.Equal("\"p1\"", record!.Value!.Revision);
        Assert.NotNull(record.Value.UpdatedAt);
        Assert.Equal(record.LastModified, record.Value.UpdatedAt);
        Assert.Equal($"/exam-imports/jobs/{id}.json", handler.Requests.Last().Path);
        Assert.Null(await storage.ReadIfChangedAsync(id, "\"p1\"", default));

        handler.Missing = true;
        var missing = await storage.ReadProgressAsync(id, null, default);
        Assert.Null(missing!.Value);
    }

    private static ImportRecord Sample(string id) => new(id, "tagging", new("biology", "havo", 2025, 1, "", "", []), [], [], [], null, "",
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, ProcessingAttempts: 1);

    private sealed class ConditionalHandler : HttpMessageHandler
    {
        public List<(string Method, string Path, string IfMatch, string IfNoneMatch)> Requests { get; } = [];
        public string Body { get; set; } = "{}";
        public bool Missing { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ifNoneMatch = request.Headers.IfNoneMatch.ToString();
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.IfMatch.ToString(), ifNoneMatch));
            var status = request.Method == HttpMethod.Put ? HttpStatusCode.Created
                : Missing ? HttpStatusCode.NotFound
                : ifNoneMatch == "\"p1\"" ? HttpStatusCode.NotModified : HttpStatusCode.OK;
            var response = new HttpResponseMessage(status)
            {
                Content = status == HttpStatusCode.OK ? new StringContent(Body, Encoding.UTF8, "application/json") : new ByteArrayContent([])
            };
            if (status == HttpStatusCode.OK) response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(Body);
            if (status == HttpStatusCode.NotFound) response.Headers.TryAddWithoutValidation("x-ms-error-code", "BlobNotFound");
            response.Headers.TryAddWithoutValidation("ETag", "\"p1\"");
            response.Headers.TryAddWithoutValidation("Last-Modified", DateTimeOffset.UtcNow.ToString("R"));
            response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "synthetic");
            response.Headers.TryAddWithoutValidation("x-ms-version", "2024-11-04");
            return Task.FromResult(response);
        }
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class BlobHandler : HttpMessageHandler
    {
        public string? IfNoneMatch { get; private set; }
        public string? IfMatch { get; private set; }
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IfNoneMatch = request.Headers.IfNoneMatch.ToString();
            IfMatch = request.Headers.IfMatch.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new ByteArrayContent([])
            };
            response.Headers.TryAddWithoutValidation("ETag", "\"test-etag\"");
            response.Headers.TryAddWithoutValidation("Last-Modified", DateTimeOffset.UtcNow.ToString("R"));
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "synthetic");
            response.Headers.TryAddWithoutValidation("x-ms-version", "2024-11-04");
            return response;
        }
    }

    private sealed class CatalogHandler(ImportRecord record) : HttpMessageHandler
        {
            public int ManifestReads { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string json;
                if (request.RequestUri!.Host == "table.example")
                {
                    json = JsonSerializer.Serialize(new { value = new[] { new
                    {
                        PartitionKey = "biology-havo", RowKey = "2025-1", ImportId = record.Id
                    } } });
                }
                else
                {
                    var snapshot = request.RequestUri.AbsolutePath.EndsWith("/manifest.json");
                    if (snapshot) ManifestReads++;
                    json = JsonSerializer.Serialize(snapshot ? record with { State = "published" } : record,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                }
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(json);
                response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
                response.Headers.TryAddWithoutValidation("Last-Modified", DateTimeOffset.UtcNow.ToString("R"));
                response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                response.Headers.TryAddWithoutValidation("x-ms-request-id", "synthetic");
                return Task.FromResult(response);
            }
    }
}
