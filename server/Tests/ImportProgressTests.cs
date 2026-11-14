using System.Text.Json;
using Azure;
using Examinator.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace Examinator.Tests;

/// <summary>A settable clock; timers stay real so near-zero poll intervals still elapse.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private readonly Lock gate = new();
    private DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
    public void Advance(TimeSpan by) { lock (gate) now += by; }
}

public sealed class ImportProgressReporterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 14, 32, 0, TimeSpan.Zero);
    private static readonly string Id = Guid.NewGuid().ToString("N");

    private static (ImportProgressReporter Reporter, MemoryImportStore Store, ManualTime Time) Create()
    {
        var store = new MemoryImportStore();
        var time = new ManualTime(Start);
        return (new ImportProgressReporter(store, time, NullLogger<ImportProgressReporter>.Instance), store, time);
    }

    [Fact]
    public async Task PhaseChangesAreWrittenImmediatelyAndStepsAreCoalesced()
    {
        var (reporter, store, time) = Create();
        await reporter.StartAsync(Id, 1, 0, 64, default);
        await reporter.PhaseAsync("tagging", "Vraag 1 van 3: onderwerpen toekennen", default, 1, 3, "vraag");
        Assert.Equal(["starting", "tagging"], store.ProgressWrites.Select(p => p.Phase));

        time.Advance(TimeSpan.FromSeconds(1));
        await reporter.StepAsync("tagging", 2, 3, "vraag", "Vraag 2 van 3: onderwerpen toekennen", default);
        Assert.Equal(2, store.ProgressWrites.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        reporter.ModelCalls(5);
        await reporter.StepAsync("tagging", 3, 3, "vraag", "Vraag 3 van 3: onderwerpen toekennen", default);
        Assert.Equal(3, store.ProgressWrites.Count);
        var latest = store.Progress(Id)!;
        Assert.Equal(3, latest.Step);
        Assert.Equal(5, latest.ModelCalls);
        Assert.Equal(64, latest.ModelCallBudget);
        Assert.Equal("vraag", latest.Unit);
        Assert.Equal(Start.AddSeconds(2), latest.UpdatedAt);
        Assert.Equal(store.ProgressWrites.Select(p => p.Sequence).Order(), store.ProgressWrites.Select(p => p.Sequence));
        Assert.Equal(store.ProgressWrites.Count, store.ProgressWrites.Select(p => p.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task FlushWritesACoalescedStepAndNothingWhenClean()
    {
        var (reporter, store, _) = Create();
        await reporter.StartAsync(Id, 1, 0, 64, default);
        await reporter.PhaseAsync("tagging", "Vraag 1 van 2: onderwerpen toekennen", default, 1, 2, "vraag");
        await reporter.StepAsync("tagging", 2, 2, "vraag", "Vraag 2 van 2: onderwerpen toekennen", default);
        Assert.Equal(1, store.Progress(Id)!.Step);
        await reporter.FlushAsync(default);
        Assert.Equal(2, store.Progress(Id)!.Step);
        var count = store.ProgressWrites.Count;
        await reporter.FlushAsync(default);
        Assert.Equal(count, store.ProgressWrites.Count);
    }

    [Fact]
    public async Task HeartbeatOnlyMovesHeartbeatAt()
    {
        var (reporter, store, time) = Create();
        await reporter.StartAsync(Id, 2, 3, 64, default);
        await reporter.PhaseAsync("aligning", "Antwoorden koppelen", default);
        var before = store.Progress(Id)!;
        time.Advance(TimeSpan.FromSeconds(25));
        await reporter.HeartbeatAsync(default);
        var after = store.Progress(Id)!;
        Assert.Equal(before with { Sequence = after.Sequence, HeartbeatAt = after.HeartbeatAt }, after);
        Assert.Equal(Start.AddSeconds(25), after.HeartbeatAt);
        Assert.True(after.Sequence > before.Sequence);
    }

    [Fact]
    public async Task HeartbeatBeforeAnAttemptWritesNothing()
    {
        var (reporter, store, _) = Create();
        await reporter.HeartbeatAsync(default);
        Assert.Empty(store.ProgressWrites);
    }

    [Fact]
    public async Task StorageFailuresAreSwallowed()
    {
        var (reporter, store, _) = Create();
        store.ProgressFailure = new RequestFailedException(503, "Synthetic outage.");
        await reporter.StartAsync(Id, 1, 0, 64, default);
        await reporter.PhaseAsync("extracting", "Examen-PDF uitlezen (1 van 2)", default, 1, 2, "document");
        await reporter.ThrottledAsync(TimeSpan.FromSeconds(30), default);
        await reporter.ResumeAsync(default);
        await reporter.HeartbeatAsync(default);
        await reporter.FlushAsync(default);
        Assert.Empty(store.ProgressWrites);
    }

    [Fact]
    public async Task ANewAttemptContinuesTheStoredSequence()
    {
        var (reporter, store, _) = Create();
        await store.WriteProgressAsync(new ImportProgress(Id, 1, 41, "tagging", 3, 9, "vraag", 5, 64, "Vraag 3 van 9", Start, Start), default);
        await reporter.StartAsync(Id, 2, 5, 64, default);
        var started = store.Progress(Id)!;
        Assert.Equal(42, started.Sequence);
        Assert.Equal(2, started.Attempt);
        Assert.Equal("starting", started.Phase);
        Assert.Equal("Verwerking gestart (poging 2)", started.Message);
    }

    [Fact]
    public async Task ThrottlingShowsTheWaitAndResumeRestoresThePhase()
    {
        var (reporter, store, _) = Create();
        await reporter.StartAsync(Id, 1, 0, 64, default);
        await reporter.PhaseAsync("tagging", "Vraag 7 van 32: onderwerpen toekennen", default, 7, 32, "vraag");
        await reporter.ThrottledAsync(TimeSpan.FromSeconds(29.2), default);
        var throttled = store.Progress(Id)!;
        Assert.Equal("throttled", throttled.Phase);
        Assert.Equal("Wachten op modelcapaciteit, nieuwe poging over 30 s", throttled.Message);
        Assert.Null(throttled.Step);
        await reporter.ResumeAsync(default);
        var resumed = store.Progress(Id)!;
        Assert.Equal("tagging", resumed.Phase);
        Assert.Equal(7, resumed.Step);
        Assert.Equal("Vraag 7 van 32: onderwerpen toekennen", resumed.Message);
    }
}

public sealed class ImportProgressStreamTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 14, 0, 0, TimeSpan.Zero);

    private sealed record Sent(string Name, JsonElement Data);

    private sealed class Harness
    {
        public MemoryImportStore Store { get; } = new();
        public ManualTime Time { get; } = new(Start);
        public List<Sent> Events { get; } = [];
        private readonly Lock gate = new();
        public ImportProgressStreamOptions Options { get; init; } = new()
        {
            PollInterval = TimeSpan.FromMilliseconds(1), DiscoveryInterval = TimeSpan.Zero,
            PingInterval = TimeSpan.FromSeconds(15), Lifetime = TimeSpan.FromSeconds(180)
        };

        public Harness() => Store.Clock = Time.GetUtcNow;

        public Task Run(CancellationToken cancellationToken) =>
            new ImportProgressStream(Store, Time, Options, NullLogger<ImportProgressStream>.Instance).RunAsync((name, payload, _) =>
            {
                lock (gate) Events.Add(new(name, JsonSerializer.SerializeToElement(payload, payload.GetType(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))));
                return Task.CompletedTask;
            }, cancellationToken);

        public Sent[] Snapshot() { lock (gate) return Events.ToArray(); }

        public async Task<Sent[]> WaitFor(Func<Sent[], bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var events = Snapshot();
                if (condition(events)) return events;
                await Task.Delay(5);
            }
            throw new TimeoutException("The expected stream events did not arrive. Seen: " +
                string.Join(", ", Snapshot().Select(e => e.Name + " " + e.Data)));
        }

        public async Task<ImportRecord> Add(string state, int attempts = 1)
        {
            var record = Sample(state) with { ProcessingAttempts = attempts };
            return await Store.WriteAsync(record, null, default);
        }
    }

    private static ImportRecord Sample(string state) => new(Guid.NewGuid().ToString("N"), state,
        new("biology", "havo", 2025, 1, "", "", []), [], [], [], null, "", Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N"), Start);

    private static ImportProgress Progress(ImportRecord record, long sequence, string phase, int? step = null, int? total = null,
        int attempt = 1, DateTimeOffset? at = null) => new(record.Id, attempt, sequence, phase, step, total,
        step is null ? null : "vraag", 4, 64, $"Fase {phase} {step}", at ?? Start, at ?? Start);

    /// <summary>Cancellation ends the stream either inside a delay (throws) or at the loop check (returns).</summary>
    private static async Task Stop(CancellationTokenSource cancel, Task run)
    {
        cancel.Cancel();
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
    }

    private static IEnumerable<Sent> For(IEnumerable<Sent> events, string name, string id) =>
        events.Where(e => e.Name == name && e.Data.GetProperty("importId").GetString() == id);

    [Fact]
    public async Task SendsASnapshotPerProcessingImportAndThenOnlyChanges()
    {
        var harness = new Harness();
        var tagging = await harness.Add("tagging");
        var queued = await harness.Add("queued", attempts: 0);
        await harness.Add("review");
        await harness.Store.WriteProgressAsync(Progress(tagging, 1, "tagging", 1, 3), default);
        using var cancel = new CancellationTokenSource();
        var run = harness.Run(cancel.Token);

        var events = await harness.WaitFor(e => e.Count(x => x.Name == "progress") >= 2);
        await Task.Delay(50);
        events = harness.Snapshot();
        Assert.Equal(2, events.Count(e => e.Name == "progress"));
        var first = For(events, "progress", tagging.Id).Single().Data;
        Assert.Equal("tagging", first.GetProperty("phase").GetString());
        Assert.Equal(1, first.GetProperty("step").GetInt32());
        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(4, first.GetProperty("modelCalls").GetInt32());
        Assert.Equal(64, first.GetProperty("modelCallBudget").GetInt32());
        Assert.Equal(tagging.Revision, first.GetProperty("revision").GetString());
        Assert.False(first.GetProperty("stale").GetBoolean());
        var waiting = For(events, "progress", queued.Id).Single().Data;
        Assert.Equal("queued", waiting.GetProperty("phase").GetString());
        Assert.Equal("Wachten op de achtergrondtaak (kan tot een minuut duren)", waiting.GetProperty("message").GetString());

        await harness.Store.WriteProgressAsync(Progress(tagging, 2, "tagging", 2, 3), default);
        events = await harness.WaitFor(e => For(e, "progress", tagging.Id).Count() == 2);
        Assert.Equal(2, For(events, "progress", tagging.Id).Last().Data.GetProperty("step").GetInt32());
        Assert.Single(For(events, "progress", queued.Id));
        await Stop(cancel, run);
    }

    [Fact]
    public async Task DiscoversNewlyQueuedImportsDuringTheStream()
    {
        var harness = new Harness();
        await harness.Add("tagging");
        using var cancel = new CancellationTokenSource();
        var run = harness.Run(cancel.Token);
        await harness.WaitFor(e => e.Any(x => x.Name == "progress"));
        var added = await harness.Add("queued", attempts: 0);
        await harness.WaitFor(e => For(e, "progress", added.Id).Any());
        await Stop(cancel, run);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("awaitingDefinitions")]
    public async Task SettlesOnceWhenAnImportLeavesProcessing(string state)
    {
        var harness = new Harness();
        var record = await harness.Add("tagging");
        using var cancel = new CancellationTokenSource();
        var run = harness.Run(cancel.Token);
        await harness.WaitFor(e => e.Any(x => x.Name == "progress"));
        var settled = await harness.Store.WriteAsync(record with { State = state }, record.Revision, default);
        var events = await harness.WaitFor(e => e.Any(x => x.Name == "settled"));
        await Task.Delay(50);
        events = harness.Snapshot();
        var data = For(events, "settled", record.Id).Single().Data;
        Assert.Equal(state, data.GetProperty("state").GetString());
        Assert.Equal(settled.Revision, data.GetProperty("revision").GetString());
        Assert.Single(For(events, "progress", record.Id));
        await Stop(cancel, run);
    }

    [Fact]
    public async Task SettlesAsDeletedWhenAQueuedImportIsRemoved()
    {
        var harness = new Harness();
        var record = await harness.Add("queued", attempts: 0);
        using var cancel = new CancellationTokenSource();
        var run = harness.Run(cancel.Token);
        await harness.WaitFor(e => e.Any(x => x.Name == "progress"));
        await harness.Store.DeleteAsync(record, default);
        var events = await harness.WaitFor(e => e.Any(x => x.Name == "settled"));
        Assert.Equal("deleted", For(events, "settled", record.Id).Single().Data.GetProperty("state").GetString());
        await Stop(cancel, run);
    }

    [Fact]
    public async Task PingsAndClosesAfterTheLifetime()
    {
        var harness = new Harness();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = harness.Run(cancel.Token);
        await Task.Delay(20);
        Assert.DoesNotContain(harness.Snapshot(), e => e.Name == "ping");
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        await harness.WaitFor(e => e.Any(x => x.Name == "ping"));
        Assert.False(run.IsCompleted);
        harness.Time.Advance(TimeSpan.FromSeconds(170));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FlipsToStaleWithoutAHeartbeatAndBackWhenOneArrives()
    {
        var harness = new Harness();
        var record = await harness.Add("aligning");
        await harness.Store.WriteProgressAsync(Progress(record, 1, "aligning"), default);
        using var cancel = new CancellationTokenSource();
        var run = harness.Run(cancel.Token);
        await harness.WaitFor(e => e.Any(x => x.Name == "progress"));
        harness.Time.Advance(TimeSpan.FromSeconds(76));
        var events = await harness.WaitFor(e => e.Count(x => x.Name == "progress") == 2);
        Assert.True(events.Last(e => e.Name == "progress").Data.GetProperty("stale").GetBoolean());
        await harness.Store.WriteProgressAsync(Progress(record, 2, "aligning", at: harness.Time.GetUtcNow()), default);
        events = await harness.WaitFor(e => e.Count(x => x.Name == "progress") == 3);
        Assert.False(events.Last(e => e.Name == "progress").Data.GetProperty("stale").GetBoolean());
        await Stop(cancel, run);
    }

    [Fact]
    public async Task AQueuedImportBecomesStaleAfterThreeMinutes()
    {
        var harness = new Harness { Options = new() { PollInterval = TimeSpan.FromMilliseconds(1), DiscoveryInterval = TimeSpan.Zero, Lifetime = TimeSpan.FromHours(1), PingInterval = TimeSpan.FromHours(1) } };
        var record = await harness.Add("queued", attempts: 0);
        using var cancel = new CancellationTokenSource();
        var run = harness.Run(cancel.Token);
        await harness.WaitFor(e => e.Any(x => x.Name == "progress"));
        harness.Time.Advance(TimeSpan.FromMinutes(3.5));
        var events = await harness.WaitFor(e => e.Count(x => x.Name == "progress") == 2);
        var stale = For(events, "progress", record.Id).Last().Data;
        Assert.True(stale.GetProperty("stale").GetBoolean());
        Assert.Equal("De achtergrondtaak is nog niet gestart", stale.GetProperty("message").GetString());
        await Stop(cancel, run);
    }

    [Fact]
    public async Task MasksProgressFromAnEarlierAttempt()
    {
        var harness = new Harness();
        var record = await harness.Add("aligning", attempts: 2);
        await harness.Store.WriteProgressAsync(Progress(record, 9, "tagging", 5, 9, attempt: 1), default);
        using var cancel = new CancellationTokenSource();
        var run = harness.Run(cancel.Token);
        var events = await harness.WaitFor(e => e.Any(x => x.Name == "progress"));
        var data = events.First(e => e.Name == "progress").Data;
        Assert.Equal("aligning", data.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("step").ValueKind);
        Assert.Equal("De verwerking loopt op de achtergrond", data.GetProperty("message").GetString());
        await Stop(cancel, run);
    }
}
