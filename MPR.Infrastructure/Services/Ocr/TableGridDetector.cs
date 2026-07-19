using OpenCvSharp;

namespace MPR.Infrastructure.Services.Ocr;

public record TableGrid(List<int> RowBoundariesY, List<int> ColBoundariesX);

/// <summary>
/// Locates the printed table grid lines on a rendered attendance-sheet page using
/// morphological line extraction, so cell boundaries are derived from the actual
/// scan every time instead of hardcoded per-template pixel offsets.
///
/// Approach validated against the sample PDFs before writing this: adaptive
/// threshold -> erode/dilate with long horizontal and vertical structuring
/// elements isolates the printed grid lines even where handwritten marks overlap
/// them; clustering the resulting line pixels gives stable row/column boundaries.
/// This also naturally picks up the inner divider between a student's attendance
/// mark (v/A/L) and homework mark (H/N/P) within the same row, since that divider
/// is drawn with the same line weight as the outer grid on these sheets.
/// </summary>
public static class TableGridDetector
{
    public static TableGrid Detect(Mat pageGray)
    {
        using var inverted = new Mat();
        Cv2.BitwiseNot(pageGray, inverted);

        using var thresh = new Mat();
        Cv2.AdaptiveThreshold(inverted, thresh, 255, AdaptiveThresholdTypes.MeanC, ThresholdTypes.Binary, 15, -2);

        var rowYs = Cluster(ExtractLinePositions(thresh, horizontal: true));
        var colXs = Cluster(ExtractLinePositions(thresh, horizontal: false));

        return new TableGrid(rowYs, colXs);
    }

    private static List<int> ExtractLinePositions(Mat thresh, bool horizontal)
    {
        int size = horizontal ? thresh.Cols / 30 : thresh.Rows / 30;
        size = Math.Max(size, 5);

        using var structuringElement = horizontal
            ? Cv2.GetStructuringElement(MorphShapes.Rect, new Size(size, 1))
            : Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, size));

        using var eroded = new Mat();
        using var lines = new Mat();
        Cv2.Erode(thresh, eroded, structuringElement);
        Cv2.Dilate(eroded, lines, structuringElement);

        var positions = new List<int>();
        if (horizontal)
        {
            for (int y = 0; y < lines.Rows; y++)
            {
                using var row = lines.Row(y);
                if (Cv2.Sum(row).Val0 > 0.3 * 255 * lines.Cols * 0.1)
                    positions.Add(y);
            }
        }
        else
        {
            for (int x = 0; x < lines.Cols; x++)
            {
                using var col = lines.Col(x);
                if (Cv2.Sum(col).Val0 > 0.3 * 255 * lines.Rows * 0.1)
                    positions.Add(x);
            }
        }
        return positions;
    }

    /// <summary>Collapses runs of adjacent line-pixel positions (within `gap`) into one boundary each.</summary>
    private static List<int> Cluster(List<int> values, int gap = 5)
    {
        if (values.Count == 0) return new List<int>();
        values.Sort();
        var clusters = new List<List<int>> { new() { values[0] } };
        foreach (var v in values.Skip(1))
        {
            if (v - clusters[^1][^1] <= gap) clusters[^1].Add(v);
            else clusters.Add(new List<int> { v });
        }
        return clusters.Select(c => (int)c.Average()).ToList();
    }
}
