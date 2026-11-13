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
        var storage = new ImportStorage(records, documents, queue);
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
