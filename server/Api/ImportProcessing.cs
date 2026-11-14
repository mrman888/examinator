using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs.Models;

namespace Examinator.Api;

public interface IPdfRenderer
{
    Task<PageImage[]> RenderAsync(Stream stream, int pages, CancellationToken cancellationToken);
}

public sealed class PdfRenderer : IPdfRenderer
{
    public async Task<PageImage[]> RenderAsync(Stream stream, int pages, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "examinator-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.pdf");
            await using (var file = File.Create(input)) await stream.CopyToAsync(file, cancellationToken);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("pdftoppm")
                {
                    UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
                    ArgumentList = { "-f", "1", "-l", pages.ToString(), "-scale-to", "1200", "-png", input, Path.Combine(directory, "page") }
                }
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            if (!process.Start()) throw new InvalidDataException("Het renderen van de PDF kon niet starten.");
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                throw;
            }
            await Task.WhenAll(stderr, stdout);
            if (process.ExitCode != 0) throw new InvalidDataException("Het renderen van de PDF-pagina's is mislukt.");
            var files = Directory.GetFiles(directory, "page-*.png").Order(StringComparer.Ordinal).ToArray();
            if (files.Length != pages || files.Sum(file => new FileInfo(file).Length) > 64L * 1024 * 1024)
                throw new InvalidDataException("De gerenderde PDF-pagina's overschrijden de toegestane limieten.");
            var images = new List<PageImage>();
            foreach (var path in files)
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path).Split('-').Last(), out var page))
                    throw new InvalidDataException("Het renderen van de PDF gaf onverwachte uitvoer.");
                images.Add(new PageImage(page, "data:image/png;base64," +
                    Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken))));
            }
            return images.OrderBy(image => image.Page).ToArray();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

