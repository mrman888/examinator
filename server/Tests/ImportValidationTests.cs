using Examinator.Api;

namespace Examinator.Tests;

public sealed class ImportValidationTests
{
    private static readonly TopicDefinition Cells = new(
        "biology-cells", "Cells", "Cell structure", [], [], [], "2", "2.1");
    private static readonly DraftQuestion Question = new(
        "biology-havo-2025-1-q1", 1, "Question", 1, 2, 1, 1,
        ["biology-cells"], ["Evidence"], false, []);

    [Fact]
    public void MetadataOnlyAcceptsEnabledSubjectAndLevel()
    {
        var metadata = new ImportMetadata("biology", "havo", 2025, 1, "2", "Cells", [Cells]);
        Assert.True(ImportValidation.ValidMetadata(metadata));
        Assert.False(ImportValidation.ValidMetadata(metadata with { SubjectId = "physics" }));
        Assert.False(ImportValidation.ValidMetadata(metadata with { Level = "vwo" }));
    }

    [Fact]
    public void DraftRejectsOutOfBoundsPagesAndUnknownTopics()
    {
        Assert.True(ImportValidation.ValidDraft([Cells], [Question], [Question], 3, 2));
        Assert.False(ImportValidation.ValidDraft([Cells], [Question with { ExamEndPage = 4 }], [Question], 3, 2));
        Assert.False(ImportValidation.ValidDraft([Cells], [Question with { AnswerPage = 3, AnswerEndPage = 3 }], [Question], 3, 2));
        Assert.False(ImportValidation.ValidDraft([Cells], [Question with { TopicIds = ["unknown"] }], [Question], 3, 2));
        Assert.False(ImportValidation.ValidDraft([Cells], [Question with { Number = 2 }], [Question], 3, 2));
    }

    [Fact]
    public void PublicationRequiresResolvedQuestionAndAnswer()
    {
        Assert.True(ImportValidation.CanPublishQuestions([Question]));
        Assert.False(ImportValidation.CanPublishQuestions([]));
        Assert.False(ImportValidation.CanPublishQuestions([Question with { NeedsReview = true }]));
        Assert.False(ImportValidation.CanPublishQuestions([Question with { AnswerPage = null, AnswerEndPage = null }]));
    }

    [Fact]
    public void TopicIdsAreIndependentOfChapterHints()
    {
        Assert.True(ImportValidation.ValidTopics([Cells with { ChapterNumber = "other", ParagraphNumber = null }]));
        Assert.False(ImportValidation.ValidTopics([Cells, Cells with { ChapterNumber = "other" }]));
    }
}
