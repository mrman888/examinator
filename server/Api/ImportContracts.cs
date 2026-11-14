namespace Examinator.Api;

/// <summary>A stable, subject-scoped topic with optional chapter/paragraph grouping hints.</summary>
public sealed record TopicDefinition(
    string Id, string Title, string Description, string[] Keywords, string[] Exclusions,
    string[] Aliases, string? ChapterNumber, string? ParagraphNumber);

/// <summary>Exam metadata; an unknown sitting is resolved from the uploaded documents.</summary>
public sealed record ImportMetadata(
    string SubjectId, string Level, int Year, int Timevak, string ChapterNumber,
    string ChapterTitle, TopicDefinition[] Topics);

/// <summary>A reviewable question with physical PDF page ranges and independent topic tags.</summary>
public sealed record DraftQuestion(
    string Id, int Number, string Title, int ExamPage, int ExamEndPage,
    int? AnswerPage, int? AnswerEndPage, string[] TopicIds, string[] Reasons,
    bool NeedsReview, string[] Warnings);

/// <summary>
/// Durable import status, source references, and revision-checked review data. <c>UpdatedAt</c> is set on every
/// record write (older records fall back to the blob's last-modified time). <c>Timing</c> is never stored: the Api
/// adds it to responses from the worker's progress blob.
/// </summary>
public sealed record ImportRecord(
    string Id, string State, ImportMetadata Metadata, TopicDefinition[] Topics,
    DraftQuestion[] Questions, string[] Warnings, string? Error, string Revision,
    string ExamDocumentId, string AnswerDocumentId, DateTimeOffset CreatedAt,
    string? ContextDocumentId = null, string? ContextText = null, int ProcessingAttempts = 0,
    DateTimeOffset? UpdatedAt = null, ImportTiming? Timing = null);

/// <summary>Saves topic definitions and the complete, unchanged question inventory.</summary>
public sealed record DraftUpdate(string Revision, TopicDefinition[] Topics, DraftQuestion[] Questions);
/// <summary>Approves proposed topic definitions before question tagging continues.</summary>
public sealed record DefinitionsUpdate(string Revision, TopicDefinition[] Topics);
/// <summary>The latest storage revision required for a state-changing action.</summary>
public sealed record RevisionUpdate(string Revision);

public sealed record ImportWork(PdfContent Exam, PdfContent Answers, DraftQuestion[] Inventory,
    VerifiedDocuments Documents, ImportSourceBlock[] Blocks, int ClassifiedCount, int ModelCalls,
    string PromptVersion, string ModelDeployment, DocumentInventory? ExamInventory = null,
    DocumentInventory? AnswerInventory = null, bool ContextPrepared = false, string? PendingCall = null,
    ClassificationResult[]? Classifications = null, TopicDefinition[]? ApprovedTopics = null);

public sealed record DocumentInventory(string SubjectId, string Level, int Year, int Timevak,
    string ExamCode, string Role, int QuestionCount, QuestionSource[] Questions);

public sealed record PageImage(int Page, string DataUrl, string Role = "context");

/// <summary>A published trainer question with protected source-document URLs.</summary>
public sealed record CatalogQuestion(string Id, string[] Topics, string Title, string ExamFile,
    int ExamPage, string AnswerFile, int AnswerPage, int Year, int Timevak, int Number, string SubjectId);

/// <summary>A published subject/sitting and its aligned exam/correction document pair.</summary>
public sealed record CatalogExam(string Id, string SubjectId, string Level, int Year, int Timevak,
    string ExamFile, string AnswerFile);

/// <summary>An enabled exam subject.</summary>
public sealed record SubjectResponse(string Id, string Title);

/// <summary>The published subject catalog; unpublished drafts are excluded.</summary>
public sealed record CatalogResponse(SubjectResponse[] Subjects, TopicDefinition[] Topics,
    CatalogQuestion[] Questions, CatalogExam[] Exams);

