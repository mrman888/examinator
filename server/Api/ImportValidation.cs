namespace Examinator.Api;

public static class ImportValidation
{
    public const int MaximumPages = 100;
    public const int MaximumTopics = 30;
    public const int MaximumQuestions = 200;

    public static bool ValidTopics(TopicDefinition[]? topics)
    {
        if (topics is null || topics.Length > MaximumTopics) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in topics)
        {
            if (topic is null || !ValidId(topic.Id) || !ids.Add(topic.Id) ||
                !Text(topic.Title, 200) || !Text(topic.Description, 2000) ||
                !TextArray(topic.Keywords) || !TextArray(topic.Exclusions) || !TextArray(topic.Aliases) ||
                !OptionalText(topic.ChapterNumber, 100) || !OptionalText(topic.ParagraphNumber, 100))
                return false;
        }
        return true;
    }

    public static bool ValidMetadata(ImportMetadata? metadata) =>
        ValidMetadataFields(metadata) && metadata!.Year is >= 2000 and <= 2100 &&
        metadata.Timevak is >= 1 and <= 3;

    public static bool ValidUploadMetadata(ImportMetadata? metadata) =>
        ValidMetadataFields(metadata) &&
        ((metadata!.Year == 0 && metadata.Timevak == 0) ||
            (metadata.Year is >= 2000 and <= 2100 && metadata.Timevak is >= 1 and <= 3));

    private static bool ValidMetadataFields(ImportMetadata? metadata) =>
        metadata is not null && metadata.SubjectId == "biology" && metadata.Level == "havo" &&
        metadata.ChapterNumber is not null && metadata.ChapterTitle is not null &&
        OptionalText(metadata.ChapterNumber, 100) && OptionalText(metadata.ChapterTitle, 200) &&
        ValidTopics(metadata.Topics);

    public static bool ValidDraft(TopicDefinition[]? topics, DraftQuestion[]? questions,
        IReadOnlyList<DraftQuestion> originalQuestions, int examPages, int answerPages)
    {
        if (!ValidTopics(topics) || topics!.Length == 0 || questions is null || questions.Length > MaximumQuestions ||
            questions.Length != originalQuestions.Count || examPages is < 1 or > MaximumPages ||
            answerPages is < 1 or > MaximumPages ||
            originalQuestions.Any(q => q is null || string.IsNullOrEmpty(q.Id)) ||
            originalQuestions.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != originalQuestions.Count ||
            originalQuestions.Select(q => q.Number).Distinct().Count() != originalQuestions.Count) return false;
        var allowedTopics = topics!.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var originals = originalQuestions.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in questions)
        {
            if (q is null || !ValidId(q.Id) || !seen.Add(q.Id) || !originals.TryGetValue(q.Id, out var original) ||
                q.Number != original.Number || !Text(q.Title, 500) ||
                q.ExamPage < 1 || q.ExamEndPage < q.ExamPage || q.ExamEndPage > examPages ||
                (q.AnswerPage is null) != (q.AnswerEndPage is null) ||
                (q.AnswerPage is not null && (q.AnswerPage < 1 || q.AnswerEndPage < q.AnswerPage || q.AnswerEndPage > answerPages)) ||
                q.TopicIds is null || q.TopicIds.Length > MaximumTopics ||
                q.TopicIds.Distinct(StringComparer.Ordinal).Count() != q.TopicIds.Length ||
                q.TopicIds.Any(id => id is null || !allowedTopics.Contains(id)) ||
                !TextArray(q.Reasons) || !TextArray(q.Warnings)) return false;
        }
        return true;
    }

    // This is only the question-level gate; document identity and revision must
    // also be verified before an import may be published.
    public static bool CanPublishQuestions(IReadOnlyCollection<DraftQuestion> questions) =>
        questions.Count > 0 && questions.All(q => !q.NeedsReview && q.AnswerPage is not null &&
            q.AnswerEndPage is not null);

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 100 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static bool OptionalText(string? value, int max) => value is null || value.Length <= max;
    private static bool TextArray(string[]? values) => values is { Length: <= 100 } &&
        values.All(value => Text(value, 2000));
}
