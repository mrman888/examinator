using System.Buffers.Binary;
using System.Text.Json;
using Azure;

namespace Examinator.Api;

public interface IExamImports
{
    Task<ImportRecord> CreateAsync(IFormCollection form, CancellationToken cancellationToken);
    Task<ImportRecord> ReprocessAsync(string sourceId, IFormCollection form, CancellationToken cancellationToken);
    Task<ImportRecord> GetAsync(string id, CancellationToken cancellationToken);
    Task<ImportRecord[]> ListAsync(CancellationToken cancellationToken);
    Task<ImportRecord> UpdateAsync(string id, string action, object update, CancellationToken cancellationToken);
    Task DeleteAsync(string id, CancellationToken cancellationToken);
    Task<DocumentDownload?> DocumentAsync(string id, CancellationToken cancellationToken);
    Task<CatalogResponse> CatalogAsync(string subjectId, CancellationToken cancellationToken);
}

public sealed class ExamImports(IImportPersistence storage, IPdfDownloader downloader, ILogger<ExamImports> logger) : IExamImports
{
    public async Task<ImportRecord> CreateAsync(IFormCollection form, CancellationToken cancellationToken)
    {
        var metadata = ParseMetadata(form);
        if (form.Files.Any(file => file.Name is not ("exam" or "answers" or "context")) ||
            form.Files.GroupBy(file => file.Name).Any(group => group.Count() > 1) ||
            form["examUrl"].Count > 1 || form["answersUrl"].Count > 1)
            throw new ImportProblem(400, "Geef een examen-PDF, antwoord-PDF en eventueel een screenshot op.");
        var context = form.Files.GetFile("context");
        var contextText = form["contextText"].ToString();
        if (contextText.Length > 12_000) throw new ImportProblem(400, "De hoofdstuktekst is te lang.");
        var exam = await ResolvePdfAsync(form, "exam", "Het examendocument", cancellationToken);
        var answers = await ResolvePdfAsync(form, "answers", "Het antwoorddocument", cancellationToken);
        string? contextType = context is null ? null : await ValidateFileAsync(context, true, cancellationToken);
        await RejectDuplicatesAsync([("exam", exam.Sha256), ("answers", answers.Sha256)], cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var record = new ImportRecord(id, "queued", metadata, metadata.Topics, [], [], null, "",
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            context is null ? null : Guid.NewGuid().ToString("N"), contextText);
        await using (var stream = exam.Open())
            await storage.UploadAsync(record.ExamDocumentId, stream, "application/pdf", "exam", metadata, id, cancellationToken);
        await using (var stream = answers.Open())
            await storage.UploadAsync(record.AnswerDocumentId, stream, "application/pdf", "answers", metadata, id, cancellationToken);
        if (context is not null)
        {
            await using var stream = context.OpenReadStream();
            await storage.UploadAsync(record.ContextDocumentId!, stream, contextType!, "context", metadata, id, cancellationToken);
        }
        record = await storage.WriteAsync(record, null, cancellationToken);
        return await DispatchAsync(record, cancellationToken);
    }

    public async Task<ImportRecord> ReprocessAsync(string sourceId, IFormCollection form, CancellationToken cancellationToken)
    {
        var source = await GetAsync(sourceId, cancellationToken);
        var metadata = ParseMetadata(form);
        if (form.Files.Count > 1 || form.Files.Any(file => file.Name != "context"))
            throw new ImportProblem(400, "Alleen een screenshot kan worden toegevoegd bij opnieuw verwerken.");
        var contextText = form["contextText"].ToString();
        if (contextText.Length > 12_000) throw new ImportProblem(400, "De hoofdstuktekst is te lang.");
        foreach (var documentId in new[] { source.ExamDocumentId, source.AnswerDocumentId })
        {
            var document = await storage.DownloadAsync(documentId, cancellationToken)
                ?? throw new ImportProblem(409, "Het bronbestand van deze import ontbreekt.");
            await document.Stream.DisposeAsync();
        }
        var context = form.Files.GetFile("context");
        string? contextType = context is null ? null : await ValidateFileAsync(context, true, cancellationToken);
        metadata = metadata with { Year = source.Metadata.Year, Timevak = source.Metadata.Timevak };
        var id = Guid.NewGuid().ToString("N");
        var record = new ImportRecord(id, "queued", metadata, metadata.Topics, [], [], null, "",
            source.ExamDocumentId, source.AnswerDocumentId, DateTimeOffset.UtcNow,
            context is null ? null : Guid.NewGuid().ToString("N"), contextText);
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

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var record = await GetAsync(id, cancellationToken);
        if (record.State == "published")
            throw new ImportProblem(409, "Een gepubliceerde import kan niet worden verwijderd.");
        if (record.State is "extracting" or "aligning" or "tagging" || await storage.IsLeasedAsync(id, cancellationToken))
            throw new ImportProblem(409, "Deze import wordt verwerkt en kan nu niet worden verwijderd. Probeer het later opnieuw.");
        try { await storage.DeleteAsync(record, cancellationToken); }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            throw new ImportProblem(409, "De import is gewijzigd. Vernieuw de gegevens voordat je verwijdert.");
        }
        var remaining = (await storage.ListAllAsync(cancellationToken)).Where(other => other.Id != id).ToArray();
        var documentIds = new[] { record.ExamDocumentId, record.AnswerDocumentId, record.ContextDocumentId }
            .OfType<string>().Distinct();
        foreach (var documentId in documentIds)
        {
            var survivor = remaining.FirstOrDefault(other => other.ExamDocumentId == documentId ||
                other.AnswerDocumentId == documentId || other.ContextDocumentId == documentId);
            await storage.ReleaseDocumentAsync(documentId, id, survivor?.Id, cancellationToken);
        }
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

    private static ImportMetadata ParseMetadata(IFormCollection form)
    {
        ImportMetadata? metadata;
        try { metadata = JsonSerializer.Deserialize<ImportMetadata>(form["metadata"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { throw new ImportProblem(400, "Ongeldige importmetadata."); }
        if (!ImportValidation.ValidUploadMetadata(metadata) || metadata!.Topics.Length == 0)
            throw new ImportProblem(400, "Geef Biologie, HAVO en geldige onderwerpdefinities op.");
        return metadata;
    }

    private async Task RejectDuplicatesAsync((string Role, string Sha256)[] items, CancellationToken cancellationToken)
    {
        var duplicates = new List<DuplicateImport>();
        ImportRecord[]? all = null;
        foreach (var (role, sha) in items)
        {
            var entry = await storage.FindHashAsync(sha, cancellationToken);
            if (entry is null) continue;
            all ??= await storage.ListAsync(cancellationToken);
            var matches = all.Where(r => r.ExamDocumentId == entry.DocumentId || r.AnswerDocumentId == entry.DocumentId).ToList();
            if (matches.Count == 0 && await storage.ReadAsync(entry.ImportId, cancellationToken) is { } origin) matches.Add(origin);
            if (matches.Count == 0) duplicates.Add(new DuplicateImport(entry.ImportId, "unknown", 0, 0, role, entry.DocumentId));
            duplicates.AddRange(matches.Select(r => new DuplicateImport(r.Id, r.State, r.Metadata.Year, r.Metadata.Timevak, role, entry.DocumentId)));
        }
        if (duplicates.Count > 0) throw new ImportDuplicateProblem(duplicates.ToArray());
    }

    private static async Task<string> HashAsync(Func<Stream> open, CancellationToken cancellationToken)
    {
        await using var stream = open();
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private sealed record PdfSource(Func<Stream> Open, string Sha256);

    private async Task<PdfSource> ResolvePdfAsync(IFormCollection form, string name, string label, CancellationToken cancellationToken)
    {
        var file = form.Files.GetFile(name);
        var url = form[$"{name}Url"].ToString().Trim();
        if (file is not null && url.Length > 0) throw new ImportProblem(400, $"{label}: kies een bestand of een link, niet beide.");
        if (file is not null)
        {
            await ValidateFileAsync(file, false, cancellationToken);
            return new PdfSource(file.OpenReadStream, await HashAsync(file.OpenReadStream, cancellationToken));
        }
        if (url.Length == 0) throw new ImportProblem(400, $"{label} ontbreekt.");
        var bytes = await downloader.DownloadAsync(url, cancellationToken);
        if (bytes.Length is < 5 || bytes.Length > PdfExtraction.MaximumFileBytes)
            throw new ImportProblem(413, "Maximaal 20 MiB per PDF en 5 MiB per screenshot.");
        if (!bytes.AsSpan().StartsWith("%PDF-"u8)) throw new ImportProblem(400, "Het bestand is geen PDF.");
        return new PdfSource(() => new MemoryStream(bytes, writable: false),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
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
