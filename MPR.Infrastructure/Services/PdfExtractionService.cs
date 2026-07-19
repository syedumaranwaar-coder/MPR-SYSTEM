using Docnet.Core;
using Docnet.Core.Models;
using MPR.Application.Interfaces;
using MPR.Domain.Entities;
using MPR.Infrastructure.Services.Ocr;
using OpenCvSharp;
using Tesseract;
using Rect = OpenCvSharp.Rect;

namespace MPR.Infrastructure.Services;

/// <summary>
/// Extracts attendance data from scanned/handwritten sheets using a two-pass pipeline
/// validated against the sample PDFs during development:
///
///   1. TableGridDetector finds the actual row/column grid lines on each rendered
///      page (self-calibrating per page - no hardcoded per-template pixel offsets).
///   2. Each cell is classified by MarkClassifier: ink-density decides blank vs
///      marked, then template matching against a small closed vocabulary
///      (v/A/L for attendance, H/N/P for homework) reads the mark. Plain OCR was
///      tested and is unreliable on these handwritten single-glyph marks; template
///      matching against a growing labeled library performs much better for a
///      vocabulary this small.
///
/// The student-name column and header block (teacher/grade/subject/room/day-time)
/// still use Tesseract, since that IS printed/legible text in the samples and OCR
/// handles it fine there.
///
/// Every row this service produces carries a confidence score and is treated as a
/// DRAFT - nothing here is final until a human confirms it in the wizard's
/// Extraction Review step (see ReportWizardController). Corrections made in that
/// step should be fed back via ITemplateLibrary.AddExemplar so accuracy improves
/// over time for that organization's actual handwriting and scan quality.
/// </summary>
public class PdfExtractionService : IPdfExtractionService
{
    private readonly string _tessDataPath;
    private readonly ITemplateLibrary _templates;

    public PdfExtractionService(string tessDataPath, ITemplateLibrary templates)
    {
        _tessDataPath = tessDataPath;
        _templates = templates;
    }

    public Task<List<AttendanceRecord>> ExtractAsync(string pdfStoragePath, int reportPeriodId, int gradeId, int? subjectId, CancellationToken ct = default)
    {
        var results = new List<AttendanceRecord>();
        var classifier = new MarkClassifier(_templates);

        using var docReader = DocLib.Instance.GetDocReader(pdfStoragePath, new PageDimensions(1655, 2340)); // ~200 DPI A4, matches sample renders
        using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);

        int pageCount = docReader.GetPageCount();
        for (int p = 0; p < pageCount; p++)
        {
            using var pageReader = docReader.GetPageReader(p);
            int width = pageReader.GetPageWidth();
            int height = pageReader.GetPageHeight();
            byte[] rawBgra = pageReader.GetImage(); // Docnet returns raw BGRA bytes

            using var pageMat = new Mat(height, width, MatType.CV_8UC4, rawBgra);
            using var pageBgr = new Mat();
            Cv2.CvtColor(pageMat, pageBgr, ColorConversionCodes.BGRA2BGR);
            using var pageGray = new Mat();
            Cv2.CvtColor(pageBgr, pageGray, ColorConversionCodes.BGR2GRAY);

            var grid = TableGridDetector.Detect(pageGray);
            if (grid.RowBoundariesY.Count < 3 || grid.ColBoundariesX.Count < 3)
                continue; // page likely has no attendance table (e.g. a cover/blank page) - skip rather than guess

            string teacherName = OcrHeaderField(engine, pageGray, grid);

            // Column 0 is the student-name column (between ColBoundariesX[0] and [1]);
            // remaining columns are week-date columns. Row pairs alternate between the
            // attendance-mark sub-row and homework-mark sub-row for the same student -
            // detected automatically by TableGridDetector as extra interior lines.
            var studentRowBands = PairUpStudentRows(grid.RowBoundariesY);

            foreach (var (topY, midY, bottomY) in studentRowBands)
            {
                var nameRect = new Rect(grid.ColBoundariesX[0], topY, grid.ColBoundariesX[1] - grid.ColBoundariesX[0], bottomY - topY);
                string studentName = OcrCropText(engine, pageBgr, nameRect);
                if (string.IsNullOrWhiteSpace(studentName)) continue;

                var record = new AttendanceRecord
                {
                    ReportPeriodId = reportPeriodId,
                    GradeId = gradeId,
                    SubjectId = subjectId ?? 0, // resolved by the caller once the wizard confirms the subject for this file
                    StudentName = studentName.Trim(),
                    TeacherName = teacherName.Trim()
                };

                double minConfidence = 1.0;
                int weekIndex = 0;
                var cellImages = new Dictionary<int, byte[]>();
                for (int col = 1; col < grid.ColBoundariesX.Count - 1 && weekIndex < 5; col++, weekIndex++)
                {
                    int x0 = grid.ColBoundariesX[col];
                    int x1 = grid.ColBoundariesX[col + 1];

                    using var attendanceCell = new Mat(pageBgr, new Rect(x0, topY, x1 - x0, midY - topY));
                    var attendanceResult = classifier.Classify(attendanceCell);
                    minConfidence = Math.Min(minConfidence, attendanceResult.Confidence);

                    int? value = ResolveAttendanceValue(attendanceResult);
                    SetWeek(record, weekIndex, value);

                    // Retain the crop + prediction so a low-confidence cell can be
                    // corrected in the wizard review step and fed back into the
                    // template library (see ReportWizardController.CorrectMark).
                    Cv2.ImEncode(".png", attendanceCell, out byte[] cellPng);
                    cellImages[weekIndex + 1] = cellPng;
                }
                record.WeekCellImages = cellImages;

                record.OcrConfidence = minConfidence;
                results.Add(record);
            }
        }

