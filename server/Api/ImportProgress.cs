namespace Examinator.Api;

/// <summary>
/// Writes <c>progress/{id}.json</c> for the import the leased worker is processing. Phase changes are written
/// immediately, steps within a phase at most once per <see cref="StepInterval"/>, and the lease renew loop adds a
/// heartbeat. Progress is best effort: storage failures are logged and never reach the import itself.
/// The blob also keeps a step log: every phase change closes the open <see cref="ImportPhaseTiming"/> (outcome
/// <c>ok</c>) and opens the next one, which keeps the latest message, step/total and the model calls of that step.
/// A rate-limit wait is a step of its own (<c>throttled</c>); afterwards the interrupted phase opens a new entry.
/// <see cref="CompleteAsync"/> closes the run. A new attempt moves the stored run into <c>EarlierRuns</c>, so retries
/// and the run resumed after <c>awaitingDefinitions</c> keep the history of the runs before them.
/// </summary>
public sealed class ImportProgressReporter(IImportPersistence storage, TimeProvider time, ILogger<ImportProgressReporter> logger)
{
    public static readonly TimeSpan StepInterval = TimeSpan.FromSeconds(2);
    /// <summary>Earlier runs kept per import; older ones are dropped and the history is then marked incomplete.</summary>
    public const int MaximumEarlierRuns = 20;
    /// <summary>Step entries per run; later phase changes are logged in the last entry.</summary>
    public const int MaximumPhases = 100;
    private const int MaximumErrorLength = 300;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly Lock gate = new();
    private ImportProgress? current;
    private ImportProgress? beforeThrottle;
    private DateTimeOffset lastWrite = DateTimeOffset.MinValue;
    private bool dirty;

    /// <summary>The import whose progress is being reported, or null before an attempt starts.</summary>
    public string? ImportId { get { lock (gate) return current?.ImportId; } }

    /// <summary>Begins reporting an attempt; the sequence and the run history continue from the stored progress.</summary>
    public async Task StartAsync(string importId, int attempt, int modelCalls, int modelCallBudget, CancellationToken cancellationToken)
    {
        ImportProgress? previous = null;
        var unreadable = false;
        try { previous = (await storage.ReadProgressAsync(importId, null, cancellationToken))?.Value; }
        catch (Exception exception)
        {
            unreadable = true;
            logger.LogWarning("Earlier progress for import {ImportId} could not be read: {ErrorType}.", importId, exception.GetType().Name);
        }
        var (earlier, unknown) = EarlierRuns(previous);
        var now = time.GetUtcNow();
        var message = $"Verwerking gestart (poging {attempt})";
        lock (gate)
        {
            current = new ImportProgress(importId, attempt, previous?.Sequence ?? 0, "starting", null, null, null, modelCalls,
                modelCallBudget, message, now, now, AttemptStartedAt: now,
                Phases: [new ImportPhaseTiming("starting", now, Message: message)], EarlierRuns: earlier,
                EarlierRunsUnknown: unknown || unreadable, Run: (previous?.Run ?? 0) + 1);
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

    /// <summary>Replaces the message of the current step (a result such as "48 vragen gevonden"); coalesced.</summary>
    public Task NoteAsync(string message, CancellationToken cancellationToken) =>
        Update(progress => progress with { Message = message }, force: false, cancellationToken);

    /// <summary>Records the spent model calls (also on the open step); included in the next write.</summary>
    public void ModelCalls(int calls)
    {
        lock (gate)
        {
            if (current is null || current.ModelCalls == calls) return;
            var used = calls - current.ModelCalls;
            var phases = current.Phases;
            if (current.CompletedAt is null && used > 0 && phases is [.., { EndedAt: null } open])
                phases = [.. phases[..^1], open with { ModelCalls = open.ModelCalls + used }];
            current = current with { ModelCalls = calls, Phases = phases };
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

    /// <summary>
    /// Ends the run with <paramref name="outcome"/> (<c>awaitingDefinitions</c>, <c>review</c>, <c>failed</c> or
    /// <c>cancelled</c>): closes the open step (<c>failed</c> with the short <paramref name="error"/>, <c>cancelled</c>,
    /// or <c>ok</c>), sets <c>CompletedAt</c> and writes immediately. Only the first call counts.
    /// </summary>
    public Task CompleteAsync(string outcome, CancellationToken cancellationToken, string? error = null)
    {
        lock (gate)
        {
            if (current is null || current.CompletedAt is not null) return Task.CompletedTask;
            var now = time.GetUtcNow();
            var stepOutcome = outcome is "failed" or "cancelled" ? outcome : null;
            var phases = Close(current.Phases, now, stepOutcome, outcome == "failed" ? Shorten(error) : null);
            current = current with { CompletedAt = now, Outcome = outcome, Phases = phases, UpdatedAt = now, HeartbeatAt = now };
            beforeThrottle = null;
            dirty = true;
        }
        return WriteAsync(force: true, cancellationToken);
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
            var next = change(current) with { UpdatedAt = now, HeartbeatAt = now };
            if (current.CompletedAt is null) next = next with { Phases = Track(current, next, now) };
            current = next;
            dirty = true;
        }
        return WriteAsync(force, cancellationToken);
    }

    /// <summary>The step log after a change: a new entry on a phase change, then the latest details on the open entry.</summary>
    private static ImportPhaseTiming[]? Track(ImportProgress before, ImportProgress next, DateTimeOffset now)
    {
        var phases = before.Phases;
        if (phases is null) return null;
        if (next.Phase != before.Phase && phases.Length < MaximumPhases)
            phases = [.. Close(phases, now, null, null)!, new ImportPhaseTiming(next.Phase, now)];
        if (phases is not [.., { EndedAt: null } open]) return phases;
        return [.. phases[..^1], open with { Message = next.Message, Step = next.Step, Total = next.Total }];
    }

    /// <summary>Closes the open entry; without an explicit outcome a wait is <c>throttled</c> and any other step <c>ok</c>.</summary>
    private static ImportPhaseTiming[]? Close(ImportPhaseTiming[]? phases, DateTimeOffset end, string? outcome, string? error) =>
        phases is [.., { EndedAt: null } open]
            ? [.. phases[..^1], open with
            {
                EndedAt = end, Outcome = outcome ?? (open.Phase == "throttled" ? "throttled" : "ok"), Error = error
            }]
            : phases;

    private static string? Shorten(string? error) =>
        error is null || error.Length <= MaximumErrorLength ? error : error[..(MaximumErrorLength - 1)] + "…";

    /// <summary>
    /// The stored attempt becomes an earlier run. One that never completed (the worker stopped) ends at its last
    /// heartbeat as <c>interrupted</c>. Progress written before the step log existed marks the history incomplete.
    /// </summary>
    private static (ImportRunTiming[] Runs, bool Unknown) EarlierRuns(ImportProgress? previous)
    {
        if (previous is null) return ([], false);
        if (previous.AttemptStartedAt is not { } started) return ([], true);
        var end = previous.CompletedAt ?? previous.HeartbeatAt;
        var interrupted = previous.CompletedAt is null ? "interrupted" : null;
        var run = new ImportRunTiming(previous.Run, previous.Attempt, started, end, previous.Outcome ?? "interrupted",
            Close(previous.Phases, end, interrupted, null) ?? []);
        ImportRunTiming[] runs = [.. previous.EarlierRuns ?? [], run];
        var dropped = runs.Length > MaximumEarlierRuns;
        return (dropped ? runs[^MaximumEarlierRuns..] : runs, previous.EarlierRunsUnknown || dropped);
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
