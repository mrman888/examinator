using Examinator.Api;

namespace Examinator.Tests;

public sealed class ImportReviewTests
{
    private static readonly TopicDefinition Topic = new("biology-cells", "Cellen", "Celstructuur", [], [], [], null, null);
    private static readonly DraftQuestion Question = new("q1", 1, "Question", 1, 2, 1, 1, [Topic.Id], [], false, []);
    private static readonly VerifiedDocuments Documents = new(2, 2, true, true);
    private static readonly ImportRecord Record = new("id", "review",
        new("biology", "havo", 2025, 1, "", "", [Topic]), [Topic], [Question], [], null, "\"v1\"",
        "exam", "answers", DateTimeOffset.UtcNow);

    [Fact]
    public void PublicationRequiresVerifiedDocumentIdentitiesAndResolvedReferences()
    {
        Assert.Equal("published", ImportReview.Publish(Record, new("\"v1\""), [Question], Documents).State);
        Assert.Throws<InvalidDataException>(() => ImportReview.Publish(Record, new("\"v1\""), [Question],
            Documents with { AnswerIdentityVerified = false }));
        Assert.Throws<InvalidDataException>(() => ImportReview.Publish(Record with
        {
            Questions = [Question with { NeedsReview = true }]
        }, new("\"v1\""), [Question], Documents));
        Assert.Throws<InvalidDataException>(() => ImportReview.Publish(Record with
        {
            Questions = [Question with { ExamEndPage = 3 }]
        }, new("\"v1\""), [Question], Documents));
    }

    [Fact]
    public void MutationsRejectStaleRevisionsAndWrongStates()
    {
        Assert.Throws<InvalidOperationException>(() => ImportReview.Publish(Record, new("\"stale\""), [Question], Documents));
        Assert.Throws<InvalidOperationException>(() => ImportReview.Retry(Record, new("\"v1\"")));
        Assert.Equal("queued", ImportReview.Retry(Record with { State = "failed" }, new("\"v1\"")).State);
        Assert.Equal("cancelled", ImportReview.Cancel(Record, new("\"v1\"")).State);
        Assert.Throws<InvalidOperationException>(() => ImportReview.Cancel(Record with { State = "published" }, new("\"v1\"")));
    }

    [Fact]
    public void DefinitionConfirmationRequeuesWithoutPublishing()
    {
        var result = ImportReview.ConfirmDefinitions(Record with { State = "awaitingDefinitions" },
            new("\"v1\"", [Topic]));
        Assert.Equal("queued", result.State);
        Assert.Equal(Topic.Id, result.Topics[0].Id);
        Assert.Throws<InvalidDataException>(() => ImportReview.ConfirmDefinitions(
            Record with { State = "awaitingDefinitions" }, new("\"v1\"", [])));
    }
}
