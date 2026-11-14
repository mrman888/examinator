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

/// <summary>Durable import status, source references, and revision-checked review data.</summary>
public sealed record ImportRecord(
    string Id, string State, ImportMetadata Metadata, TopicDefinition[] Topics,
    DraftQuestion[] Questions, string[] Warnings, string? Error, string Revision,
    string ExamDocumentId, string AnswerDocumentId, DateTimeOffset CreatedAt,
    string? ContextDocumentId = null, string? ContextText = null, int ProcessingAttempts = 0);

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
/// this blob only describes what the leased worker is doing right now.
/// </summary>
public sealed record ImportProgress(string ImportId, int Attempt, long Sequence, string Phase, int? Step, int? Total,
    string? Unit, int ModelCalls, int ModelCallBudget, string Message, DateTimeOffset UpdatedAt, DateTimeOffset HeartbeatAt);

/// <summary>A conditionally read blob: Value is null when the blob does not exist.</summary>
public sealed record Versioned<T>(T? Value, string? ETag, DateTimeOffset? LastModified) where T : class;
