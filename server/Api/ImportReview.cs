namespace Examinator.Api;

public sealed record VerifiedDocuments(int ExamPages, int AnswerPages, bool ExamIdentityVerified, bool AnswerIdentityVerified);

/// <summary>Pure revision-checked transitions. Persistence must also use the same ETag.</summary>
public static class ImportReview
{
    public static ImportRecord SaveDraft(ImportRecord record, DraftUpdate update,
        IReadOnlyList<DraftQuestion> inventory, VerifiedDocuments documents)
    {
        RequireRevisionAndState(record, update.Revision, "review");
        if (!ImportValidation.ValidDraft(update.Topics, update.Questions, inventory,
                documents.ExamPages, documents.AnswerPages))
            throw new InvalidDataException("Draft contains invalid topics, questions or page references.");
        return record with { Topics = update.Topics, Questions = update.Questions };
    }

    public static ImportRecord ConfirmDefinitions(ImportRecord record, DefinitionsUpdate update)
    {
        RequireRevisionAndState(record, update.Revision, "awaitingDefinitions");
        if (!ImportValidation.ValidTopics(update.Topics) || update.Topics.Length == 0)
            throw new InvalidDataException("Invalid topic definitions.");
        return record with { Topics = update.Topics, State = "queued", Error = null };
    }

    public static ImportRecord Publish(ImportRecord record, RevisionUpdate update,
        IReadOnlyList<DraftQuestion> inventory, VerifiedDocuments documents)
    {
        RequireRevisionAndState(record, update.Revision, "review");
        if (!documents.ExamIdentityVerified || !documents.AnswerIdentityVerified ||
            !ImportValidation.ValidDraft(record.Topics, record.Questions, inventory, documents.ExamPages, documents.AnswerPages) ||
            !ImportValidation.CanPublishQuestions(record.Questions))
            throw new InvalidDataException("Document identity, question references or review are unresolved.");
        return record with { State = "published" };
    }

    public static ImportRecord Cancel(ImportRecord record, RevisionUpdate update)
    {
        RequireRevision(record, update.Revision);
        if (record.State is "published" or "cancelled")
            throw new InvalidOperationException("The import cannot be cancelled in this state.");
        return record with { State = "cancelled" };
    }

    public static ImportRecord Retry(ImportRecord record, RevisionUpdate update)
    {
        RequireRevisionAndState(record, update.Revision, "failed");
        return record with { State = "queued", Error = null, ProcessingAttempts = 0 };
    }

    private static void RequireRevisionAndState(ImportRecord record, string? revision, string state)
    {
        RequireRevision(record, revision);
        if (record.State != state) throw new InvalidOperationException("The import is not in the required state.");
    }

    private static void RequireRevision(ImportRecord record, string? revision)
    {
        if (string.IsNullOrEmpty(revision) || !string.Equals(record.Revision, revision, StringComparison.Ordinal))
            throw new InvalidOperationException("The import revision has changed.");
    }
}
