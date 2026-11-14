using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Examinator.Api;
using Examinator.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace Examinator.Tests;

public sealed class ImportDedupTests
{
    [Fact]
    public async Task StoringAnOriginalWritesAHashIndexRowAndLookupFindsIt()
    {
        var handler = new TableHandler();
        using var http = new HttpClient(handler);
        var transport = new HttpClientTransport(http);
        var credential = new Credential();
        var options = new BlobClientOptions { Transport = transport, Retry = { MaxRetries = 0 } };
        var storage = new ImportStorage(
            new BlobContainerClient(new Uri("https://blob.example/exam-imports"), credential, options),
            new BlobContainerClient(new Uri("https://blob.example/exam-documents"), credential, options),
            new QueueClient(new Uri("https://queue.example/exam-imports"), credential),
            null,
            new TableClient(new Uri("https://table.example"), "ExamCatalog", credential,
                new TableClientOptions { Transport = transport, Retry = { MaxRetries = 0 } }));
        var bytes = "%PDF-1.4 synthetic"u8.ToArray();
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var documentId = Guid.NewGuid().ToString("N");
        var importId = Guid.NewGuid().ToString("N");

        await storage.UploadAsync(documentId, new MemoryStream(bytes), "application/pdf", "exam",
            new("biology", "havo", 2025, 1, "", "", []), importId, CancellationToken.None);
        var row = Assert.Single(handler.Rows, entity => entity.GetProperty("PartitionKey").GetString() == "sha256");
        Assert.Equal(digest, row.GetProperty("RowKey").GetString());
        Assert.Equal(documentId, row.GetProperty("DocumentId").GetString());
        Assert.Equal("exam", row.GetProperty("Role").GetString());
        Assert.Equal(importId, row.GetProperty("ImportId").GetString());

        var found = await storage.FindHashAsync(digest.ToUpperInvariant(), CancellationToken.None);
        Assert.Equal(documentId, found!.DocumentId);
        Assert.Equal(importId, found.ImportId);
        Assert.Null(await storage.FindHashAsync(new string('0', 64), CancellationToken.None));
    }

