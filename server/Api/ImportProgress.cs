namespace Examinator.Api;

/// <summary>
/// Writes <c>progress/{id}.json</c> for the import the leased worker is processing. Phase changes are written
/// immediately, steps within a phase at most once per <see cref="StepInterval"/>, and the lease renew loop adds a
/// heartbeat. Progress is best effort: storage failures are logged and never reach the import itself.
/// </summary>
public sealed class ImportProgressReporter(IImportPersistence storage, TimeProvider time, ILogger<ImportProgressReporter> logger)
{
    public static readonly TimeSpan StepInterval = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly Lock gate = new();
    private ImportProgress? current;
    private ImportProgress? beforeThrottle;
    private DateTimeOffset lastWrite = DateTimeOffset.MinValue;
    private bool dirty;

    /// <summary>The import whose progress is being reported, or null before an attempt starts.</summary>
    public string? ImportId { get { lock (gate) return current?.ImportId; } }

    /// <summary>Begins reporting an attempt; the sequence continues from any earlier attempt's progress.</summary>
    public async Task StartAsync(string importId, int attempt, int modelCalls, int modelCallBudget, CancellationToken cancellationToken)
    {
        long sequence = 0;
        try { sequence = (await storage.ReadProgressAsync(importId, null, cancellationToken))?.Value?.Sequence ?? 0; }
        catch (Exception exception)
        {
            logger.LogWarning("Earlier progress for import {ImportId} could not be read: {ErrorType}.", importId, exception.GetType().Name);
        }
        var now = time.GetUtcNow();
        lock (gate)
        {
            current = new ImportProgress(importId, attempt, sequence, "starting", null, null, null, modelCalls, modelCallBudget,
                $"Verwerking gestart (poging {attempt})", now, now);
            beforeThrottle = null;
            dirty = true;
        }
        await WriteAsync(force: true, cancellationToken);
    }

    /// <summary>Starts a phase (or the first step of one); always written immediately.</summary>
    public Task PhaseAsync(string phase, string message, CancellationToken cancellationToken,
        int? step = null, int? total = null, string? unit = null) =>
        Update(progress => progress with { Phase = phase, Message = message, Step = step, Total = total, Unit = unit },
            force: true, cancellationToken);

    /// <summary>Reports a step inside the current phase; coalesced unless the phase changes.</summary>
    public Task StepAsync(string phase, int step, int total, string unit, string message, CancellationToken cancellationToken)
    {
        bool phaseChanged;
        lock (gate) phaseChanged = current?.Phase != phase;
        return Update(progress => progress with { Phase = phase, Step = step, Total = total, Unit = unit, Message = message },
            force: phaseChanged, cancellationToken);
    }

    /// <summary>Records the spent model calls; included in the next write.</summary>
    public void ModelCalls(int calls)
    {
        lock (gate)
        {
            if (current is null || current.ModelCalls == calls) return;
            current = current with { ModelCalls = calls };
            dirty = true;
        }
    }

    /// <summary>Shows a rate-limit wait; <see cref="ResumeAsync"/> restores the interrupted phase.</summary>
    public Task ThrottledAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (current is null) return Task.CompletedTask;
            if (current.Phase != "throttled") beforeThrottle = current;
        }
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        return Update(progress => progress with
        {
            Phase = "throttled", Step = null, Total = null, Unit = null,
            Message = $"Wachten op modelcapaciteit, nieuwe poging over {seconds} s"
        }, force: true, cancellationToken);
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        ImportProgress? previous;
        lock (gate)
        {
            previous = beforeThrottle;
            beforeThrottle = null;
            if (previous is null || current?.Phase != "throttled") return Task.CompletedTask;
        }
        return Update(progress => progress with
        {
            Phase = previous.Phase, Step = previous.Step, Total = previous.Total, Unit = previous.Unit, Message = previous.Message
        }, force: true, cancellationToken);
    }

    /// <summary>Writes a coalesced step that has not been stored yet.</summary>
    public Task FlushAsync(CancellationToken cancellationToken) => WriteAsync(force: false, cancellationToken, flush: true);

    /// <summary>Proves the worker is alive without changing the reported phase.</summary>
    public Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (current is null) return Task.CompletedTask;
            current = current with { HeartbeatAt = time.GetUtcNow() };
            dirty = true;
        }
        return WriteAsync(force: true, cancellationToken);
    }

    private Task Update(Func<ImportProgress, ImportProgress> change, bool force, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (current is null) return Task.CompletedTask;
            var now = time.GetUtcNow();
            current = change(current) with { UpdatedAt = now, HeartbeatAt = now };
            dirty = true;
        }
        return WriteAsync(force, cancellationToken);
    }

    private async Task WriteAsync(bool force, CancellationToken cancellationToken, bool flush = false)
    {
        try
        {
            await writes.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            ImportProgress snapshot;
            lock (gate)
            {
                var now = time.GetUtcNow();
                if (current is null || !dirty) return;
                if (!force && !flush && now - lastWrite < StepInterval) return;
                current = current with { Sequence = current.Sequence + 1 };
                snapshot = current;
                dirty = false;
                lastWrite = now;
            }
            await storage.WriteProgressAsync(snapshot, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Progress for import {ImportId} was not stored: {ErrorType}.", ImportId, exception.GetType().Name);
        }
        finally { writes.Release(); }
    }
}