/// <summary>
/// Runs one leased processing attempt. Live progress is reported through the optional reporter (worker only);
/// it never writes the import record and its failures never fail the import.
/// </summary>
public sealed class ImportProcessor(IImportPersistence storage, IImportModel model, IPdfRenderer renderer,
    IConfiguration configuration, ILogger<ImportProcessor> logger, ImportProgressReporter? progress = null)
{
    public const int MaximumProcessingAttempts = 10;

    public async Task ProcessAsync(string id, CancellationToken cancellationToken)
    {
        var record = await storage.ReadAsync(id, cancellationToken);
        if (record is null || record.State is "review" or "published" or "awaitingDefinitions" or "cancelled" or "failed") return;
        try
        {
            if (record.ProcessingAttempts >= MaximumProcessingAttempts)
                throw new InvalidDataException("De achtergrondtaak is te vaak opnieuw gestart. Controleer de infrastructuur voordat u bewust opnieuw verwerkt.");
            record = await storage.WriteAsync(record with { ProcessingAttempts = record.ProcessingAttempts + 1 },
                record.Revision, cancellationToken);
            var work = await storage.ReadWorkAsync(id, cancellationToken);
            var attempt = record.ProcessingAttempts;
            var spent = work?.ModelCalls ?? 0;
            await Report(reporter => reporter.StartAsync(id, attempt, spent, ModelCallBudget, cancellationToken));
            if (work?.PendingCall is not null)
                throw new InvalidDataException("Een modelaanroep is onderbroken. Kies bewust opnieuw verwerken; dit kan extra modelkosten veroorzaken.");
            if (work is not null && (work.PromptVersion != ImportModel.PromptVersion ||
                work.ModelDeployment != configuration["Foundry:ImportDeployment"]))
                throw new InvalidDataException("Het model of de instructieversie is gewijzigd. Maak een nieuwe import; bestaande resultaten kunnen niet worden hergebruikt.");
            if (work is null)
            {
                record = await SaveState(record, "extracting", cancellationToken);
                await Report(reporter => reporter.PhaseAsync("extracting", "Examen-PDF uitlezen (1 van 2)", cancellationToken, 1, 2, "document"));
                var exam = await Extract(record.ExamDocumentId, cancellationToken);
                await Report(reporter => reporter.StepAsync("extracting", 2, 2, "document", "Correctievoorschrift uitlezen (2 van 2)", cancellationToken));
                var answers = await Extract(record.AnswerDocumentId, cancellationToken);
                work = new ImportWork(exam, answers, [], new(exam.Pages.Length, answers.Pages.Length, false, false),
                    Blocks(exam, "exam").Concat(Blocks(answers, "answers")).ToArray(), 0, 0,
                    ImportModel.PromptVersion, configuration["Foundry:ImportDeployment"]!);
                await storage.WriteWorkAsync(id, work, cancellationToken);
            }
            if (!work.ContextPrepared && (record.ContextDocumentId is not null || !string.IsNullOrWhiteSpace(record.ContextText)))
            {
                string? context = null;
                if (record.ContextDocumentId is not null)
                {
                    var document = await storage.DownloadAsync(record.ContextDocumentId, cancellationToken)
                        ?? throw new InvalidDataException("De hoofdstukscreenshot ontbreekt.");
                    await using var stream = document.Stream;
                    using var bytes = new MemoryStream();
                    await stream.CopyToAsync(bytes, cancellationToken);
                    context = $"data:{document.ContentType};base64,{Convert.ToBase64String(bytes.ToArray())}";
                }
                await Report(reporter => reporter.PhaseAsync("definitions", "Hoofdstukdefinities voorstellen", cancellationToken));
                work = await BeginCall(id, work, "chapter-definitions", cancellationToken);
                var topics = await model.PrepareTopicsAsync(record, context, cancellationToken);
                record = await storage.WriteAsync(record with { Topics = topics, State = "awaitingDefinitions" }, record.Revision, cancellationToken);
                await storage.WriteWorkAsync(id, work with { ContextPrepared = true, PendingCall = null }, cancellationToken);
                await Report(reporter => reporter.CompleteAsync("awaitingDefinitions", cancellationToken));
                return;
            }
            record = await SaveState(record, "aligning", cancellationToken);
            if (work.ApprovedTopics is null)
            {
                work = work with { ApprovedTopics = record.Topics };
                await storage.WriteWorkAsync(id, work, cancellationToken);
            }
            var examPages = work.Exam.Pages.Length;
            var answerPages = work.Answers.Pages.Length;
            await Report(reporter => reporter.PhaseAsync("rendering", $"Pagina's renderen: examen ({Pages(examPages)})",
                cancellationToken, 1, 2, "document"));
            var examImages = await Render(record.ExamDocumentId, examPages, "exam", cancellationToken);
            await Report(reporter => reporter.StepAsync("rendering", 2, 2, "document",
                $"Pagina's renderen: correctievoorschrift ({Pages(answerPages)})", cancellationToken));
            var answerImages = await Render(record.AnswerDocumentId, answerPages, "answers", cancellationToken);
            if (work.ExamInventory is null)
            {
                await Report(reporter => reporter.PhaseAsync("inventory", "Vragen inventariseren in het examen", cancellationToken, 1, 2, "document"));
                work = await BeginCall(id, work, "exam-inventory", cancellationToken);
                var inventory = await model.InventoryAsync(record.Metadata, work.Exam, examImages, "exam", cancellationToken);
                work = work with { ExamInventory = inventory, PendingCall = null };
                await storage.WriteWorkAsync(id, work, cancellationToken);
            }
            if (work.AnswerInventory is null)
            {
                await Report(reporter => reporter.StepAsync("inventory", 2, 2, "document",
                    "Vragen inventariseren in het correctievoorschrift", cancellationToken));
                work = await BeginCall(id, work, "answer-inventory", cancellationToken);
                var inventory = await model.InventoryAsync(record.Metadata, work.Answers, answerImages, "answers", cancellationToken);
                work = work with { AnswerInventory = inventory, PendingCall = null };
                await storage.WriteWorkAsync(id, work, cancellationToken);
            }
            var found = work.ExamInventory?.QuestionCount ?? 0;
            await Report(reporter => reporter.NoteAsync(found == 1 ? "1 vraag gevonden" : $"{found} vragen gevonden", cancellationToken));
            if (work.ExamInventory is null || work.AnswerInventory is null ||
                work.ExamInventory.SubjectId != record.Metadata.SubjectId ||
                work.AnswerInventory.SubjectId != record.Metadata.SubjectId ||
                work.ExamInventory.Level != record.Metadata.Level ||
                work.AnswerInventory.Level != record.Metadata.Level ||
                work.ExamInventory.Role != "exam" ||
                work.AnswerInventory.Role != "answers" ||
                work.ExamInventory.Year is < 2000 or > 2100 ||
                work.ExamInventory.Timevak is < 1 or > 3 ||
                (record.Metadata.Year != 0 && record.Metadata.Year != work.ExamInventory.Year) ||
                (record.Metadata.Timevak != 0 && record.Metadata.Timevak != work.ExamInventory.Timevak) ||
                work.AnswerInventory.Year != work.ExamInventory.Year ||
                work.AnswerInventory.Timevak != work.ExamInventory.Timevak ||
                !string.Equals(work.ExamInventory.ExamCode, work.AnswerInventory.ExamCode, StringComparison.OrdinalIgnoreCase) ||
                work.ExamInventory.QuestionCount != work.AnswerInventory.QuestionCount)
                throw new InvalidDataException("De documentidentiteit, examencodes of aantallen vragen van het examen en antwoorddocument komen niet overeen.");
            if (record.Metadata.Year != work.ExamInventory.Year || record.Metadata.Timevak != work.ExamInventory.Timevak)
            {
                record = await storage.WriteAsync(record with
                {
                    Metadata = record.Metadata with
                    {
                        Year = work.ExamInventory.Year,
                        Timevak = work.ExamInventory.Timevak
                    }
                }, record.Revision, cancellationToken);
            }
            await Report(reporter => reporter.PhaseAsync("aligning", "Antwoorden koppelen", cancellationToken));
            if (work.Inventory.Length == 0)
            {
                var questions = QuestionAlignment.Align(record.Metadata, work.ExamInventory.Questions,
                    work.AnswerInventory.Questions, work.Exam.Pages.Length, work.Answers.Pages.Length);
                work = work with { Inventory = questions, Documents = work.Documents with { ExamIdentityVerified = true, AnswerIdentityVerified = true } };
                await storage.WriteWorkAsync(id, work, cancellationToken);
            }
            if (record.Questions.Length == 0)
                record = await storage.WriteAsync(record with { Questions = work.Inventory }, record.Revision, cancellationToken);
            var aligned = work.Inventory.Count(question => question.AnswerPage is not null);
            await Report(reporter => reporter.NoteAsync($"{aligned} van {work.Inventory.Length} antwoorden gekoppeld", cancellationToken));
            record = await SaveState(record, "tagging", cancellationToken);
            for (var index = work.ClassifiedCount; index < record.Questions.Length; index++)
            {
                var current = await storage.ReadAsync(id, cancellationToken);
                if (current?.State == "cancelled")
                {
                    await Report(reporter => reporter.CompleteAsync("cancelled", cancellationToken));
                    return;
                }
                var question = record.Questions[index];
                var blocks = work.Blocks.Where(block =>
                    block.Id.StartsWith("exam:", StringComparison.Ordinal) && block.Page >= question.ExamPage && block.Page <= question.ExamEndPage ||
                    block.Id.StartsWith("answers:", StringComparison.Ordinal) && block.Page >= question.AnswerPage && block.Page <= question.AnswerEndPage).ToArray();
                var images = examImages.Where(image => image.Page >= question.ExamPage && image.Page <= question.ExamEndPage)
                    .Concat(answerImages.Where(image => image.Page >= question.AnswerPage && image.Page <= question.AnswerEndPage)).ToArray();
                var step = index + 1;
                var total = record.Questions.Length;
                await Report(reporter => reporter.StepAsync("tagging", step, total, "vraag",
                    $"Vraag {step} van {total}: onderwerpen toekennen", cancellationToken));
                work = await BeginCall(id, work, $"question-{question.Number}", cancellationToken);
                var result = await model.ClassifyAsync(question, record.Topics, blocks, images, cancellationToken);
                var questions = record.Questions.ToArray();
                questions[index] = question with
                {
                    TopicIds = result.Topics.Select(match => match.TopicId).ToArray(),
                    Reasons = result.Topics.Select(match => match.Reason).ToArray(),
                    NeedsReview = true,
                    Warnings = question.Warnings.Concat(result.Warnings).Distinct().ToArray()
                };
                record = await storage.WriteAsync(record with { Questions = questions }, record.Revision, cancellationToken);
                work = work with
                {
                    ClassifiedCount = index + 1, PendingCall = null,
                    Classifications = (work.Classifications ?? []).Append(result).ToArray()
                };
                await storage.WriteWorkAsync(id, work, cancellationToken);
            }
            await Report(reporter => reporter.PhaseAsync("finishing", "Resultaten opslaan voor beoordeling", cancellationToken));
            await SaveState(record, "review", cancellationToken);
            await Report(reporter => reporter.CompleteAsync("review", cancellationToken));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or HttpRequestException or
            System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException or
            System.ComponentModel.Win32Exception or RequestFailedException)
        {
            logger.LogError(exception, "Import {ImportId} failed with {ErrorType}.", id, exception.GetType().Name);
            var latest = await storage.ReadAsync(id, CancellationToken.None);
            var error = exception is InvalidDataException ? exception.Message :
                "De verwerking is onderbroken. Controleer document/configuratie en kies bewust opnieuw verwerken; dit kan modelkosten veroorzaken.";
            if (latest is not null && latest.State is not ("cancelled" or "published" or "review"))
                await storage.WriteAsync(latest with { State = "failed", Error = error }, latest.Revision, CancellationToken.None);
            var outcome = latest?.State switch { null or "published" or "review" => null, "cancelled" => "cancelled", _ => "failed" };
            if (outcome is not null) await Report(reporter => reporter.CompleteAsync(outcome, CancellationToken.None, error));
        }
        finally
        {
            await Report(reporter => reporter.FlushAsync(CancellationToken.None));
        }
    }

    private int ModelCallBudget => configuration.GetValue("Import:MaximumModelCalls", 64);

    private static string Pages(int count) => count == 1 ? "1 pagina" : $"{count} pagina's";

    /// <summary>Progress is best effort: the reporter swallows storage failures, and this guards everything else.</summary>
    private async Task Report(Func<ImportProgressReporter, Task> report)
    {
        if (progress is null) return;
        try { await report(progress); }
        catch (Exception exception)
        {
            logger.LogWarning("Import progress reporting failed with {ErrorType}.", exception.GetType().Name);
        }
    }

    private async Task<ImportWork> BeginCall(string id, ImportWork work, string stage, CancellationToken ct)
    {
        var latest = await storage.ReadAsync(id, ct);
        if (latest?.State == "cancelled") throw new OperationCanceledException("The import was cancelled.", ct);
        if (work.ModelCalls >= ModelCallBudget) throw new InvalidDataException("Het ingestelde maximum aantal modelaanroepen voor deze import is bereikt.");
        work = work with { ModelCalls = work.ModelCalls + 1, PendingCall = stage };
        await storage.WriteWorkAsync(id, work, ct);
        progress?.ModelCalls(work.ModelCalls);
        return work;
    }

    private Task<ImportRecord> SaveState(ImportRecord record, string state, CancellationToken ct) =>
        storage.WriteAsync(record with { State = state }, record.Revision, ct);

    private async Task<PdfContent> Extract(string documentId, CancellationToken ct)
    {
        var document = await storage.DownloadAsync(documentId, ct) ?? throw new InvalidDataException("Een bron-PDF ontbreekt.");
        await using var stream = document.Stream;
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, ct);
        return PdfExtraction.Extract(bytes, ct);
    }

    private async Task<PageImage[]> Render(string documentId, int pages, string role, CancellationToken ct)
    {
        var document = await storage.DownloadAsync(documentId, ct) ?? throw new InvalidDataException("Een bron-PDF ontbreekt.");
        await using var stream = document.Stream;
        return (await renderer.RenderAsync(stream, pages, ct)).Select(image => image with { Role = role }).ToArray();
    }

    private static ImportSourceBlock[] Blocks(PdfContent content, string role) => content.Pages.Select(page =>
        new ImportSourceBlock($"{role}:p{page.Page}", page.Page,
            string.Join(" ", page.Words.OrderByDescending(word => Math.Round(word.Top / 4)).ThenBy(word => word.Left).Select(word => word.Text)))).ToArray();
}
