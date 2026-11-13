namespace Examinator.Api;

/// <summary>Source inventories must be created independently for each document.</summary>
public sealed record QuestionSource(int Number, string Title, int StartPage, int EndPage);

public static class QuestionAlignment
{
    public static DraftQuestion[] Align(ImportMetadata metadata, IReadOnlyList<QuestionSource> questions,
        IReadOnlyList<QuestionSource> answers, int examPages, int answerPages)
    {
        if (!ImportValidation.ValidMetadata(metadata) || questions.Count is < 1 or > ImportValidation.MaximumQuestions ||
            answers.Count > ImportValidation.MaximumQuestions * 2 ||
            questions.Any(q => !ValidSource(q, examPages)) ||
            answers.Any(q => !ValidSource(q, answerPages)) ||
            questions.Select(q => q.Number).Distinct().Count() != questions.Count)
            throw new InvalidDataException("Ongeldige of dubbelzinnige vraaginventaris.");

        var answersByNumber = answers.GroupBy(q => q.Number).ToDictionary(group => group.Key, group => group.ToArray());
        return questions.Select(question =>
        {
            answersByNumber.TryGetValue(question.Number, out var matches);
            var match = matches is { Length: 1 } ? matches[0] : null;
            var warnings = match is null
                ? new[] { matches is { Length: > 1 } ? "Dubbele antwoordvermelding." : "Ontbrekende antwoordvermelding." }
                : Array.Empty<string>();
            return new DraftQuestion(
                $"{metadata.SubjectId}-{metadata.Level}-{metadata.Year}-{metadata.Timevak}-q{question.Number}",
                question.Number, question.Title, question.StartPage, question.EndPage,
                match?.StartPage, match?.EndPage, [], [], true, warnings);
        }).ToArray();
    }

    private static bool ValidSource(QuestionSource? source, int pages) =>
        source is not null && source.Number is > 0 and <= 999 &&
        !string.IsNullOrWhiteSpace(source.Title) && source.Title.Length <= 500 &&
        pages is >= 1 and <= ImportValidation.MaximumPages &&
        source.StartPage >= 1 && source.EndPage >= source.StartPage && source.EndPage <= pages;
}
