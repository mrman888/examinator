using System.Buffers.Binary;
using System.Text.Json;
using Azure;

namespace Examinator.Api;

public interface IExamImports
{
    Task<ImportRecord> CreateAsync(IFormCollection form, CancellationToken cancellationToken);
    Task<ImportRecord> GetAsync(string id, CancellationToken cancellationToken);
    Task<ImportRecord[]> ListAsync(CancellationToken cancellationToken);
    Task<ImportRecord> UpdateAsync(string id, string action, object update, CancellationToken cancellationToken);
    Task<DocumentDownload?> DocumentAsync(string id, CancellationToken cancellationToken);
    Task<CatalogResponse> CatalogAsync(string subjectId, CancellationToken cancellationToken);
}

public sealed class ExamImports(IImportPersistence storage, ILogger<ExamImports> logger) : IExamImports
{
    public async Task<ImportRecord> CreateAsync(IFormCollection form, CancellationToken cancellationToken)
    {
        ImportMetadata? metadata;
        try { metadata = JsonSerializer.Deserialize<ImportMetadata>(form["metadata"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { throw new ImportProblem(400, "Ongeldige importmetadata."); }
        if (!ImportValidation.ValidUploadMetadata(metadata) || metadata!.Topics.Length == 0)
            throw new ImportProblem(400, "Geef Biologie, HAVO en geldige onderwerpdefinities op.");
        if (form.Files.Count is < 2 or > 3 || form.Files.Any(file => file.Name is not ("exam" or "answers" or "context")) ||
            form.Files.GroupBy(file => file.Name).Any(group => group.Count() > 1))
            throw new ImportProblem(400, "Upload een examen-PDF, antwoord-PDF en eventueel een screenshot.");
        var exam = form.Files.GetFile("exam") ?? throw new ImportProblem(400, "Het examendocument ontbreekt.");
        var answers = form.Files.GetFile("answers") ?? throw new ImportProblem(400, "Het antwoorddocument ontbreekt.");
        var context = form.Files.GetFile("context");
        var contextText = form["contextText"].ToString();
        if (contextText.Length > 12_000) throw new ImportProblem(400, "De hoofdstuktekst is te lang.");
        await ValidateFileAsync(exam, false, cancellationToken);
        await ValidateFileAsync(answers, false, cancellationToken);
        string? contextType = context is null ? null : await ValidateFileAsync(context, true, cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var record = new ImportRecord(id, "queued", metadata, metadata.Topics, [], [], null, "",
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            context is null ? null : Guid.NewGuid().ToString("N"), contextText);
        await using (var stream = exam.OpenReadStream())
            await storage.UploadAsync(record.ExamDocumentId, stream, "application/pdf", "exam", metadata, id, cancellationToken);
        await using (var stream = answers.OpenReadStream())
            await storage.UploadAsync(record.AnswerDocumentId, stream, "application/pdf", "answers", metadata, id, cancellationToken);
        if (context is not null)
        {
            await using var stream = context.OpenReadStream();
            await storage.UploadAsync(record.ContextDocumentId!, stream, contextType!, "context", metadata, id, cancellationToken);
        }
        record = await storage.WriteAsync(record, null, cancellationToken);
        return await DispatchAsync(record, cancellationToken);
    }

    public Task<ImportRecord[]> ListAsync(CancellationToken cancellationToken) => storage.ListAsync(cancellationToken);

    public async Task<ImportRecord> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ImportProblem(404, "De import bestaat niet.");
        return await storage.ReadAsync(id, cancellationToken) ?? throw new ImportProblem(404, "De import bestaat niet.");
    }

    public async Task<ImportRecord> UpdateAsync(string id, string action, object update, CancellationToken cancellationToken)
    {
        var record = await GetAsync(id, cancellationToken);
        var work = await storage.ReadWorkAsync(id, cancellationToken);
        ImportRecord next;
        try
        {
            next = action switch
            {
                "draft" when update is DraftUpdate draft && work is not null =>
                    ImportReview.SaveDraft(record, draft, work.Inventory, work.Documents),
                "confirm-definitions" when update is DefinitionsUpdate definitions =>
                    ImportReview.ConfirmDefinitions(record, definitions),
                "publish" when update is RevisionUpdate revision && work is not null =>
                    ImportReview.Publish(record, revision, work.Inventory, work.Documents),
                "cancel" when update is RevisionUpdate revision => ImportReview.Cancel(record, revision),
                "retry" when update is RevisionUpdate revision => ImportReview.Retry(record, revision),
                _ => throw new ImportProblem(409, "Deze actie is niet beschikbaar voor deze import.")
            };
        }
        catch (InvalidOperationException) { throw new ImportProblem(409, "De importstatus of versie is gewijzigd. Vernieuw de gegevens."); }
        catch (InvalidDataException) { throw new ImportProblem(400, "De vragen, onderwerpdefinities, documentidentiteit of antwoordkoppelingen zijn nog niet geldig."); }
        if (action == "publish") return await storage.PublishAsync(next, cancellationToken);
        if (action == "retry" && work is not null)
            await storage.WriteWorkAsync(id, work with { PendingCall = null }, cancellationToken);
        if (action == "confirm-definitions" && work is not null)
            await storage.WriteWorkAsync(id, work with
            {
                ContextPrepared = true,
                ApprovedTopics = next.Topics,
                PendingCall = work.PendingCall == "chapter-definitions" ? null : work.PendingCall
            }, cancellationToken);
        next = await storage.WriteAsync(next, record.Revision, cancellationToken);
        if (action is "retry" or "confirm-definitions") next = await DispatchAsync(next, cancellationToken);
        return next;
    }

    public Task<DocumentDownload?> DocumentAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ImportProblem(404, "Het document bestaat niet.");
        return storage.DownloadAsync(id, cancellationToken);
    }

    public Task<CatalogResponse> CatalogAsync(string subjectId, CancellationToken cancellationToken) =>
        storage.CatalogAsync(subjectId, cancellationToken);

    private async Task<ImportRecord> DispatchAsync(ImportRecord record, CancellationToken cancellationToken)
    {
        try { await storage.EnqueueAsync(record.Id, cancellationToken); }
        catch (RequestFailedException)
        {
            logger.LogWarning("Import {ImportId} is durable but dispatch failed; reconciliation will retry.", record.Id);
            return await storage.WriteAsync(record with
            {
                Warnings = record.Warnings.Append("De import is opgeslagen, maar wachtrijverzending is vertraagd. De hersteljob probeert dit opnieuw.").Distinct().ToArray()
            }, record.Revision, cancellationToken);
        }
        return record;
    }

    private static async Task<string> ValidateFileAsync(IFormFile file, bool image, CancellationToken cancellationToken)
    {
        if (file.Length is < 5 || file.Length > (image ? 5 * 1024 * 1024 : PdfExtraction.MaximumFileBytes))
            throw new ImportProblem(413, "Maximaal 20 MiB per PDF en 5 MiB per screenshot.");
        await using var stream = file.OpenReadStream();
        var header = new byte[Math.Min(file.Length, image ? 1024 * 1024 : 5)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!image)
        {
            if (!header.AsSpan().SequenceEqual("%PDF-"u8)) throw new ImportProblem(400, "Het bestand is geen PDF.");
            return "application/pdf";
        }
        int width = 0, height = 0;
        var png = header.Length >= 24 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (png)
        {
            width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
        }
        else if (header[0] == 255 && header[1] == 216)
        {
            for (var offset = 2; offset + 9 < header.Length;)
            {
                if (header[offset] != 255) break;
                var marker = header[offset + 1];
                if (marker == 255) { offset++; continue; }
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(offset + 2, 2));
                if (length < 2 || offset + 2 + length > header.Length) break;
                if (marker is 192 or 193 or 194)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(offset + 5, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(offset + 7, 2));
                    break;
                }
                offset += 2 + length;
            }
        }
        if (width < 1 || height < 1 || width > 10_000 || height > 10_000 || (long)width * height > 20_000_000)
            throw new ImportProblem(400, "Gebruik een geldige PNG/JPEG-screenshot van maximaal 20 megapixels.");
        return png ? "image/png" : "image/jpeg";
    }
}
