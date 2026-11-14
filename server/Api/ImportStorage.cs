using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Azure.Data.Tables;
using Azure.Storage.Blobs.Specialized;
using System.Security.Cryptography;

namespace Examinator.Api;

/// <summary>
/// Conditional writes use the blob ETag as the revision. Queue messages are only
/// dispatch hints; callers must retain and reconcile queued records after failures.
/// </summary>
public sealed class ImportStorage : IImportPersistence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly BlobContainerClient records;
    private readonly BlobContainerClient documents;
    private readonly QueueClient queue;
    private readonly QueueClient? poison;
    private readonly BlobContainerClient? results;
    private readonly TableClient? jobs;
    private readonly TableClient? catalog;

    /// <summary>The clock for <see cref="ImportRecord.UpdatedAt"/>; replaceable in tests.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;
    public QueueClient Queue => queue;
    public QueueClient Poison => poison ?? throw new InvalidOperationException("The poison queue is not configured.");
    public BlobClient Lock(string id) => records.GetBlobClient($"locks/{id}");

    public ImportStorage(Uri blobEndpoint, Uri queueEndpoint, Uri tableEndpoint, TokenCredential credential)
        : this(blobEndpoint, queueEndpoint, credential)
    {
        if (tableEndpoint.Scheme != "https") throw new ArgumentException("Storage endpoints must use HTTPS.");
        results = new BlobServiceClient(blobEndpoint, credential).GetBlobContainerClient("exam-results");
        var tables = new TableServiceClient(tableEndpoint, credential);
        jobs = tables.GetTableClient("ImportJobs");
        catalog = tables.GetTableClient("ExamCatalog");
    }

    public ImportStorage(Uri blobEndpoint, Uri queueEndpoint, TokenCredential credential)
    {
        if (blobEndpoint.Scheme != Uri.UriSchemeHttps || queueEndpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Storage endpoints must use HTTPS.");
        records = new BlobServiceClient(blobEndpoint, credential).GetBlobContainerClient("exam-imports");
        documents = new BlobServiceClient(blobEndpoint, credential).GetBlobContainerClient("exam-documents");
        queue = new QueueServiceClient(queueEndpoint, credential).GetQueueClient("exam-imports");
        poison = new QueueServiceClient(queueEndpoint, credential).GetQueueClient("exam-imports-poison");
    }

    internal ImportStorage(BlobContainerClient records, BlobContainerClient documents, QueueClient queue,
        BlobContainerClient? results = null, TableClient? catalog = null, TableClient? jobs = null)
    {
        this.records = records;
        this.documents = documents;
        this.queue = queue;
        this.results = results;
        this.catalog = catalog;
        this.jobs = jobs;
    }

    public async Task<ImportRecord?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        var blob = RecordBlob(id);
        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken);
            var record = JsonSerializer.Deserialize<ImportRecord>(response.Value.Content.ToMemory().Span, Json)
                ?? throw new InvalidDataException("Import record was empty.");
            return record with
            {
                Revision = response.Value.Details.ETag.ToString(),
                UpdatedAt = record.UpdatedAt ?? response.Value.Details.LastModified
            };
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<ImportRecord> WriteAsync(ImportRecord record, string? revision, CancellationToken cancellationToken)
        => await WriteCoreAsync(record, revision, null, cancellationToken);

    private async Task<ImportRecord> WriteCoreAsync(ImportRecord record, string? revision, string? leaseId, CancellationToken cancellationToken)
    {
        if (revision is null && jobs is not null)
            await jobs.UpsertEntityAsync(Index(record), cancellationToken: cancellationToken);
        var conditions = revision is null
            ? new BlobRequestConditions { IfNoneMatch = ETag.All }
            : new BlobRequestConditions { IfMatch = new ETag(revision), LeaseId = leaseId };
        // UpdatedAt is set on every write; Timing is response-only and never stored.
        record = record with { UpdatedAt = Time.GetUtcNow(), Timing = null };
        var payload = BinaryData.FromObjectAsJson(record with { Revision = "" }, Json);
        var response = await RecordBlob(record.Id).UploadAsync(payload,
            new BlobUploadOptions
            {
                Conditions = conditions,
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, cancellationToken);
        if (jobs is not null) await jobs.UpsertEntityAsync(Index(record), cancellationToken: cancellationToken);
        return record with { Revision = response.Value.ETag.ToString() };
    }

    public async Task EnqueueAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        await queue.SendMessageAsync(id, cancellationToken);
    }

    public BlobClient Document(string id)
    {
        ValidateId(id);
        return documents.GetBlobClient($"{id}/original.pdf");
    }

    public async Task UploadAsync(string id, Stream stream, string contentType, string role,
        ImportMetadata metadata, string importId, CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();
        var digest = Convert.ToHexString(await hash.ComputeHashAsync(stream, cancellationToken));
        stream.Position = 0;
        await Document(id).UploadAsync(stream, new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Metadata = new Dictionary<string, string>
            {
                ["subjectid"] = metadata.SubjectId, ["level"] = metadata.Level,
                ["year"] = metadata.Year.ToString(), ["timevak"] = metadata.Timevak.ToString(),
                ["role"] = role, ["importid"] = importId, ["sha256"] = digest
            }
        }, cancellationToken);
        await CatalogTable.UpsertEntityAsync(new TableEntity("documents", id)
        {
            ["ImportId"] = importId, ["SubjectId"] = metadata.SubjectId, ["Role"] = role
        }, cancellationToken: cancellationToken);
        if (role is "exam" or "answers")
        {
            try
            {
                await CatalogTable.AddEntityAsync(new TableEntity("sha256", digest.ToLowerInvariant())
                {
                    ["DocumentId"] = id, ["Role"] = role, ["ImportId"] = importId, ["FirstSeen"] = DateTimeOffset.UtcNow
                }, cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status == 409) { }
        }
    }

    public async Task<DocumentHashEntry?> FindHashAsync(string sha256, CancellationToken cancellationToken)
    {
        var key = sha256.ToLowerInvariant();
        var row = await CatalogTable.GetEntityIfExistsAsync<TableEntity>("sha256", key, cancellationToken: cancellationToken);
        if (!row.HasValue || row.Value is not { } value) return null;
        return new DocumentHashEntry(key, value.GetString("DocumentId")!, value.GetString("Role")!,
            value.GetString("ImportId")!, value.GetDateTimeOffset("FirstSeen") ?? default);
    }

    public async Task<ImportWork?> ReadWorkAsync(string id, CancellationToken cancellationToken) =>
        await ReadJsonAsync<ImportWork>(records.GetBlobClient($"work/{id}.json"), cancellationToken);

    public async Task WriteWorkAsync(string id, ImportWork work, CancellationToken cancellationToken) =>
        await records.GetBlobClient($"work/{id}.json").UploadAsync(BinaryData.FromObjectAsJson(work, Json),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } },
            cancellationToken);

    /// <summary>Unconditional write: the leased worker is the only progress writer.</summary>
    public async Task WriteProgressAsync(ImportProgress progress, CancellationToken cancellationToken) =>
        await ProgressBlob(progress.ImportId).UploadAsync(BinaryData.FromObjectAsJson(progress, Json),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } },
            cancellationToken);

    /// <summary>Returns null when the progress blob still matches <paramref name="ifNoneMatch"/> (HTTP 304).</summary>
    public Task<Versioned<ImportProgress>?> ReadProgressAsync(string id, string? ifNoneMatch, CancellationToken cancellationToken) =>
        ReadVersionedAsync<ImportProgress>(ProgressBlob(id), ifNoneMatch, cancellationToken);

    /// <summary>Returns null when the record still matches <paramref name="ifNoneMatch"/> (HTTP 304).</summary>
    public async Task<Versioned<ImportRecord>?> ReadIfChangedAsync(string id, string? ifNoneMatch, CancellationToken cancellationToken)
    {
        var result = await ReadVersionedAsync<ImportRecord>(RecordBlob(id), ifNoneMatch, cancellationToken);
        return result?.Value is { } record
            ? result with { Value = record with { Revision = result.ETag!, UpdatedAt = record.UpdatedAt ?? result.LastModified } }
            : result;
    }

    /// <summary>Import ids whose job index row is in a processing state (queued, extracting, aligning, tagging).</summary>
    public async Task<string[]> ListProcessingIdsAsync(CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await foreach (var entity in JobsTable.QueryAsync<TableEntity>(
            "PartitionKey eq 'imports' and (State eq 'queued' or State eq 'extracting' or State eq 'aligning' or State eq 'tagging')",
            maxPerPage: 100, select: ["ImportId"], cancellationToken: cancellationToken))
        {
            if (entity.GetString("ImportId") is { } id) ids.Add(id);
            if (ids.Count == 100) break;
        }
        return ids.ToArray();
    }

    public async Task<ImportRecord[]> ListAsync(CancellationToken cancellationToken)
    {
        var list = new List<ImportRecord>();
        await foreach (var entity in JobsTable.QueryAsync<TableEntity>("PartitionKey eq 'imports'",
            maxPerPage: 100, cancellationToken: cancellationToken))
        {
            var record = await ReadAsync(entity.GetString("ImportId")!, cancellationToken);
            if (record is not null) list.Add(record);
            if (list.Count == 100) break;
        }
        return list.OrderByDescending(record => record.CreatedAt).ThenBy(record => record.Id, StringComparer.Ordinal)
            .Take(100).ToArray();
    }

    public async Task<ImportRecord[]> ListAllAsync(CancellationToken cancellationToken)
    {
        var list = new List<ImportRecord>();
        await foreach (var entity in JobsTable.QueryAsync<TableEntity>("PartitionKey eq 'imports'",
            maxPerPage: 100, cancellationToken: cancellationToken))
        {
            var record = await ReadAsync(entity.GetString("ImportId")!, cancellationToken);
            if (record is not null) list.Add(record);
        }
        return list.OrderByDescending(record => record.CreatedAt).ThenBy(record => record.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<bool> IsLeasedAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        try
        {
            var properties = (await Lock(id).GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            return properties.LeaseState == LeaseState.Leased;
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return false; }
    }

    /// <summary>
    /// Removes the record (conditional on its revision, so a worker write wins), its job index row, work,
    /// progress and result data, the lock blob, an unpublished sitting reservation and queued dispatches.
    /// </summary>
    public async Task DeleteAsync(ImportRecord record, CancellationToken cancellationToken)
    {
        await RecordBlob(record.Id).DeleteAsync(DeleteSnapshotsOption.None,
            new BlobRequestConditions { IfMatch = new ETag(record.Revision) }, cancellationToken);
        if (jobs is not null)
        {
            var index = Index(record);
            await jobs.DeleteEntityAsync(index.PartitionKey, index.RowKey, cancellationToken: cancellationToken);
        }
        if (catalog is not null) await ReleaseReservationAsync(record, cancellationToken);
        await records.GetBlobClient($"work/{record.Id}.json").DeleteIfExistsAsync(cancellationToken: cancellationToken);
        await ProgressBlob(record.Id).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        if (results is not null)
            await results.GetBlobClient($"{record.Id}/manifest.json").DeleteIfExistsAsync(cancellationToken: cancellationToken);
        try { await Lock(record.Id).DeleteIfExistsAsync(cancellationToken: cancellationToken); }
        catch (RequestFailedException exception) when (exception.Status == 412) { }
        await DeleteQueuedAsync(record.Id, cancellationToken);
    }

    private async Task DeleteQueuedAsync(string id, CancellationToken cancellationToken)
    {
        var peeked = (await queue.PeekMessagesAsync(32, cancellationToken)).Value;
        if (!peeked.Any(message => message.MessageText == id)) return;
        foreach (var message in (await queue.ReceiveMessagesAsync(32, TimeSpan.FromSeconds(2), cancellationToken)).Value)
            if (message.MessageText == id) await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
    }

    /// <summary>
    /// Releases an original: the blob, its document row and its hash row are removed unless another import still
    /// references it, in which case the rows are repointed to that surviving import.
    /// </summary>
    public async Task ReleaseDocumentAsync(string documentId, string deletedImportId, string? survivorImportId,
        CancellationToken cancellationToken)
    {
        var blob = Document(documentId);
        string? digest = null;
        try
        {
            var properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            if (properties.Metadata.TryGetValue("sha256", out var value)) digest = value.ToLowerInvariant();
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { }
        var documentRow = (await CatalogTable.GetEntityIfExistsAsync<TableEntity>("documents", documentId,
            cancellationToken: cancellationToken)).Value;
        TableEntity? hashRow = null;
        if (digest is not null)
            hashRow = (await CatalogTable.GetEntityIfExistsAsync<TableEntity>("sha256", digest, cancellationToken: cancellationToken)).Value;
        if (survivorImportId is not null)
        {
            if (documentRow?.GetString("ImportId") == deletedImportId)
            {
                documentRow["ImportId"] = survivorImportId;
                await CatalogTable.UpdateEntityAsync(documentRow, documentRow.ETag, TableUpdateMode.Replace, cancellationToken);
            }
            if (hashRow?.GetString("DocumentId") == documentId && hashRow.GetString("ImportId") == deletedImportId)
            {
                hashRow["ImportId"] = survivorImportId;
                await CatalogTable.UpdateEntityAsync(hashRow, hashRow.ETag, TableUpdateMode.Replace, cancellationToken);
            }
            return;
        }
        if (hashRow?.GetString("DocumentId") == documentId)
            await CatalogTable.DeleteEntityAsync("sha256", digest!, hashRow.ETag, cancellationToken);
        if (documentRow is not null)
            await CatalogTable.DeleteEntityAsync("documents", documentId, documentRow.ETag, cancellationToken);
        await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    private async Task ReleaseReservationAsync(ImportRecord record, CancellationToken cancellationToken)
    {
        var partition = $"{record.Metadata.SubjectId}-{record.Metadata.Level}";
        var key = $"{record.Metadata.Year:D4}-{record.Metadata.Timevak}";
        var pointer = await CatalogTable.GetEntityIfExistsAsync<TableEntity>(partition, key, cancellationToken: cancellationToken);
        if (pointer.HasValue && pointer.Value is { } value && value.GetString("ImportId") == record.Id)
            await CatalogTable.DeleteEntityAsync(partition, key, value.ETag, cancellationToken);
    }

    public async Task<DocumentDownload?> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        var entry = await CatalogTable.GetEntityIfExistsAsync<TableEntity>("documents", id, cancellationToken: cancellationToken);
        if (!entry.HasValue || entry.Value is not { } value) return null;
        var import = await ReadAsync(value.GetString("ImportId")!, cancellationToken);
        if (import is null) return null;
        var blob = Document(id);
        var properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
        return new DocumentDownload(await blob.OpenReadAsync(cancellationToken: cancellationToken),
            properties.ContentType, properties.ETag, properties.LastModified);
    }

    public async Task<ImportRecord> PublishAsync(ImportRecord record, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = deadline.Token;
        var lease = RecordBlob(record.Id).GetBlobLeaseClient();
        await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
        try
        {
        var current = await ReadAsync(record.Id, cancellationToken);
        if (current is null || current.Revision != record.Revision || current.State != "review")
            throw new ImportProblem(409, "De import is gewijzigd. Vernieuw de gegevens.");
        var table = CatalogTable;
        var key = $"{record.Metadata.Year:D4}-{record.Metadata.Timevak}";
        var partition = $"{record.Metadata.SubjectId}-{record.Metadata.Level}";
        var snapshot = results!.GetBlobClient($"{record.Id}/manifest.json");
        try
        {
            await snapshot.UploadAsync(BinaryData.FromObjectAsJson(record with { State = "published", Timing = null }, Json),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
                }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            var frozen = await ReadJsonAsync<ImportRecord>(snapshot, cancellationToken);
            if (frozen?.Revision != record.Revision) throw new ImportProblem(409, "Een andere versie van deze import is al klaargezet voor publicatie.");
        }
        try
        {
            await table.AddEntityAsync(new TableEntity(partition, key) { ["ImportId"] = record.Id },
                cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            var existing = (await table.GetEntityAsync<TableEntity>(partition, key, cancellationToken: cancellationToken)).Value;
            if (existing.GetString("ImportId") != record.Id)
                throw new ImportProblem(409, "Dit examen is al gepubliceerd. Een bestaand examen wordt niet stilzwijgend vervangen.");
        }
        return await WriteCoreAsync(record with { State = "published" }, record.Revision, lease.LeaseId, cancellationToken);
        }
        finally { await lease.ReleaseAsync(cancellationToken: CancellationToken.None); }
    }

    public async Task<CatalogResponse> CatalogAsync(string subjectId, CancellationToken cancellationToken)
    {
        if (subjectId != "biology") throw new ImportProblem(400, "Dit vak is nog niet beschikbaar.");
        var published = new List<ImportRecord>();
        await foreach (var entity in CatalogTable.QueryAsync<TableEntity>("PartitionKey eq 'biology-havo'",
            cancellationToken: cancellationToken))
        {
            var id = entity.GetString("ImportId")!;
            var status = await ReadAsync(id, cancellationToken);
            if (status is null) throw new InvalidDataException("Het importrecord bij de catalogusverwijzing ontbreekt.");
            if (status.State != "published") continue;
            var record = await ReadJsonAsync<ImportRecord>(results!.GetBlobClient($"{id}/manifest.json"), cancellationToken)
                ?? throw new InvalidDataException("Published manifest is missing.");
            if (record.Questions is null || !ImportValidation.CanPublishQuestions(record.Questions))
                throw new InvalidDataException("De gepubliceerde vraaginventaris is leeg of onvolledig.");
            published.Add(record);
            if (published.Count >= 1000) throw new ImportProblem(503, "De catalogus vereist paginering boven 1000 examens.");
        }
        var topics = published.OrderBy(r => r.CreatedAt).SelectMany(r => r.Topics)
            .GroupBy(t => t.Id).Select(group => group.Last()).ToArray();
        return new CatalogResponse([new("biology", "Biologie")], topics,
            published.SelectMany(record => record.Questions.Select(q =>
                new CatalogQuestion(q.Id, q.TopicIds, q.Title, $"/api/documents/{record.ExamDocumentId}",
                    q.ExamPage, $"/api/documents/{record.AnswerDocumentId}", q.AnswerPage!.Value,
                    record.Metadata.Year, record.Metadata.Timevak, q.Number, record.Metadata.SubjectId))).ToArray(),
            published.Select(record => new CatalogExam(record.Id, record.Metadata.SubjectId, record.Metadata.Level,
                record.Metadata.Year, record.Metadata.Timevak, $"/api/documents/{record.ExamDocumentId}",
                $"/api/documents/{record.AnswerDocumentId}")).ToArray());
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in JobsTable.QueryAsync<TableEntity>("PartitionKey eq 'imports'",
            maxPerPage: 100, cancellationToken: cancellationToken))
        {
            if (item.GetString("State") == "published") continue;
            var id = item.GetString("ImportId")!;
            var record = await ReadAsync(id, cancellationToken);
            if (record is not null)
            {
                if (record.State == "cancelled") await ReleaseReservationAsync(record, cancellationToken);
                if (item.GetString("State") != record.State)
                    await JobsTable.UpsertEntityAsync(Index(record), cancellationToken: cancellationToken);
                if (record.State is "queued" or "extracting" or "aligning" or "tagging")
                    await EnqueueAsync(id, cancellationToken);
            }
            else await JobsTable.DeleteEntityAsync(item.PartitionKey, item.RowKey, cancellationToken: cancellationToken);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(BlobClient blob, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var result = await blob.DownloadContentAsync(cancellationToken);
            return result.Value.Content.ToObjectFromJson<T>(Json)
                ?? throw new InvalidDataException("The stored import artifact is empty.");
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return default; }
    }

    private static async Task<Versioned<T>?> ReadVersionedAsync<T>(BlobClient blob, string? ifNoneMatch,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            var response = await blob.DownloadContentAsync(new BlobDownloadOptions
            {
                Conditions = ifNoneMatch is null ? null : new BlobRequestConditions { IfNoneMatch = new ETag(ifNoneMatch) }
            }, cancellationToken);
            if (response.GetRawResponse().Status == 304) return null;
            var result = response.Value;
            var value = result.Content.ToObjectFromJson<T>(Json) ?? throw new InvalidDataException("The stored import artifact is empty.");
            return new Versioned<T>(value, result.Details.ETag.ToString(), result.Details.LastModified);
        }
        catch (RequestFailedException exception) when (exception.Status == 304) { return null; }
        catch (RequestFailedException exception) when (exception.Status == 404) { return new Versioned<T>(null, null, null); }
    }

    private BlobClient ProgressBlob(string id)
    {
        ValidateId(id);
        return records.GetBlobClient($"progress/{id}.json");
    }

    private TableClient JobsTable => jobs ?? throw new InvalidOperationException("Import job table is not configured.");
    private TableClient CatalogTable => catalog ?? throw new InvalidOperationException("Exam catalog table is not configured.");
    private static TableEntity Index(ImportRecord record) => new("imports",
        $"{DateTime.MaxValue.Ticks - record.CreatedAt.UtcTicks:D19}-{record.Id}")
    {
        ["ImportId"] = record.Id, ["CreatedAt"] = record.CreatedAt,
        ["SubjectId"] = record.Metadata.SubjectId, ["State"] = record.State
    };

    private BlobClient RecordBlob(string id)
    {
        ValidateId(id);
        return records.GetBlobClient($"jobs/{id}.json");
    }

    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("A generated import or document ID is required.", nameof(id));
    }
}

    public sealed record DocumentDownload(Stream Stream, string ContentType, ETag ETag, DateTimeOffset LastModified);

    public interface IImportPersistence
    {
        Task<ImportRecord?> ReadAsync(string id, CancellationToken cancellationToken);
        Task<ImportRecord> WriteAsync(ImportRecord record, string? revision, CancellationToken cancellationToken);
        Task EnqueueAsync(string id, CancellationToken cancellationToken);
        Task UploadAsync(string id, Stream stream, string contentType, string role, ImportMetadata metadata,
            string importId, CancellationToken cancellationToken);
        Task<DocumentHashEntry?> FindHashAsync(string sha256, CancellationToken cancellationToken);
        Task<ImportWork?> ReadWorkAsync(string id, CancellationToken cancellationToken);
        Task WriteWorkAsync(string id, ImportWork work, CancellationToken cancellationToken);
        Task WriteProgressAsync(ImportProgress progress, CancellationToken cancellationToken);
        Task<Versioned<ImportProgress>?> ReadProgressAsync(string id, string? ifNoneMatch, CancellationToken cancellationToken);
        Task<Versioned<ImportRecord>?> ReadIfChangedAsync(string id, string? ifNoneMatch, CancellationToken cancellationToken);
        Task<string[]> ListProcessingIdsAsync(CancellationToken cancellationToken);
        Task<ImportRecord[]> ListAsync(CancellationToken cancellationToken);
        Task<ImportRecord[]> ListAllAsync(CancellationToken cancellationToken);
        Task<bool> IsLeasedAsync(string id, CancellationToken cancellationToken);
        Task DeleteAsync(ImportRecord record, CancellationToken cancellationToken);
        Task ReleaseDocumentAsync(string documentId, string deletedImportId, string? survivorImportId, CancellationToken cancellationToken);
        Task<DocumentDownload?> DownloadAsync(string id, CancellationToken cancellationToken);
        Task<ImportRecord> PublishAsync(ImportRecord record, CancellationToken cancellationToken);
        Task<CatalogResponse> CatalogAsync(string subjectId, CancellationToken cancellationToken);
    }

