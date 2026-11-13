using System.Text.Json.Serialization;

namespace Examinator.Api;

public sealed record ImportSourceBlock(string Id, int Page, string Text);
public sealed record TopicMatch([property: JsonRequired] string TopicId, [property: JsonRequired] string Reason,
    [property: JsonRequired] string[] EvidenceBlockIds);
public sealed record ClassificationResult([property: JsonRequired] string SubjectId,
    [property: JsonRequired] string QuestionId, [property: JsonRequired] int Number,
    [property: JsonRequired] TopicMatch[] Topics, [property: JsonRequired] bool NeedsReview,
    [property: JsonRequired] string[] Warnings);