    [Fact]
    public async Task ContextScreenshotsAreNotHashIndexed()
    {
        var handler = new TableHandler();
        using var http = new HttpClient(handler);
        var transport = new HttpClientTransport(http);
        var credential = new Credential();
        var options = new BlobClientOptions { Transport = transport, Retry = { MaxRetries = 0 } };
        var storage = new ImportStorage(
            new BlobContainerClient(new Uri("https://blob.example/exam-imports"), credential, options),
            new BlobContainerClient(new Uri("https://blob.example/exam-documents"), credential, options),
            new QueueClient(new Uri("https://queue.example/exam-imports"), credential),
            null,
            new TableClient(new Uri("https://table.example"), "ExamCatalog", credential,
                new TableClientOptions { Transport = transport, Retry = { MaxRetries = 0 } }));
        await storage.UploadAsync(Guid.NewGuid().ToString("N"), new MemoryStream([1, 2, 3, 4, 5]), "image/png", "context",
            new("biology", "havo", 0, 0, "", "", []), Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.DoesNotContain(handler.Rows, entity => entity.GetProperty("PartitionKey").GetString() == "sha256");
    }

    [Fact]
    public async Task DuplicateUploadIsRejectedBeforeStoringAnythingAndNamesTheEarlierImport()
    {
        var store = new MemoryImportStore();
        var service = ImportProcessorTests.Service(store);
        var first = await service.CreateAsync(ImportProcessorTests.Form(), CancellationToken.None);
        var uploads = store.Uploads;

        var problem = await Assert.ThrowsAsync<ImportDuplicateProblem>(() =>
            service.CreateAsync(ImportProcessorTests.Form(), CancellationToken.None));
        Assert.Equal(409, problem.Status);
        Assert.Equal(uploads, store.Uploads);
        Assert.Equal(1, store.Dispatches);
        Assert.Equal(2, problem.Duplicates.Length);
        Assert.All(problem.Duplicates, duplicate =>
        {
            Assert.Equal(first.Id, duplicate.ImportId);
            Assert.Equal("queued", duplicate.State);
        });
        Assert.Equal(["exam", "answers"], problem.Duplicates.Select(d => d.Role));
    }

    [Fact]
    public async Task DuplicateDownloadIsRejectedWithTheSameHashCheck()
    {
        var store = new MemoryImportStore();
        var template = ImportProcessorTests.Form();
        var examBytes = new MemoryStream();
        await template.Files["exam"]!.CopyToAsync(examBytes);
        var answerBytes = new MemoryStream();
        await template.Files["answers"]!.CopyToAsync(answerBytes);
        var downloader = new MapDownloader(new()
        {
            ["https://www.alleexamens.nl/exam.pdf"] = examBytes.ToArray(),
            ["https://www.alleexamens.nl/answers.pdf"] = answerBytes.ToArray()
        });
        var service = ImportProcessorTests.Service(store, downloader);
        await service.CreateAsync(ImportProcessorTests.Form(), CancellationToken.None);
        var uploads = store.Uploads;

        var problem = await Assert.ThrowsAsync<ImportDuplicateProblem>(() => service.CreateAsync(new FormCollection(
            new Dictionary<string, StringValues>
            {
                ["metadata"] = template["metadata"], ["examUrl"] = "https://www.alleexamens.nl/exam.pdf",
                ["answersUrl"] = "https://www.alleexamens.nl/answers.pdf"
            }), CancellationToken.None));
        Assert.Equal(2, problem.Duplicates.Length);
        Assert.Equal(uploads, store.Uploads);
    }

    [Fact]
    public async Task ExceptionHandlerReturnsDuplicatesAsProblemDetails()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);
        await handler.TryHandleAsync(context, new ImportDuplicateProblem(
            [new("import-1", "published", 2025, 1, "exam", "doc-1")]), CancellationToken.None);
        Assert.Equal(409, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("Dit document is al eerder verwerkt.", json.RootElement.GetProperty("title").GetString());
        var duplicate = json.RootElement.GetProperty("duplicates")[0];
        Assert.Equal("import-1", duplicate.GetProperty("importId").GetString());
        Assert.Equal(2025, duplicate.GetProperty("year").GetInt32());
    }

    [Fact]
    public async Task ReprocessCreatesANewJobFromTheStoredOriginalsWithoutReuploading()
    {
        var store = new MemoryImportStore();
        var service = ImportProcessorTests.Service(store);
        var original = await service.CreateAsync(ImportProcessorTests.Form(), CancellationToken.None);
        var uploads = store.Uploads;
        var dispatches = store.Dispatches;
        var topic = ImportProcessorTests.Topic with { Id = "biology-new-topic", Title = "Nieuw onderwerp" };

        var next = await service.ReprocessAsync(original.Id, TopicForm(topic, "Nieuwe uitleg"), CancellationToken.None);
        Assert.NotEqual(original.Id, next.Id);
        Assert.Equal("queued", next.State);
        Assert.Equal(original.ExamDocumentId, next.ExamDocumentId);
        Assert.Equal(original.AnswerDocumentId, next.AnswerDocumentId);
        Assert.Equal([topic.Id], next.Topics.Select(t => t.Id));
        Assert.Equal("Nieuwe uitleg", next.ContextText);
        Assert.Null(next.ContextDocumentId);
        Assert.Equal(uploads, store.Uploads);
        Assert.Equal(dispatches + 1, store.Dispatches);
        Assert.Equal("queued", original.State);
    }

