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

public sealed class ImportStepLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 14, 32, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (ImportProgressReporter Reporter, MemoryImportStore Store, ManualTime Time) Create(MemoryImportStore? store = null,
        ManualTime? time = null)
    {
        store ??= new MemoryImportStore();
        time ??= new ManualTime(Start);
        return (new ImportProgressReporter(store, time, NullLogger<ImportProgressReporter>.Instance), store, time);
    }

    [Fact]
    public async Task EveryPhaseChangeClosesTheOpenStepWithItsDetails()
    {
        var id = Guid.NewGuid().ToString("N");
        var (reporter, store, time) = Create();
        await reporter.StartAsync(id, 1, 0, 64, default);
        time.Advance(TimeSpan.FromSeconds(5));
        await reporter.PhaseAsync("extracting", "Examen-PDF uitlezen (1 van 2)", default, 1, 2, "document");
        time.Advance(TimeSpan.FromSeconds(4));
        await reporter.StepAsync("extracting", 2, 2, "document", "Correctievoorschrift uitlezen (2 van 2)", default);
        time.Advance(TimeSpan.FromSeconds(8));
        await reporter.PhaseAsync("inventory", "Vragen inventariseren in het examen", default, 1, 2, "document");
        reporter.ModelCalls(1);
        reporter.ModelCalls(2);
        await reporter.NoteAsync("48 vragen gevonden", default);
        time.Advance(TimeSpan.FromSeconds(20));
        await reporter.PhaseAsync("aligning", "Antwoorden koppelen", default);
        var running = store.Progress(id)!;
        Assert.Equal(["starting", "extracting", "inventory", "aligning"], running.Phases!.Select(p => p.Phase));
        Assert.Null(running.Phases![^1].EndedAt);
        Assert.Null(running.Phases![^1].Outcome);
        Assert.Null(running.CompletedAt);

        time.Advance(TimeSpan.FromSeconds(3));
        await reporter.CompleteAsync("review", default);
        var done = store.Progress(id)!;
        Assert.Equal(Start, done.AttemptStartedAt);
        Assert.Equal(Start.AddSeconds(40), done.CompletedAt);
        Assert.Equal("review", done.Outcome);
        Assert.Equal(1, done.Run);
        var extracting = done.Phases![1];
        Assert.Equal((Start.AddSeconds(5), Start.AddSeconds(17)), (extracting.StartedAt, extracting.EndedAt!.Value));
        Assert.Equal(("Correctievoorschrift uitlezen (2 van 2)", 2, 2, "ok"), (extracting.Message, extracting.Step, extracting.Total, extracting.Outcome));
        var inventory = done.Phases[2];
        Assert.Equal(("48 vragen gevonden", 2, "ok"), (inventory.Message, inventory.ModelCalls, inventory.Outcome));
        Assert.Equal(0, done.Phases[1].ModelCalls);
        Assert.All(done.Phases, p => Assert.Equal("ok", p.Outcome));

        var timing = ImportTiming.From(done, ImportTimingDetail.AllRuns)!;
        Assert.Equal((Start, 40_000L, 0L), (timing.ProcessingStartedAt!.Value, timing.ProcessingMilliseconds!.Value, timing.EarlierProcessingMilliseconds!.Value));
        Assert.Single(timing.Runs);
        Assert.Empty(ImportTiming.From(done, ImportTimingDetail.Summary)!.Runs);
    }

    [Fact]
    public async Task ARateLimitWaitIsAStepOfItsOwnAndTheInterruptedPhaseContinuesAfterIt()
    {
        var id = Guid.NewGuid().ToString("N");
        var (reporter, store, time) = Create();
        await reporter.StartAsync(id, 1, 0, 64, default);
        await reporter.PhaseAsync("tagging", "Vraag 7 van 32: onderwerpen toekennen", default, 7, 32, "vraag");
        time.Advance(TimeSpan.FromSeconds(5));
        await reporter.ThrottledAsync(TimeSpan.FromSeconds(30), default);
        time.Advance(TimeSpan.FromSeconds(30));
        await reporter.ResumeAsync(default);
        time.Advance(TimeSpan.FromSeconds(4));
        await reporter.CompleteAsync("review", default);
        var phases = store.Progress(id)!.Phases!;
        Assert.Equal(["starting", "tagging", "throttled", "tagging"], phases.Select(p => p.Phase));
        Assert.Equal(("throttled", "Wachten op modelcapaciteit, nieuwe poging over 30 s"), (phases[2].Outcome, phases[2].Message));
        Assert.Equal(TimeSpan.FromSeconds(30), phases[2].EndedAt - phases[2].StartedAt);
        Assert.Equal(("ok", 7, "Vraag 7 van 32: onderwerpen toekennen"), (phases[3].Outcome, phases[3].Step, phases[3].Message));
    }

    [Fact]
    public async Task AFailedRunMarksTheOpenStepWithTheShortError()
    {
        var id = Guid.NewGuid().ToString("N");
        var (reporter, store, time) = Create();
        await reporter.StartAsync(id, 1, 0, 64, default);
        await reporter.PhaseAsync("inventory", "Vragen inventariseren in het examen", default);
        time.Advance(TimeSpan.FromSeconds(2));
        await reporter.CompleteAsync("failed", default, new string('x', 500));
        await reporter.CompleteAsync("review", default);
        await reporter.PhaseAsync("finishing", "Na afloop", default);
        var progress = store.Progress(id)!;
        Assert.Equal("failed", progress.Outcome);
        Assert.Equal(["starting", "inventory"], progress.Phases!.Select(p => p.Phase));
        Assert.Equal(("ok", "failed"), (progress.Phases[0].Outcome, progress.Phases[1].Outcome));
        Assert.Equal(300, progress.Phases[1].Error!.Length);
    }

    [Fact]
    public async Task AResumedRunKeepsTheRunBeforeItAndExcludesTheWaitBetweenRuns()
    {
        var id = Guid.NewGuid().ToString("N");
        var store = new MemoryImportStore();
        var time = new ManualTime(Start);
        var (first, _, _) = Create(store, time);
        await first.StartAsync(id, 1, 0, 64, default);
        await first.PhaseAsync("definitions", "Hoofdstukdefinities voorstellen", default);
        time.Advance(TimeSpan.FromSeconds(10));
        await first.CompleteAsync("awaitingDefinitions", default);
        time.Advance(TimeSpan.FromHours(1));
        var (second, _, _) = Create(store, time);
        await second.StartAsync(id, 2, 1, 64, default);
        await second.PhaseAsync("aligning", "Antwoorden koppelen", default);
        time.Advance(TimeSpan.FromSeconds(20));
        await second.CompleteAsync("review", default);

        var progress = store.Progress(id)!;
        Assert.Equal(2, progress.Run);
        var earlier = Assert.Single(progress.EarlierRuns!);
        Assert.Equal((1, 1, "awaitingDefinitions", Start.AddSeconds(10)), (earlier.Number, earlier.Attempt, earlier.Outcome, earlier.EndedAt!.Value));
        var timing = ImportTiming.From(progress, ImportTimingDetail.AllRuns)!;
        Assert.Equal([1, 2], timing.Runs.Select(r => r.Number));
        Assert.Equal(Start, timing.ProcessingStartedAt);
        Assert.Equal(30_000, timing.ProcessingMilliseconds);
        Assert.Equal(10_000, timing.EarlierProcessingMilliseconds);
        Assert.Equal(Start.AddHours(1).AddSeconds(30), timing.CompletedAt);
        var current = Assert.Single(ImportTiming.From(progress, ImportTimingDetail.CurrentRun)!.Runs);
        Assert.Equal(2, current.Number);
    }

    [Fact]
    public async Task ARunThatStoppedWithoutFinishingEndsAtItsLastHeartbeat()
    {
        var id = Guid.NewGuid().ToString("N");
        var (reporter, store, time) = Create();
        await store.WriteProgressAsync(new ImportProgress(id, 1, 5, "tagging", 3, 9, "vraag", 4, 64, "Vraag 3 van 9", Start, Start.AddSeconds(50),
            AttemptStartedAt: Start, Phases: [new("starting", Start, Start.AddSeconds(1), Outcome: "ok"), new("tagging", Start.AddSeconds(1))], Run: 3), default);
        time.Advance(TimeSpan.FromMinutes(5));
        await reporter.StartAsync(id, 2, 4, 64, default);
        var run = Assert.Single(store.Progress(id)!.EarlierRuns!);
        Assert.Equal((3, "interrupted", Start.AddSeconds(50)), (run.Number, run.Outcome, run.EndedAt!.Value));
        Assert.Equal(("interrupted", Start.AddSeconds(50)), (run.Phases[^1].Outcome, run.Phases[^1].EndedAt!.Value));
        Assert.Equal(4, store.Progress(id)!.Run);
    }

    [Fact]
    public async Task OnlyTheLastTwentyRunsAreKept()
    {
        var id = Guid.NewGuid().ToString("N");
        var (reporter, store, _) = Create();
        var runs = Enumerable.Range(1, 20).Select(n => new ImportRunTiming(n, 1, Start, Start.AddSeconds(1), "failed", [])).ToArray();
        await store.WriteProgressAsync(new ImportProgress(id, 1, 1, "starting", null, null, null, 0, 64, "", Start, Start,
            AttemptStartedAt: Start, CompletedAt: Start.AddSeconds(1), Outcome: "failed", Phases: [], EarlierRuns: runs, Run: 21), default);
        await reporter.StartAsync(id, 1, 0, 64, default);
        var progress = store.Progress(id)!;
        Assert.Equal(ImportProgressReporter.MaximumEarlierRuns, progress.EarlierRuns!.Length);
        Assert.Equal(Enumerable.Range(2, 20), progress.EarlierRuns.Select(r => r.Number));
        Assert.True(progress.EarlierRunsUnknown);
        Assert.Equal(22, progress.Run);
        Assert.Null(ImportTiming.From(progress, ImportTimingDetail.AllRuns)!.ProcessingStartedAt);
    }

    [Fact]
    public async Task ProgressWrittenBeforeTheStepLogStillDeserializesAndMarksTheHistoryUnknown()
    {
        const string old = """
            {"importId":"0123456789abcdef0123456789abcdef","attempt":1,"sequence":7,"phase":"tagging","step":3,"total":9,
             "unit":"vraag","modelCalls":5,"modelCallBudget":64,"message":"Vraag 3 van 9","updatedAt":"2026-11-14T14:00:00+00:00",
             "heartbeatAt":"2026-11-14T14:00:10+00:00"}
            """;
        var progress = JsonSerializer.Deserialize<ImportProgress>(old, Json)!;
        Assert.Equal(("tagging", 7L), (progress.Phase, progress.Sequence));
        Assert.Null(progress.AttemptStartedAt);
        Assert.Null(progress.Phases);
        Assert.Null(ImportTiming.From(progress, ImportTimingDetail.AllRuns));
        var record = JsonSerializer.Deserialize<ImportRecord>("""
            {"id":"0123456789abcdef0123456789abcdef","state":"review","metadata":{"subjectId":"biology","level":"havo","year":2025,
             "timevak":1,"chapterNumber":"","chapterTitle":"","topics":[]},"topics":[],"questions":[],"warnings":[],"error":null,
             "revision":"","examDocumentId":"a","answerDocumentId":"b","createdAt":"2026-11-14T13:00:00+00:00"}
            """, Json)!;
        Assert.Null(record.UpdatedAt);
        Assert.Null(record.Timing);

        var (reporter, store, _) = Create();
        await store.WriteProgressAsync(progress, default);
        await reporter.StartAsync(progress.ImportId, 2, 5, 64, default);
        await reporter.CompleteAsync("review", default);
        var timing = ImportTiming.From(store.Progress(progress.ImportId), ImportTimingDetail.AllRuns)!;
        Assert.True(timing.EarlierRunsUnknown);
        Assert.Null(timing.ProcessingMilliseconds);
        Assert.Null(timing.ProcessingStartedAt);
        Assert.Single(timing.Runs);
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
    public async Task ProgressEventsCarryTheStepLogOfTheCurrentRunOnly()
    {
        var harness = new Harness();
        var record = await harness.Add("tagging", attempts: 2);
        var earlier = new ImportRunTiming(1, 1, Start.AddMinutes(-30), Start.AddMinutes(-29), "awaitingDefinitions",
            [new("definitions", Start.AddMinutes(-30), Start.AddMinutes(-29), "Hoofdstukdefinities voorstellen", Outcome: "ok")]);
        await harness.Store.WriteProgressAsync(Progress(record, 1, "tagging", 1, 3, attempt: 2) with
        {
            AttemptStartedAt = Start, Run = 2, EarlierRuns = [earlier],
            Phases = [new("starting", Start, Start.AddSeconds(1), Outcome: "ok"), new("tagging", Start.AddSeconds(1), Message: "Vraag 1 van 3", Step: 1, Total: 3, ModelCalls: 1)]
        }, default);
        using var cancel = new CancellationTokenSource();
        var run = harness.Run(cancel.Token);
        var events = await harness.WaitFor(e => e.Any(x => x.Name == "progress"));
        var timing = For(events, "progress", record.Id).Single().Data.GetProperty("timing");
        Assert.Equal(60_000, timing.GetProperty("earlierProcessingMilliseconds").GetInt64());
        Assert.Equal(JsonValueKind.Null, timing.GetProperty("processingMilliseconds").ValueKind);
        var runs = timing.GetProperty("runs");
        Assert.Equal(1, runs.GetArrayLength());
        Assert.Equal(2, runs[0].GetProperty("number").GetInt32());
        var phases = runs[0].GetProperty("phases");
        Assert.Equal("tagging", phases[1].GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, phases[1].GetProperty("endedAt").ValueKind);
        Assert.Equal(1, phases[1].GetProperty("modelCalls").GetInt32());
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
