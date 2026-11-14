using Azure;

namespace Examinator.Api;

/// <summary>
/// Live progress for one processing import, as sent in an SSE <c>progress</c> event. <c>Timing</c> carries the step log
/// of the current run only (the record GET has all runs), and only when the progress belongs to the record's current attempt.
/// </summary>
public sealed record ImportProgressEvent(string ImportId, string State, string Revision, string Phase, int? Step, int? Total,
    string? Unit, int? ModelCalls, int? ModelCallBudget, string Message, DateTimeOffset? UpdatedAt, DateTimeOffset? HeartbeatAt,
    DateTimeOffset ServerTime, bool Stale, ImportTiming? Timing = null);

/// <summary>Sent once when an import leaves the processing states (or was deleted); the client reloads that row.</summary>
public sealed record ImportSettledEvent(string ImportId, string State, string? Revision);

/// <summary>Keeps idle SSE connections visible to proxies and to the client watchdog.</summary>
public sealed record ImportPingEvent(DateTimeOffset ServerTime);

public sealed class ImportProgressStreamOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Below the ~240 s Container Apps ingress timeout; EventSource reconnects afterwards.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromSeconds(180);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(75);
    public TimeSpan QueuedTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public int MaximumStreams { get; init; } = 4;
}

/// <summary>Limits concurrent progress streams per Api replica.</summary>
public sealed class ImportProgressStreamGate(ImportProgressStreamOptions options)
{
    private readonly SemaphoreSlim slots = new(options.MaximumStreams, options.MaximumStreams);
    public bool TryEnter() => slots.Wait(0);
    public void Exit() => slots.Release();
}

/// <summary>
/// One list-level stream for all processing imports. It discovers processing imports from the job index, polls
/// their record and progress blobs with conditional reads, and emits only changes. The record stays authoritative:
/// progress is shown only while the record is processing and belongs to the record's current attempt.
/// </summary>
public sealed class ImportProgressStream(IImportPersistence storage, TimeProvider time, ImportProgressStreamOptions options,
    ILogger<ImportProgressStream> logger)
{
    public delegate Task EventWriter(string name, object payload, CancellationToken cancellationToken);

    private static readonly HashSet<string> Processing = ["queued", "extracting", "aligning", "tagging"];

    private sealed class Tracked
    {
        public ImportRecord? Record;
        public string? RecordETag;
        public DateTimeOffset? RecordModified;
        public ImportProgress? Progress;
        public string? ProgressETag;
        public string? LastKey;
    }

    public async Task RunAsync(EventWriter write, CancellationToken cancellationToken)
    {
        var tracked = new Dictionary<string, Tracked>(StringComparer.Ordinal);
        var start = time.GetUtcNow();
        var lastDiscovery = DateTimeOffset.MinValue;
        var lastPing = start;
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            try
            {
                if (now - lastDiscovery >= options.DiscoveryInterval)
                {
                    lastDiscovery = now;
                    foreach (var id in await storage.ListProcessingIdsAsync(cancellationToken))
                        tracked.TryAdd(id, new Tracked());
                }
                foreach (var (id, item) in tracked.ToArray())
                    if (!await PollAsync(id, item, write, cancellationToken)) tracked.Remove(id);
            }
            catch (RequestFailedException exception)
            {
                logger.LogWarning("Import progress polling failed with storage status {Status}.", exception.Status);
            }
            now = time.GetUtcNow();
            if (now - lastPing >= options.PingInterval)
            {
                lastPing = now;
                await write("ping", new ImportPingEvent(now), cancellationToken);
            }
            if (now - start >= options.Lifetime) return;
            await Task.Delay(options.PollInterval, time, cancellationToken);
        }
    }

    /// <summary>Returns false once the import has settled and is no longer tracked.</summary>
    private async Task<bool> PollAsync(string id, Tracked item, EventWriter write, CancellationToken cancellationToken)
    {
        var record = await storage.ReadIfChangedAsync(id, item.RecordETag, cancellationToken);
        if (record is not null)
        {
            if (record.Value is null)
            {
                await write("settled", new ImportSettledEvent(id, "deleted", null), cancellationToken);
                return false;
            }
            item.Record = record.Value;
            item.RecordETag = record.ETag;
            item.RecordModified = record.LastModified;
        }
        var current = item.Record!;
        if (!Processing.Contains(current.State))
        {
            await write("settled", new ImportSettledEvent(id, current.State, current.Revision), cancellationToken);
            return false;
        }
        var progress = await storage.ReadProgressAsync(id, item.ProgressETag, cancellationToken);
        if (progress is not null)
        {
            item.Progress = progress.Value;
            item.ProgressETag = progress.ETag;
        }
        var payload = Describe(current, item.RecordModified, item.Progress);
        var key = $"{current.Revision}|{(Applies(current, item.Progress) ? item.Progress!.Sequence : -1)}|{payload.Stale}";
        if (key == item.LastKey) return true;
        item.LastKey = key;
        await write("progress", payload, cancellationToken);
        return true;
    }

    /// <summary>Progress from an earlier attempt (or before the worker started this one) is masked.</summary>
    private static bool Applies(ImportRecord record, ImportProgress? progress) =>
        progress is not null && record.ProcessingAttempts > 0 && progress.Attempt == record.ProcessingAttempts;

    private ImportProgressEvent Describe(ImportRecord record, DateTimeOffset? recordModified, ImportProgress? progress)
    {
        var now = time.GetUtcNow();
        if (Applies(record, progress))
            return new ImportProgressEvent(record.Id, record.State, record.Revision, progress!.Phase, progress.Step, progress.Total,
                progress.Unit, progress.ModelCalls, progress.ModelCallBudget, progress.Message, progress.UpdatedAt, progress.HeartbeatAt,
                now, progress.CompletedAt is null && now - progress.HeartbeatAt > options.HeartbeatTimeout, ImportTiming.From(progress, ImportTimingDetail.CurrentRun));
        if (record.State == "queued")
        {
            var stale = recordModified is { } since && now - since > options.QueuedTimeout;
            return new ImportProgressEvent(record.Id, record.State, record.Revision, "queued", null, null, null, null, null,
                stale ? "De achtergrondtaak is nog niet gestart" : "Wachten op de achtergrondtaak (kan tot een minuut duren)",
                recordModified, null, now, stale);
        }
        return new ImportProgressEvent(record.Id, record.State, record.Revision, record.State, null, null, null, null, null,
            "De verwerking loopt op de achtergrond", recordModified, null, now,
            recordModified is { } modified && now - modified > options.HeartbeatTimeout);
    }
}