    [Fact]
    public async Task ReprocessKeepsHavoOnlyAndRejectsMissingTopicsOrUnknownImports()
    {
        var store = new MemoryImportStore();
        var service = ImportProcessorTests.Service(store);
        var original = await service.CreateAsync(ImportProcessorTests.Form(), CancellationToken.None);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var vwo = new FormCollection(new Dictionary<string, StringValues>
        {
            ["metadata"] = JsonSerializer.Serialize(new ImportMetadata("biology", "vwo", 0, 0, "", "", [ImportProcessorTests.Topic]), options)
        });
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() => service.ReprocessAsync(original.Id, vwo, CancellationToken.None))).Status);
        var noTopics = new FormCollection(new Dictionary<string, StringValues>
        {
            ["metadata"] = JsonSerializer.Serialize(new ImportMetadata("biology", "havo", 0, 0, "", "", []), options)
        });
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() => service.ReprocessAsync(original.Id, noTopics, CancellationToken.None))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ImportProblem>(() => service.ReprocessAsync(
            Guid.NewGuid().ToString("N"), TopicForm(ImportProcessorTests.Topic, ""), CancellationToken.None))).Status);
        var withPdf = new FormCollection(new Dictionary<string, StringValues> { ["metadata"] = TopicForm(ImportProcessorTests.Topic, "")["metadata"] },
            ImportProcessorTests.Form().Files);
        Assert.Equal(400, (await Assert.ThrowsAsync<ImportProblem>(() => service.ReprocessAsync(original.Id, withPdf, CancellationToken.None))).Status);
    }

    [Fact]
    public async Task ReprocessKeepsTheKnownSittingSoPublicationStillCannotReplaceSilently()
    {
        var store = new MemoryImportStore();
        var service = ImportProcessorTests.Service(store);
        var original = await service.CreateAsync(ImportProcessorTests.Form(), CancellationToken.None);
        Assert.Equal((2025, 1), (original.Metadata.Year, original.Metadata.Timevak));
        var next = await service.ReprocessAsync(original.Id, TopicForm(ImportProcessorTests.Topic, ""), CancellationToken.None);
        Assert.Equal((2025, 1), (next.Metadata.Year, next.Metadata.Timevak));
    }

    private static FormCollection TopicForm(TopicDefinition topic, string contextText) => new(new Dictionary<string, StringValues>
    {
        ["metadata"] = JsonSerializer.Serialize(new ImportMetadata("biology", "havo", 0, 0, "", "", [topic]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        ["contextText"] = contextText
    });

    private sealed class MapDownloader(Dictionary<string, byte[]> map) : IPdfDownloader
    {
        public Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken) => Task.FromResult(map[url]);
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class TableHandler : HttpMessageHandler
    {
        public List<JsonElement> Rows { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            if (request.RequestUri!.Host == "table.example")
            {
                if (request.Method == HttpMethod.Get)
                {
                    var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
                    var found = Rows.FirstOrDefault(row =>
                        path.Contains($"PartitionKey='{row.GetProperty("PartitionKey").GetString()}'") &&
                        path.Contains($"RowKey='{row.GetProperty("RowKey").GetString()}'"));
                    if (found.ValueKind == JsonValueKind.Undefined)
                        return new HttpResponseMessage(HttpStatusCode.NotFound)
                        {
                            Content = new StringContent("{\"odata.error\":{\"code\":\"ResourceNotFound\"}}", Encoding.UTF8, "application/json")
                        };
                    response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(found.GetRawText(), Encoding.UTF8, "application/json")
                    };
                }
                else
                {
                    if (request.Method == HttpMethod.Post)
                        Rows.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
                    response = new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                response.Headers.TryAddWithoutValidation("ETag", "W/\"datetime'2026-01-01T00%3A00%3A00.0000000Z'\"");
            }
            else
            {
                await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) };
                response.Headers.TryAddWithoutValidation("ETag", "\"test-etag\"");
                response.Headers.TryAddWithoutValidation("Last-Modified", DateTimeOffset.UtcNow.ToString("R"));
            }
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "synthetic");
            response.Headers.TryAddWithoutValidation("x-ms-version", "2024-11-04");
            return response;
        }
    }
}
