using Examinator.Api;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Examinator.Tests;

public sealed class PdfExtractionTests
{
    [Fact]
    public void ExtractsPhysicalPagesAndWordCoordinates()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(500, 700).AddText("first page", 12, new PdfPoint(30, 600), font);
        builder.AddPage(500, 700).AddText("second page", 12, new PdfPoint(30, 500), font);
        using var input = new MemoryStream(builder.Build());
        var pdf = PdfExtraction.Extract(input, CancellationToken.None);
        Assert.Equal(2, pdf.Pages.Length);
        Assert.Equal(1, pdf.Pages[0].Page);
        Assert.Equal(2, pdf.Pages[1].Page);
        Assert.All(pdf.Pages, page => Assert.True(page.RequiresVisualReview));
        Assert.Contains(pdf.Pages[0].Words, word => word.Text == "first" && word.Left >= 0);
        Assert.Contains(pdf.Pages[1].Words, word => word.Text == "second");
    }

    [Fact]
    public void RejectsInvalidAndUnreadablePdf()
    {
        Assert.Throws<InvalidDataException>(() =>
            PdfExtraction.Extract(new MemoryStream("not a pdf"u8.ToArray()), CancellationToken.None));
        Assert.Throws<InvalidDataException>(() =>
            PdfExtraction.Extract(new MemoryStream("%PDF-broken"u8.ToArray()), CancellationToken.None));
    }

    [Fact]
    public void EmptyPageAlwaysRequiresVisualReview()
    {
        using var builder = new PdfDocumentBuilder();
        builder.AddPage(500, 700);
        using var input = new MemoryStream(builder.Build());
        var page = Assert.Single(PdfExtraction.Extract(input, CancellationToken.None).Pages);
        Assert.Empty(page.Words);
        Assert.True(page.RequiresVisualReview);
    }
}
