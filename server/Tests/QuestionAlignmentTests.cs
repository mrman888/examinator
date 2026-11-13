using Examinator.Api;

namespace Examinator.Tests;

public sealed class QuestionAlignmentTests
{
    private static readonly ImportMetadata Metadata = new("biology", "havo", 2025, 1, "", "", []);

    [Fact]
    public void MatchesNumbersNotPagePositionsAndPreservesRanges()
    {
        var result = QuestionAlignment.Align(Metadata,
            [new(1, "First", 2, 3), new(2, "Second", 4, 4)],
            [new(2, "Second answer", 2, 3), new(1, "First answer", 7, 8)], 5, 8);
        Assert.Equal(7, result[0].AnswerPage);
        Assert.Equal(8, result[0].AnswerEndPage);
        Assert.Equal(2, result[1].AnswerPage);
        Assert.All(result, question => Assert.True(question.NeedsReview));
    }

    [Fact]
    public void DuplicateOrMissingAnswersRemainUnresolved()
    {
        var result = QuestionAlignment.Align(Metadata,
            [new(1, "First", 1, 1), new(2, "Second", 2, 2)],
            [new(1, "Answer", 1, 1), new(1, "Answer again", 2, 2)], 2, 2);
        Assert.All(result, question => Assert.Null(question.AnswerPage));
        Assert.Contains("Dubbele", result[0].Warnings[0]);
        Assert.Contains("Ontbrekende", result[1].Warnings[0]);
    }

    [Fact]
    public void RejectsDuplicateQuestionNumbersAndInvalidPhysicalPages()
    {
        Assert.Throws<InvalidDataException>(() => QuestionAlignment.Align(Metadata,
            [new(1, "First", 1, 1), new(1, "Repeated", 2, 2)], [], 2, 2));
        Assert.Throws<InvalidDataException>(() => QuestionAlignment.Align(Metadata,
            [new(1, "First", 1, 3)], [], 2, 2));
    }
}
