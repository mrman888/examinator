using UglyToad.PdfPig;

namespace Examinator.Api;

public sealed record PdfWord(string Text, double Left, double Bottom, double Right, double Top);
public sealed record PdfPageContent(int Page, double Width, double Height, bool HasImages,
    bool RequiresVisualReview, PdfWord[] Words);
public sealed record PdfContent(PdfPageContent[] Pages);

/// <summary>Extracts physical pages and word coordinates; it does not infer visual content.</summary>
public static class PdfExtraction
{
    public const long MaximumFileBytes = 20 * 1024 * 1024;
    private const int MaximumWords = 200_000;

    public static PdfContent Extract(Stream input, CancellationToken cancellationToken)
    {
        if (!input.CanRead || !input.CanSeek || input.Length > MaximumFileBytes || input.Length < 5)
            throw new InvalidDataException("Ongeldige PDF-grootte of bestandsstroom.");
        input.Position = 0;
        Span<byte> signature = stackalloc byte[5];
        input.ReadExactly(signature);
        if (!signature.SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("Het bestand heeft geen geldige PDF-signatuur.");
        input.Position = 0;

        try
        {
            using var document = PdfDocument.Open(input);
            if (document.NumberOfPages is < 1 or > ImportValidation.MaximumPages)
                throw new InvalidDataException("Het aantal PDF-pagina's valt buiten de toegestane limieten.");
            var pages = new List<PdfPageContent>(document.NumberOfPages);
            var wordCount = 0;
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var words = new List<PdfWord>();
                foreach (var word in page.GetWords())
                {
                    if (++wordCount > MaximumWords)
                        throw new InvalidDataException("De PDF-tekst overschrijdt de extractielimiet.");
                    var box = word.BoundingBox;
                    words.Add(new PdfWord(word.Text, box.Left, box.Bottom, box.Right, box.Top));
                }
                // Images are not the only visual content: figures can be vector graphics.
                // Until the page has been visually examined, text alone cannot establish
                // that the context is complete, including on apparently text-only pages.
                pages.Add(new PdfPageContent(page.Number, page.Width, page.Height,
                    page.NumberOfImages > 0, true, words.ToArray()));
            }
            return new PdfContent(pages.ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("De PDF is versleuteld, beschadigd of onleesbaar.", exception);
        }
    }
}
