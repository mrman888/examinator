using System.Text;
using System.Text.Json;
using Azure;
using Examinator.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace Examinator.Tests;

public sealed class ImportProcessorTests
{
    internal static readonly TopicDefinition Topic = new("biology-cells", "Cellen", "Celstructuur", ["cel"], [], [], "2", "2.1");

    [Fact]
    public async Task UploadContextApprovalReviewAndPublicationAreSeparateDurableSteps()
    {
        var store = new MemoryImportStore();
        var service = Service(store);
        var record = await service.CreateAsync(Form("Leerdoelen celstructuur"), CancellationToken.None);
        Assert.Equal("queued", record.State);
        Assert.Empty((await store.CatalogAsync("biology", default)).Exams);
        var model = new Model();
        var processor = Processor(store, model);
        await processor.ProcessAsync(record.Id, default);
        record = (await store.ReadAsync(record.Id, default))!;
        Assert.Equal("awaitingDefinitions", record.State);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, store.Work!.ModelCalls);
        record = await service.UpdateAsync(record.Id, "confirm-definitions",
            new DefinitionsUpdate(record.Revision, [Topic with { ChapterNumber = "ander hoofdstuk" }]), default);
        await processor.ProcessAsync(record.Id, default);
        record = (await store.ReadAsync(record.Id, default))!;
        Assert.Equal("review", record.State);
        Assert.Equal(4, model.Calls);
        Assert.True(record.Questions.Single().NeedsReview);
        Assert.Equal(Topic.Id, record.Questions.Single().TopicIds.Single());
        Assert.Contains(model.Images, image => image.Role == "exam");
        Assert.Contains(model.Images, image => image.Role == "answers");
        var problem = await Assert.ThrowsAsync<ImportProblem>(() => service.UpdateAsync(record.Id, "publish", new RevisionUpdate(record.Revision), default));
        Assert.Equal(400, problem.Status);
        Assert.Empty((await store.CatalogAsync("biology", default)).Exams);
        record = await service.UpdateAsync(record.Id, "draft", new DraftUpdate(record.Revision,
            record.Topics, [record.Questions.Single() with { NeedsReview = false }]), default);
        record = await service.UpdateAsync(record.Id, "publish", new RevisionUpdate(record.Revision), default);
        Assert.Equal("published", record.State);
        Assert.Single((await store.CatalogAsync("biology", default)).Exams);
        Assert.Equal(2, store.Dispatches);
    }

    [Fact]
    public async Task ImportReadsYearAndTimevakFromBothDocuments()
    {
        var store = new MemoryImportStore();
        var record = await Service(store).CreateAsync(Form(unknownSitting: true), default);
        Assert.Equal(0, record.Metadata.Year);
        Assert.Equal(0, record.Metadata.Timevak);

        await Processor(store, new Model()).ProcessAsync(record.Id, default);

        Assert.Equal(2025, store.Record!.Metadata.Year);
        Assert.Equal(1, store.Record.Metadata.Timevak);
        Assert.Equal("review", store.Record.State);
    }

    [Fact]
    public async Task InterruptedPaidCallRequiresExplicitRetryAndRetainsSpentBudget()
    {
        var store = new MemoryImportStore();
        var record = await Service(store).CreateAsync(Form(), default);
        store.Work = Work() with { ModelCalls = 1, PendingCall = "exam-inventory" };
        var model = new Model();
        await Processor(store, model).ProcessAsync(record.Id, default);
        record = (await store.ReadAsync(record.Id, default))!;
        Assert.Equal("failed", record.State);
        Assert.Equal(0, model.Calls);
        record = await Service(store).UpdateAsync(record.Id, "retry", new RevisionUpdate(record.Revision), default);
        Assert.Null(store.Work!.PendingCall);
        Assert.Equal(1, store.Work.ModelCalls);
        await Processor(store, model).ProcessAsync(record.Id, default);
        Assert.Equal("review", store.Record!.State);
        Assert.Equal(4, store.Work.ModelCalls);
    }

    [Fact]
    public async Task CompletedClassificationCheckpointDoesNotSpendAgainAfterRestart()
    {
        var store = new MemoryImportStore();
        var record = await Service(store).CreateAsync(Form(), default);
        var inventory = QuestionAlignment.Align(record.Metadata, [new(1, "Celstructuur", 1, 1)], [new(1, "Correctie", 1, 1)], 1, 1);
        store.Work = Work() with
        {
            Inventory = inventory, ExamInventory = Inventory("exam"), AnswerInventory = Inventory("answers"),
            ClassifiedCount = 1, ModelCalls = 3, Documents = new(1, 1, true, true)
        };
        store.Record = record with { State = "tagging", Questions = [inventory[0] with { TopicIds = [Topic.Id] }] };
        var model = new Model();
        await Processor(store, model).ProcessAsync(record.Id, default);
        Assert.Equal("review", store.Record.State);
        Assert.Equal(0, model.Calls);
        Assert.Equal(Topic.Id, store.Record.Questions.Single().TopicIds.Single());
    }

    [Fact]
    public async Task CancellationDuringModelCallIsNotOverwrittenByLateResult()
    {
        var store = new MemoryImportStore();
        var record = await Service(store).CreateAsync(Form(), default);
        var model = new Model
        {
            BeforeClassification = async () =>
            {
                var latest = store.Record!;
                await Service(store).UpdateAsync(latest.Id, "cancel", new RevisionUpdate(latest.Revision), default);
            }
        };
        await Processor(store, model).ProcessAsync(record.Id, default);
        Assert.Equal("cancelled", store.Record!.State);
        Assert.Empty((await store.CatalogAsync("biology", default)).Exams);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MismatchedDocumentCodeOrInventoryFailsBeforeTagging(bool codeMismatch)
    {
        var store = new MemoryImportStore();
        var record = await Service(store).CreateAsync(Form(), default);
        var model = new Model { CodeMismatch = codeMismatch, CountMismatch = !codeMismatch };
        await Processor(store, model).ProcessAsync(record.Id, default);
        Assert.Equal("failed", store.Record!.State);
        Assert.Equal(2, model.Calls);
        Assert.Empty(store.Record.Questions);
    }

    [Fact]
    public async Task MismatchedDocumentSittingsFailBeforeMetadataIsAccepted()
    {
        var store = new MemoryImportStore();
        var record = await Service(store).CreateAsync(Form(unknownSitting: true), default);
        var model = new Model { SittingMismatch = true };
        await Processor(store, model).ProcessAsync(record.Id, default);
        Assert.Equal("failed", store.Record!.State);
        Assert.Equal(0, store.Record.Metadata.Year);
        Assert.Equal(0, store.Record.Metadata.Timevak);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task BudgetIsCheckedBeforeSendingAnotherModelRequest()
    {
        var store = new MemoryImportStore();
        var record = await Service(store).CreateAsync(Form(), default);
        var model = new Model();
        await Processor(store, model, 2).ProcessAsync(record.Id, default);
        Assert.Equal("failed", store.Record!.State);
        Assert.Equal(2, model.Calls);
        Assert.Equal(2, store.Work!.ModelCalls);
        Assert.Null(store.Work.PendingCall);
    }

    [Fact]
    public async Task DispatchFailureIsVisibleAndUploadRemainsDurable()
    {
        var store = new MemoryImportStore { FailDispatch = true };
        var record = await Service(store).CreateAsync(Form(), default);
        Assert.Equal("queued", record.State);
        Assert.Contains(record.Warnings, warning => warning.Contains("wachtrij"));
        Assert.Equal(record.Revision, store.Record!.Revision);
        Assert.Equal(2, store.Documents.Count);
    }

    [Fact]
    public async Task NewDispatchHintsCannotResetDurableWorkerRestartLimit()
    {
        var store = new MemoryImportStore();
        var record = await Service(store).CreateAsync(Form(), default);
        store.Record = record with { ProcessingAttempts = ImportProcessor.MaximumProcessingAttempts };
        var model = new Model();
        await Processor(store, model).ProcessAsync(record.Id, default);
        Assert.Equal("failed", store.Record.State);
        Assert.Equal(0, model.Calls);
        var retried = await Service(store).UpdateAsync(record.Id, "retry", new RevisionUpdate(store.Record.Revision), default);
        Assert.Equal(0, retried.ProcessingAttempts);
    }

    internal static ExamImports Service(MemoryImportStore store) => new(store, NullLogger<ExamImports>.Instance);
    private static ImportProcessor Processor(MemoryImportStore store, Model model, int calls = 64) => new(store, model,
        new Renderer(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Foundry:ImportDeployment"] = "synthetic", ["Import:MaximumModelCalls"] = calls.ToString() }).Build(),
        NullLogger<ImportProcessor>.Instance);

    internal static IFormCollection Form(string context = "", bool unknownSitting = false)
    {
        var files = new FormFileCollection();
        foreach (var role in new[] { "exam", "answers" })
        {
            var bytes = Pdf(role);
            files.Add(new FormFile(new MemoryStream(bytes), 0, bytes.Length, role, role + ".pdf")
            { Headers = new HeaderDictionary(), ContentType = "application/pdf" });
        }
        return new FormCollection(new Dictionary<string, StringValues>
        {
            ["metadata"] = JsonSerializer.Serialize(new ImportMetadata("biology", "havo",
                unknownSitting ? 0 : 2025, unknownSitting ? 0 : 1, "2", "Cellen", [Topic]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ["contextText"] = context
        }, files);
    }

    private static byte[] Pdf(string text)
    {
        var stream = $"BT /F1 12 Tf 20 100 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static ImportWork Work() => new(new([new(1, 200, 200, false, true, [])]),
        new([new(1, 200, 200, false, true, [])]), [], new(1, 1, false, false),
        [new("exam:p1", 1, "Celstructuur"), new("answers:p1", 1, "Correctie")], 0, 0,
        ImportModel.PromptVersion, "synthetic");
    private static DocumentInventory Inventory(string role) => new("biology", "havo", 2025, 1, "HA-1021-a-25-1", role,
        1, [new(1, "Celstructuur", 1, 1)]);

    private sealed class Renderer : IPdfRenderer
    {
        public Task<PageImage[]> RenderAsync(Stream stream, int pages, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Range(1, pages).Select(page => new PageImage(page, "synthetic-image")).ToArray());
    }

    private sealed class Model : IImportModel
    {
        public int Calls { get; private set; }
        public bool CodeMismatch { get; init; }
        public bool CountMismatch { get; init; }
        public bool SittingMismatch { get; init; }
        public Func<Task>? BeforeClassification { get; init; }
        public PageImage[] Images { get; private set; } = [];
        public Task<DocumentInventory> InventoryAsync(ImportMetadata metadata, PdfContent content, PageImage[] images, string role, CancellationToken cancellationToken)
        {
            Calls++;
            var result = Inventory(role);
            if (role == "answers" && CodeMismatch) result = result with { ExamCode = "different" };
            if (role == "answers" && CountMismatch) result = result with { QuestionCount = 2 };
            if (role == "answers" && SittingMismatch) result = result with { Timevak = 2 };
            return Task.FromResult(result);
        }
        public Task<TopicDefinition[]> PrepareTopicsAsync(ImportRecord record, string? imageDataUrl, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new[] { Topic });
        }
        public async Task<ClassificationResult> ClassifyAsync(DraftQuestion question, TopicDefinition[] topics, ImportSourceBlock[] blocks, PageImage[] images, CancellationToken cancellationToken)
        {
            Calls++;
            Images = images;
            if (BeforeClassification is not null) await BeforeClassification();
            return new("biology", question.Id, question.Number, [new(Topic.Id, "Celstructuur wordt getoetst.", [blocks[0].Id])], false, []);
        }
    }
}

internal sealed class MemoryImportStore : IImportPersistence
{
    public ImportRecord? Record { get; set; }
    public ImportWork? Work { get; set; }
    public int Dispatches { get; private set; }
    public bool FailDispatch { get; init; }
    public Dictionary<string, byte[]> Documents { get; } = [];
    private int version;
    public Task<ImportRecord?> ReadAsync(string id, CancellationToken cancellationToken) => Task.FromResult(Record?.Id == id ? Record : null);
    public Task<ImportRecord> WriteAsync(ImportRecord record, string? revision, CancellationToken cancellationToken)
    {
        if (Record is not null && Record.Revision != revision) throw new RequestFailedException(412, "Stale synthetic revision.");
        Record = record with { Revision = $"\"v{++version}\"" };
        return Task.FromResult(Record);
    }
    public Task EnqueueAsync(string id, CancellationToken cancellationToken)
    {
        if (FailDispatch) throw new RequestFailedException(503, "Synthetic queue outage.");
        Dispatches++;
        return Task.CompletedTask;
    }
    public async Task UploadAsync(string id, Stream stream, string contentType, string role, ImportMetadata metadata, string importId, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, cancellationToken);
        Documents[id] = bytes.ToArray();
    }
    public Task<ImportWork?> ReadWorkAsync(string id, CancellationToken cancellationToken) => Task.FromResult(Work);
    public Task WriteWorkAsync(string id, ImportWork work, CancellationToken cancellationToken) { Work = work; return Task.CompletedTask; }
    public Task<ImportRecord[]> ListAsync(CancellationToken cancellationToken) => Task.FromResult(Record is null ? Array.Empty<ImportRecord>() : [Record]);
    public Task<DocumentDownload?> DownloadAsync(string id, CancellationToken cancellationToken) => Task.FromResult(
        Documents.TryGetValue(id, out var bytes) ? new DocumentDownload(new MemoryStream(bytes), "application/pdf", new ETag("\"document\""), DateTimeOffset.UtcNow) : null);
    public Task<ImportRecord> PublishAsync(ImportRecord record, CancellationToken cancellationToken) => WriteAsync(record, record.Revision, cancellationToken);
    public Task<CatalogResponse> CatalogAsync(string subjectId, CancellationToken cancellationToken) => Task.FromResult(
        Record?.State == "published" ? new CatalogResponse([new("biology", "Biologie")], Record.Topics, [],
            [new(Record.Id, "biology", "havo", 2025, 1, $"/api/documents/{Record.ExamDocumentId}", $"/api/documents/{Record.AnswerDocumentId}")]) :
            new CatalogResponse([new("biology", "Biologie")], [], [], []));
}
