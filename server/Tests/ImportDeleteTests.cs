using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Examinator.Api;
using Microsoft.AspNetCore.Http;

namespace Examinator.Tests;

public sealed class ImportDeleteTests
{
    private static ImportRecord Sample(string state, string? examId = null, string? answerId = null) => new(
        Guid.NewGuid().ToString("N"), state, new("biology", "havo", 2025, 1, "", "", [ImportProcessorTests.Topic]),
        [ImportProcessorTests.Topic], [], [], null, "", examId ?? Guid.NewGuid().ToString("N"),
        answerId ?? Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

    private static async Task<ImportRecord> Seed(MemoryImportStore store, string state)
    {
        var record = await store.WriteAsync(Sample(state), null, default);
        store.Documents[record.ExamDocumentId] = [1];
        store.Documents[record.AnswerDocumentId] = [2];
        return record;
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("awaitingDefinitions")]
    [InlineData("review")]
    public async Task IdleUnpublishedImportsCanBeDeleted(string state)
    {
        var store = new MemoryImportStore();
        var record = await Seed(store, state);
        await ImportProcessorTests.Service(store).DeleteAsync(record.Id, default);
        Assert.Null(await store.ReadAsync(record.Id, default));
        Assert.Equal([record.Id], store.Deleted);
        Assert.Empty(store.Documents);
    }

    [Theory]
    [InlineData("published")]
    [InlineData("extracting")]
    [InlineData("aligning")]
    [InlineData("tagging")]
    public async Task PublishedAndProcessingImportsAreRefusedWithADutchMessage(string state)
    {
        var store = new MemoryImportStore();
        var record = await Seed(store, state);
        var problem = await Assert.ThrowsAsync<ImportProblem>(() => ImportProcessorTests.Service(store).DeleteAsync(record.Id, default));
        Assert.Equal(409, problem.Status);
        Assert.Contains(state == "published" ? "gepubliceerde" : "verwerkt", problem.Message);
        Assert.NotNull(await store.ReadAsync(record.Id, default));
        Assert.Empty(store.Deleted);
        Assert.Equal(2, store.Documents.Count);
    }

    [Fact]
    public async Task AnImportLeasedByTheWorkerIsRefused()
    {
        var store = new MemoryImportStore { Leased = true };
        var record = await Seed(store, "queued");
        var problem = await Assert.ThrowsAsync<ImportProblem>(() => ImportProcessorTests.Service(store).DeleteAsync(record.Id, default));
        Assert.Equal(409, problem.Status);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task UnknownImportsAreNotFound()
    {
        var problem = await Assert.ThrowsAsync<ImportProblem>(() =>
            ImportProcessorTests.Service(new MemoryImportStore()).DeleteAsync(Guid.NewGuid().ToString("N"), default));
        Assert.Equal(404, problem.Status);
    }

    [Fact]
    public async Task ReprocessedImportsShareOriginalsUntilTheLastReferenceIsDeleted()
    {
        var store = new MemoryImportStore();
        var service = ImportProcessorTests.Service(store);
        var first = await service.CreateAsync(ImportProcessorTests.Form(), default);
        var second = await service.ReprocessAsync(first.Id, ReprocessForm(), default);
        Assert.Equal(first.ExamDocumentId, second.ExamDocumentId);
        Assert.All(store.Hashes.Values, hash => Assert.Equal(first.Id, hash.ImportId));

        await service.DeleteAsync(first.Id, default);
        Assert.True(store.Documents.ContainsKey(first.ExamDocumentId));
        Assert.True(store.Documents.ContainsKey(first.AnswerDocumentId));
        Assert.Equal(2, store.Hashes.Count);
        Assert.All(store.Hashes.Values, hash => Assert.Equal(second.Id, hash.ImportId));
        Assert.Null(await store.ReadAsync(first.Id, default));

        await service.DeleteAsync(second.Id, default);
        Assert.Empty(store.Documents);
        Assert.Empty(store.Hashes);
    }

    [Fact]
    public async Task ASoleImportRemovesItsOriginalsAndHashRows()
    {
        var store = new MemoryImportStore();
        var service = ImportProcessorTests.Service(store);
        var record = await service.CreateAsync(ImportProcessorTests.Form("Leerdoelen"), default);
        Assert.Equal(2, store.Hashes.Count);
        Assert.Equal(2, store.Documents.Count);
        await service.DeleteAsync(record.Id, default);
        Assert.Empty(store.Documents);
        Assert.Empty(store.Hashes);
        Assert.Equal(2, store.Released.Count);
        await service.CreateAsync(ImportProcessorTests.Form(), default);
    }

    [Fact]
    public async Task TheWorkerSkipsAnImportThatWasDeletedWhileQueued()
    {
        var store = new MemoryImportStore();
        var service = ImportProcessorTests.Service(store);
        var record = await service.CreateAsync(ImportProcessorTests.Form(), default);
        await service.DeleteAsync(record.Id, default);
        var model = new ImportProcessorTests.Model();
        await ImportProcessorTests.Processor(store, model).ProcessAsync(record.Id, default);
        Assert.Equal(0, model.Calls);
        Assert.Null(await store.ReadAsync(record.Id, default));
        Assert.Null(store.Work);
    }

    [Fact]
    public async Task StorageDeleteIsConditionalAndRemovesRecordWorkProgressIndexLockAndQueuedDispatch()
    {
        var handler = new StorageHandler();
        var (storage, record) = Storage(handler);
        handler.QueuedText = record.Id;
        await storage.DeleteAsync(record, default);

        var recordDelete = Assert.Single(handler.Requests, r => r.Method == "DELETE" && r.Path.EndsWith($"/jobs/{record.Id}.json"));
        Assert.Equal("\"v7\"", recordDelete.IfMatch);
        Assert.Contains(handler.Requests, r => r.Method == "DELETE" && r.Path.EndsWith($"/work/{record.Id}.json"));
        Assert.Contains(handler.Requests, r => r.Method == "DELETE" && r.Path.EndsWith($"/progress/{record.Id}.json"));
        Assert.Contains(handler.Requests, r => r.Method == "DELETE" && r.Path.EndsWith($"/locks/{record.Id}"));
        Assert.Contains(handler.Requests, r => r.Method == "DELETE" && r.Path.EndsWith($"/{record.Id}/manifest.json"));
        Assert.Contains(handler.Requests, r => r.Method == "DELETE" && r.Host == "table.example" &&
            r.Path.Contains("PartitionKey='imports'") && r.Path.Contains(record.Id));
        Assert.Contains(handler.Requests, r => r.Method == "DELETE" && r.Host == "queue.example" && r.Path.EndsWith("/messages/queued-1"));
    }

    [Fact]
    public async Task StorageDeleteReleasesOnlyItsOwnSittingReservation()
    {
        var handler = new StorageHandler();
        var (storage, record) = Storage(handler);
        handler.Rows[("biology-havo", "2025-1")] = new() { ["ImportId"] = record.Id };
        await storage.DeleteAsync(record, default);
        Assert.DoesNotContain(("biology-havo", "2025-1"), handler.Rows.Keys);

        var other = Sample("review");
        handler.Rows[("biology-havo", "2025-1")] = new() { ["ImportId"] = other.Id };
        await storage.DeleteAsync(record with { Id = Guid.NewGuid().ToString("N") }, default);
        Assert.Contains(("biology-havo", "2025-1"), handler.Rows.Keys);
    }

    [Fact]
    public async Task StorageKeepsASharedDocumentAndRepointsDocumentAndHashRowsToTheSurvivor()
    {
        var handler = new StorageHandler();
        var (storage, record) = Storage(handler);
        var survivor = Guid.NewGuid().ToString("N");
        handler.Digest = "abc123";
        handler.Rows[("documents", record.ExamDocumentId)] = new() { ["ImportId"] = record.Id };
        handler.Rows[("sha256", "abc123")] = new() { ["DocumentId"] = record.ExamDocumentId, ["ImportId"] = record.Id };

        await storage.ReleaseDocumentAsync(record.ExamDocumentId, record.Id, survivor, default);

        Assert.Equal(survivor, handler.Rows[("documents", record.ExamDocumentId)]["ImportId"]);
        Assert.Equal(survivor, handler.Rows[("sha256", "abc123")]["ImportId"]);
        Assert.Equal(record.ExamDocumentId, handler.Rows[("sha256", "abc123")]["DocumentId"]);
        Assert.DoesNotContain(handler.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task StorageRemovesAnUnsharedDocumentItsDocumentRowAndItsHashRowOnly()
    {
        var handler = new StorageHandler();
        var (storage, record) = Storage(handler);
        handler.Digest = "abc123";
        handler.Rows[("documents", record.ExamDocumentId)] = new() { ["ImportId"] = record.Id };
        handler.Rows[("sha256", "abc123")] = new() { ["DocumentId"] = record.ExamDocumentId, ["ImportId"] = record.Id };
        handler.Rows[("sha256", "other")] = new() { ["DocumentId"] = "elsewhere", ["ImportId"] = "x" };

        await storage.ReleaseDocumentAsync(record.ExamDocumentId, record.Id, null, default);

        Assert.DoesNotContain(("documents", record.ExamDocumentId), handler.Rows.Keys);
        Assert.DoesNotContain(("sha256", "abc123"), handler.Rows.Keys);
        Assert.Contains(("sha256", "other"), handler.Rows.Keys);
        Assert.Contains(handler.Requests, r => r.Method == "DELETE" && r.Path.EndsWith($"/{record.ExamDocumentId}/original.pdf"));
    }

    [Fact]
    public async Task StorageDoesNotTouchAHashRowThatPointsToADifferentDocument()
    {
        var handler = new StorageHandler();
        var (storage, record) = Storage(handler);
        handler.Digest = "abc123";
        handler.Rows[("documents", record.ExamDocumentId)] = new() { ["ImportId"] = record.Id };
        handler.Rows[("sha256", "abc123")] = new() { ["DocumentId"] = "first-seen", ["ImportId"] = "someone" };
        await storage.ReleaseDocumentAsync(record.ExamDocumentId, record.Id, null, default);
        Assert.Equal("someone", handler.Rows[("sha256", "abc123")]["ImportId"]);
    }

    private static (ImportStorage Storage, ImportRecord Record) Storage(StorageHandler handler)
    {
        var http = new HttpClient(handler);
        var transport = new HttpClientTransport(http);
        var credential = new Credential();
        var blobOptions = new BlobClientOptions { Transport = transport, Retry = { MaxRetries = 0 } };
        var tableOptions = new TableClientOptions { Transport = transport, Retry = { MaxRetries = 0 } };
        var storage = new ImportStorage(
            new BlobContainerClient(new Uri("https://blob.example/exam-imports"), credential, blobOptions),
            new BlobContainerClient(new Uri("https://blob.example/exam-documents"), credential, blobOptions),
            new QueueClient(new Uri("https://queue.example/exam-imports"), credential,
                new QueueClientOptions { Transport = transport, Retry = { MaxRetries = 0 } }),
            new BlobContainerClient(new Uri("https://blob.example/exam-results"), credential, blobOptions),
            new TableClient(new Uri("https://table.example"), "ExamCatalog", credential, tableOptions),
            new TableClient(new Uri("https://table.example"), "ImportJobs", credential, tableOptions));
        return (storage, Sample("review") with { Revision = "\"v7\"" });
    }

    private static FormCollection ReprocessForm() => new(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
    {
        ["metadata"] = JsonSerializer.Serialize(new ImportMetadata("biology", "havo", 0, 0, "", "", [ImportProcessorTests.Topic]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        ["contextText"] = ""
    });

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed record Seen(string Method, string Host, string Path, string? IfMatch);

    /// <summary>A synthetic Azure Storage endpoint: blobs, queue and tables answer from memory; no network is used.</summary>
    private sealed class StorageHandler : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        public Dictionary<(string Partition, string Row), Dictionary<string, string>> Rows { get; } = [];
        public string Digest { get; set; } = "";
        public string? QueuedText { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            Requests.Add(new(request.Method.Method, uri.Host, path, request.Headers.IfMatch.ToString() is { Length: > 0 } match ? match : null));
            var response = uri.Host switch
            {
                "table.example" => Table(request, path),
                "queue.example" => Queue(request),
                _ => Blob(request)
            };
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "synthetic");
            response.Headers.TryAddWithoutValidation("x-ms-version", "2024-11-04");
            return Task.FromResult(response);
        }

        private HttpResponseMessage Table(HttpRequestMessage request, string path)
        {
            var partition = Between(path, "PartitionKey='", "'");
            var row = Between(path, "RowKey='", "'");
            HttpResponseMessage response;
            if (request.Method == HttpMethod.Get)
            {
                if (!Rows.TryGetValue((partition, row), out var values))
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{\"odata.error\":{\"code\":\"ResourceNotFound\"}}", Encoding.UTF8, "application/json")
                    };
                var entity = new Dictionary<string, string>(values) { ["PartitionKey"] = partition, ["RowKey"] = row, ["odata.etag"] = "W/\"synthetic-etag\"" };
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(entity), Encoding.UTF8, "application/json")
                };
            }
            else
            {
                if (request.Method == HttpMethod.Delete) Rows.Remove((partition, row));
                else if (request.Method == HttpMethod.Put || request.Method.Method == "MERGE")
                {
                    var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement;
                    Rows[(partition, row)] = body.EnumerateObject()
                        .Where(property => property.Value.ValueKind == JsonValueKind.String &&
                            property.Name is not ("PartitionKey" or "RowKey"))
                        .ToDictionary(property => property.Name, property => property.Value.GetString()!);
                }
                response = new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            response.Headers.TryAddWithoutValidation("ETag", "W/\"datetime'2026-01-01T00%3A00%3A00.0000000Z'\"");
            return response;
        }

        private HttpResponseMessage Queue(HttpRequestMessage request)
        {
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var peek = request.RequestUri!.Query.Contains("peekonly=true");
            var items = QueuedText is null ? "" :
                $"<QueueMessage><MessageId>queued-1</MessageId><InsertionTime>Thu, 01 Jan 2026 00:00:00 GMT</InsertionTime>" +
                $"<ExpirationTime>Thu, 08 Jan 2026 00:00:00 GMT</ExpirationTime>" +
                (peek ? "" : "<PopReceipt>receipt</PopReceipt><TimeNextVisible>Thu, 01 Jan 2026 00:00:02 GMT</TimeNextVisible>") +
                $"<DequeueCount>1</DequeueCount><MessageText>{QueuedText}</MessageText></QueueMessage>";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<?xml version=\"1.0\" encoding=\"utf-8\"?><QueueMessagesList>{items}</QueueMessagesList>",
                    Encoding.UTF8, "application/xml")
            };
        }

        private HttpResponseMessage Blob(HttpRequestMessage request)
        {
            var response = new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.Accepted : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
            response.Headers.TryAddWithoutValidation("ETag", "\"test-etag\"");
            response.Content.Headers.TryAddWithoutValidation("Last-Modified", DateTimeOffset.UtcNow.ToString("R"));
            if (request.Method == HttpMethod.Head)
            {
                response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                response.Headers.TryAddWithoutValidation("x-ms-meta-sha256", Digest);
                response.Content.Headers.ContentLength = 1;
            }
            return response;
        }

        private static string Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            if (from < 0) return "";
            from += start.Length;
            return text[from..text.IndexOf(end, from, StringComparison.Ordinal)];
        }
    }
}