public class ImportProblem(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>An earlier import that already holds a document with the same SHA-256 digest.</summary>
public sealed record DuplicateImport(string ImportId, string State, int Year, int Timevak, string Role, string DocumentId);

/// <summary>A hash index row: the first stored original with this digest.</summary>
public sealed record DocumentHashEntry(string Sha256, string DocumentId, string Role, string ImportId, DateTimeOffset FirstSeen);

public sealed class ImportDuplicateProblem(DuplicateImport[] duplicates)
    : ImportProblem(409, "Dit document is al eerder verwerkt.")
{
    public DuplicateImport[] Duplicates { get; } = duplicates;
}

/// <summary>
/// Worker-written live progress for one processing attempt. The import record stays authoritative for state;
/// this blob describes what the leased worker is doing right now and keeps the step log: <c>Phases</c> for this
/// attempt (run number <c>Run</c>) and <c>EarlierRuns</c> for the attempts before it. The step-log fields are
/// optional so progress written before they existed still deserializes.
/// </summary>
public sealed record ImportProgress(string ImportId, int Attempt, long Sequence, string Phase, int? Step, int? Total,
    string? Unit, int ModelCalls, int ModelCallBudget, string Message, DateTimeOffset UpdatedAt, DateTimeOffset HeartbeatAt,
    DateTimeOffset? AttemptStartedAt = null, DateTimeOffset? CompletedAt = null, string? Outcome = null,
    ImportPhaseTiming[]? Phases = null, ImportRunTiming[]? EarlierRuns = null, bool EarlierRunsUnknown = false, int Run = 0);

/// <summary>
/// One step of a processing run. <c>EndedAt</c> and <c>Outcome</c> are null while it runs. <c>Message</c>,
/// <c>Step</c> and <c>Total</c> are the latest values reported in the step; <c>ModelCalls</c> are the calls started in
/// it. Outcomes: <c>ok</c>, <c>throttled</c> (a Foundry rate-limit wait, phase <c>throttled</c>), <c>failed</c> (with
/// the short <c>Error</c>), <c>cancelled</c> and <c>interrupted</c> (the worker stopped).
/// </summary>
public sealed record ImportPhaseTiming(string Phase, DateTimeOffset StartedAt, DateTimeOffset? EndedAt = null,
    string? Message = null, int? Step = null, int? Total = null, int ModelCalls = 0, string? Outcome = null, string? Error = null);

/// <summary>
/// One processing run (worker attempt). <c>Number</c> counts the runs of the import (1, 2, ...), also across retries
/// that reset the record's attempt counter; <c>Attempt</c> is that counter. <c>Outcome</c> is <c>awaitingDefinitions</c>,
/// <c>review</c>, <c>failed</c>, <c>cancelled</c>, <c>interrupted</c> (it ends at the last heartbeat) or null while it runs.
/// </summary>
public sealed record ImportRunTiming(int Number, int Attempt, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Outcome,
    ImportPhaseTiming[] Phases);

/// <summary>How many runs an <see cref="ImportTiming"/> carries.</summary>
public enum ImportTimingDetail { Summary, CurrentRun, AllRuns }

/// <summary>
/// Processing time and step log of an import, derived from its progress blob. <c>ProcessingMilliseconds</c> sums
/// the run durations, so the time between runs (waiting on the user at <c>awaitingDefinitions</c>, a failed import
/// waiting for a retry, the queue) is excluded; it is null until the latest run has completed.
/// <c>EarlierProcessingMilliseconds</c> is the part of the earlier runs, for a live total. Both, and
/// <c>ProcessingStartedAt</c>, are null when earlier runs have no step log (<c>EarlierRunsUnknown</c>).
/// <c>Runs</c> is oldest first: all runs, only the current one, or none, depending on <see cref="ImportTimingDetail"/>.
/// </summary>
public sealed record ImportTiming(DateTimeOffset? ProcessingStartedAt, DateTimeOffset? CompletedAt, string? Outcome,
    long? ProcessingMilliseconds, long? EarlierProcessingMilliseconds, bool EarlierRunsUnknown, ImportRunTiming[] Runs)
{
    public static ImportTiming? From(ImportProgress? progress, ImportTimingDetail detail)
    {
        if (progress?.AttemptStartedAt is not { } started) return null;
        var current = new ImportRunTiming(progress.Run, progress.Attempt, started, progress.CompletedAt, progress.Outcome,
            progress.Phases ?? []);
        var earlier = progress.EarlierRuns ?? [];
        var unknown = progress.EarlierRunsUnknown;
        long? earlierTotal = unknown ? null : earlier.Sum(Duration);
        long? total = earlierTotal is { } sum && progress.CompletedAt is not null ? sum + Duration(current) : null;
        ImportRunTiming[] runs = detail switch
        {
            ImportTimingDetail.AllRuns => [.. earlier, current],
            ImportTimingDetail.CurrentRun => [current],
            _ => []
        };
        return new ImportTiming(unknown ? null : earlier.FirstOrDefault()?.StartedAt ?? started, progress.CompletedAt,
            progress.Outcome, total, earlierTotal, unknown, runs);
    }

    private static long Duration(ImportRunTiming run) =>
        Math.Max(0L, (long)((run.EndedAt ?? run.StartedAt) - run.StartedAt).TotalMilliseconds);
}

/// <summary>A conditionally read blob: Value is null when the blob does not exist.</summary>
public sealed record Versioned<T>(T? Value, string? ETag, DateTimeOffset? LastModified) where T : class;