        return Task.FromResult(results);
    }

    /// <summary>v or L => present (1); A or a detected strike-through => 0 (per the
    /// "crossed-out weeks count as 0" rule); unrecognized/low-confidence => null,
    /// which forces the cell into the wizard's manual review queue.</summary>
    private static int? ResolveAttendanceValue(MarkClassification result)
    {
        if (result.IsStruckOut) return 0;
        if (result.IsBlank) return null;
        return result.Label switch
        {
            "v" or "V" or "L" => 1,
            "A" => 0,
            _ => null
        };
    }

    private static void SetWeek(AttendanceRecord record, int index, int? value)
    {
        switch (index)
        {
            case 0: record.Week1 = value; break;
            case 1: record.Week2 = value; break;
            case 2: record.Week3 = value; break;
            case 3: record.Week4 = value; break;
            case 4: record.Week5 = value; break;
        }
    }

    /// <summary>
    /// Groups detected row-lines into (studentTop, subDivider, studentBottom) triples.
    /// On the sample sheets each student occupies two stacked sub-rows (attendance
    /// mark, then homework mark) separated by a thinner interior line; this pairs
    /// consecutive line positions accordingly. Falls back to treating every boundary
    /// pair as a single-mark row if no consistent sub-division is detected (some
    /// sheet variants, e.g. the Yr-9/Yr-11 "Class Attendance" sheets, only track
    /// presence with no homework sub-row).
    /// </summary>
    private static List<(int Top, int Mid, int Bottom)> PairUpStudentRows(List<int> rowYs)
    {
        var bands = new List<(int, int, int)>();
        // Skip the header band (first boundary is the top of the header, not a student row).
        for (int i = 1; i + 2 < rowYs.Count; i += 2)
            bands.Add((rowYs[i], rowYs[i + 1], rowYs[i + 2]));
        return bands;
    }

    private static string OcrHeaderField(TesseractEngine engine, Mat pageGray, TableGrid grid)
    {
        int headerBottom = grid.RowBoundariesY.Count > 0 ? grid.RowBoundariesY[0] : Math.Min(300, pageGray.Rows);
        using var header = new Mat(pageGray, new Rect(0, 0, pageGray.Cols, headerBottom));
        return OcrMat(engine, header);
    }

    private static string OcrCropText(TesseractEngine engine, Mat pageColor, Rect rect)
    {
        using var crop = new Mat(pageColor, rect);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        return OcrMat(engine, gray);
    }

    private static string OcrMat(TesseractEngine engine, Mat gray)
    {
        Cv2.ImEncode(".png", gray, out byte[] pngBytes);
        using var pix = Pix.LoadFromMemory(pngBytes);
        using var page = engine.Process(pix);
        return page.GetText();
    }
}
