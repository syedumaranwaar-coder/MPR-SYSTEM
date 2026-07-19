using OpenCvSharp;

namespace MPR.Infrastructure.Services.Ocr;

public record MarkClassification(string? Label, double Confidence, bool IsBlank, bool IsStruckOut);

/// <summary>
/// Classifies a single cropped cell as one of the closed set of marks used on these
/// sheets (v, A, L, H, N, P) or blank/struck-through, using template matching rather
/// than general OCR.
///
/// Why template matching instead of Tesseract: validated against the sample sheets
/// during development - Tesseract reads printed header/date text fine but reliably
/// misreads the single handwritten glyphs (checkmarks, single letters) that make up
/// the attendance/homework marks. Since the vocabulary here is tiny and closed
/// (6 possible letters/ticks + blank + struck-through), template matching against a
/// small labeled reference library is both simpler and more accurate than a general
/// text OCR model for this specific problem.
///
/// Also validated: cropping a cell exactly to its grid boundary picks up the printed
/// border lines themselves, which pollutes both ink-density and template-match
/// scoring. Insetting the crop by a small margin before analysis (see Inset below)
/// removes this and gives a clean blank/non-blank separation.
/// </summary>
public class MarkClassifier
{
    private const int Inset = 6;
    private const double BlankInkThreshold = 0.02;   // fraction of ink pixels below which a cell counts as blank
    private const double StrikeThroughAspectRatio = 3.0; // a long thin dark run spanning the cell => strike-through

    private readonly ITemplateLibrary _templates;

    public MarkClassifier(ITemplateLibrary templates) => _templates = templates;

    public MarkClassification Classify(Mat cellColor)
    {
        using var gray = new Mat();
        Cv2.CvtColor(cellColor, gray, ColorConversionCodes.BGR2GRAY);

        int h = gray.Rows, w = gray.Cols;
        if (h <= Inset * 2 || w <= Inset * 2)
            return new MarkClassification(null, 0, IsBlank: true, IsStruckOut: false);

        using var inset = new Mat(gray, new Rect(Inset, Inset, w - 2 * Inset, h - 2 * Inset));
        using var bw = new Mat();
        Cv2.Threshold(inset, bw, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        double inkRatio = Cv2.CountNonZero(bw) / (double)(bw.Rows * bw.Cols);

        if (inkRatio < BlankInkThreshold)
            return new MarkClassification(null, 1.0, IsBlank: true, IsStruckOut: false);

        bool struckOut = DetectStrikeThrough(bw);

        var (label, score) = MatchAgainstTemplates(bw);
        return new MarkClassification(label, score, IsBlank: false, IsStruckOut: struckOut);
    }

    /// <summary>
    /// A strike-through shows up as a long, thin, roughly-diagonal or horizontal
    /// dark run crossing most of the cell width, distinct from the compact blob
    /// shape of a v/A/L/H/N/P glyph. Uses contour bounding-box aspect ratio as a
    /// cheap first-pass signal; flagged cells should still surface in the wizard's
    /// review step rather than being trusted outright.
    /// </summary>
    private static bool DetectStrikeThrough(Mat bw)
    {
        Cv2.FindContours(bw, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var c in contours)
        {
            var rect = Cv2.BoundingRect(c);
            double aspect = rect.Width / (double)Math.Max(rect.Height, 1);
            if (aspect >= StrikeThroughAspectRatio && rect.Width >= bw.Cols * 0.6)
                return true;
        }
        return false;
    }

    private (string? label, double confidence) MatchAgainstTemplates(Mat cellBw)
    {
        string? bestLabel = null;
        double bestScore = 0;

        foreach (var (label, template) in _templates.GetAll())
        {
            using var resizedTemplate = new Mat();
            Cv2.Resize(template, resizedTemplate, cellBw.Size());

            using var result = new Mat();
            Cv2.MatchTemplate(cellBw, resizedTemplate, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal);

            if (maxVal > bestScore)
            {
                bestScore = maxVal;
                bestLabel = label;
            }
        }

        // No confident match against the current library -> null forces the wizard's
        // extraction-review step rather than guessing.
        return bestScore >= 0.45 ? (bestLabel, bestScore) : (null, bestScore);
    }
}

/// <summary>
/// Stores and retrieves labeled glyph exemplars (one or more crops per label:
/// "v", "A", "L", "H", "N", "P"). Starts empty for a fresh deployment - every
/// correction a user makes in the wizard's Extraction Review step (see
/// ReportWizardController.UpdateCell) should call AddExemplar so the library
/// grows from that teacher's/scanner's actual handwriting instead of requiring
/// an upfront labeled dataset.
/// </summary>
public interface ITemplateLibrary
{
    IEnumerable<(string Label, Mat Template)> GetAll();
    void AddExemplar(string label, Mat crop);
}

public class FileSystemTemplateLibrary : ITemplateLibrary
{
    private readonly string _rootPath;

    public FileSystemTemplateLibrary(string rootPath)
    {
        _rootPath = rootPath;
        Directory.CreateDirectory(_rootPath);
    }

    public IEnumerable<(string Label, Mat Template)> GetAll()
    {
        foreach (var dir in Directory.GetDirectories(_rootPath))
        {
            var label = Path.GetFileName(dir);
            foreach (var file in Directory.GetFiles(dir, "*.png"))
                yield return (label, Cv2.ImRead(file, ImreadModes.Grayscale));
        }
    }

    public void AddExemplar(string label, Mat crop)
    {
        var dir = Path.Combine(_rootPath, label);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}.png");
        crop.SaveImage(path);
    }
}
