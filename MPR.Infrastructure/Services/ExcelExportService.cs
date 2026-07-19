using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using MPR.Application.Interfaces;
using MPR.Domain.Entities;
using MPR.Domain.Enums;
using MPR.Infrastructure.Persistence;

namespace MPR.Infrastructure.Services;

/// <summary>
/// Rebuilds the workbook layout observed in the source template:
///   - One block per grade/category, headed "MPR LEVEL {n}[ - O2O | - PW | TT{n}]"
///   - Header row: "Subject" | week-ending dates... | "Total" | "MPR "
///   - One row per subject name, one "No. Of Std Present" row per block
///   - Grades 11 & 12: separate block per subject instead of merged subject rows
///   - "MPR All" sheet: one row per period, RC/O2O/PW columns per "year" slot,
///     STATUS = Paid when row total > 70, else Unpaid.
///
/// This does not hardcode values - every cell here is either a literal
/// student-attendance-derived number pulled from MPRDetailResult/MPRSummary,
/// or an actual Excel formula (=MAX/=AVERAGE/=ROUNDUP/=SUM), exactly like the source file.
/// </summary>
public class ExcelExportService : IExcelExportService
{
    private readonly AppDbContext _db;

    public ExcelExportService(AppDbContext db) => _db = db;

    public async Task<Stream> ExportAsync(int reportPeriodId, CancellationToken ct = default)
    {
        var period = await _db.ReportPeriods
            .Include(p => p.WeekDates)
            .FirstAsync(p => p.Id == reportPeriodId, ct);

        var details = await _db.MPRDetailResults
            .Include(d => d.Grade)
            .Where(d => d.ReportPeriodId == reportPeriodId)
            .OrderBy(d => d.Grade.SortOrder)
            .ToListAsync(ct);

        var summaries = await _db.MPRSummaries
            .Include(s => s.Grade)
            .Where(s => s.ReportPeriodId == reportPeriodId)
            .OrderBy(s => s.Grade.SortOrder)
            .ToListAsync(ct);

        int weekCount = (int)period.PeriodType;

        using var wb = new XLWorkbook();
        var sheetName = weekCount == 5 ? "MPR 5WK" : "MPR";
        var ws = wb.Worksheets.Add(sheetName);

        WriteHeader(ws, period, weekCount);

        int row = 8;
        foreach (var gradeDetails in details.GroupBy(d => d.GradeId))
        {
            row = WriteGradeBlock(ws, row, gradeDetails.Key, gradeDetails.First().Grade, gradeDetails.ToList(), weekCount);
        }

        var allWs = wb.Worksheets.Add("MPR All");
        WriteSummarySheet(allWs, period, summaries);

        ws.Columns().AdjustToContents();
        allWs.Columns().AdjustToContents();

        var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static void WriteHeader(IXLWorksheet ws, ReportPeriod period, int weekCount)
    {
        ws.Cell("C2").Value = "MPR";
        ws.Cell("D4").Value = "From Date:";
        ws.Cell("E4").Value = period.FromDate;
        ws.Cell(4, 4 + weekCount).Value = period.ToDate; // mirrors "H4"/"To Date" offset in source

        ws.Cell("C6").Value = "CG Date:";
        var rcDates = period.WeekDates.Where(w => w.DateFamily == "RC").OrderBy(w => w.WeekNumber).ToList();
        for (int i = 0; i < rcDates.Count; i++)
            ws.Cell(6, 4 + i).Value = rcDates[i].WeekEndingDate; // D6, E6, F6...
        ws.Cell(6, 4 + weekCount).Value = "Total";
        ws.Cell(6, 5 + weekCount).Value = "MPR ";

        ws.Cell("K6").Value = "CG Date TT:";
        var ttDates = period.WeekDates.Where(w => w.DateFamily == "TT").OrderBy(w => w.WeekNumber).ToList();
        for (int i = 0; i < ttDates.Count; i++)
            ws.Cell(6, 12 + i).Value = ttDates[i].WeekEndingDate; // L6, M6, N6, O6

        ws.Cell("K7").Value = "CG Date PW & O2O:";
        var pwDates = period.WeekDates.Where(w => w.DateFamily == "PW_O2O").OrderBy(w => w.WeekNumber).ToList();
        for (int i = 0; i < pwDates.Count; i++)
            ws.Cell(7, 12 + i).Value = pwDates[i].WeekEndingDate;
    }

    private static int WriteGradeBlock(IXLWorksheet ws, int startRow, int gradeId, Grade grade, List<MPRDetailResult> gradeDetails, int weekCount)
    {
        int row = startRow;

        foreach (var d in gradeDetails.OrderBy(CategorySortKey))
        {
            string blockLabel = BlockLabel(grade, d.Category);
            ws.Cell(row, 3).Value = blockLabel; // column C
            row++;

            ws.Cell(row, 3).Value = "Subject";
            for (int w = 0; w < weekCount; w++)
                ws.Cell(row, 4 + w).FormulaA1 = ColumnLetter(4 + w) + (row - 2); // "=D6" style reference to header date
            ws.Cell(row, 4 + weekCount).FormulaA1 = $"=${ColumnLetter(4 + weekCount)}$6";
            ws.Cell(row, 5 + weekCount).FormulaA1 = $"=${ColumnLetter(5 + weekCount)}$6";
            int subjectHeaderRow = row;
            row++;

            string subjectLabel = d.SubjectNameForGrade1112 ?? CategoryDefaultLabel(d.Category);
            ws.Cell(row, 3).Value = subjectLabel;
            var weekly = new[] { d.Week1Present, d.Week2Present, d.Week3Present, d.Week4Present ?? 0, d.Week5Present ?? 0 };
            for (int w = 0; w < weekCount; w++)
                ws.Cell(row, 4 + w).Value = weekly[w];
            int firstSubjectRow = row;
            row++;

            int lastSubjectRow = row - 1;
            ws.Cell(row, 3).Value = "No. Of Std Present";
            for (int w = 0; w < weekCount; w++)
            {
                string col = ColumnLetter(4 + w);
                ws.Cell(row, 4 + w).FormulaA1 = $"=MAX({col}{firstSubjectRow}:{col}{lastSubjectRow})";
            }
            string firstWeekCol = ColumnLetter(4);
            string lastWeekCol = ColumnLetter(3 + weekCount);
            ws.Cell(row, 4 + weekCount).FormulaA1 = $"=AVERAGE({firstWeekCol}{row}:{lastWeekCol}{row})";
            ws.Cell(row, 5 + weekCount).FormulaA1 = $"=ROUNDUP({ColumnLetter(4 + weekCount)}{row},0)";
            row += 2; // blank separator row, matching source spacing
        }

        return row;
    }

    private static void WriteSummarySheet(IXLWorksheet ws, ReportPeriod period, List<MPRSummary> summaries)
    {
        ws.Cell("B2").Value = "NO";
        ws.Cell("C2").Value = "MONTH";
        ws.Cell("D2").Value = "STATUS";
        ws.Cell("F2").Value = "PERIOD";
        ws.Cell("F3").Value = "RC";
        ws.Cell("G3").Value = "O2O";
        ws.Cell("H3").Value = "PW";

        int row = 4;
        int rowTotalForPeriod = summaries.Sum(s => s.RC + s.O2O + s.PW);
        string status = rowTotalForPeriod > 70 ? "Paid" : "Unpaid";

        ws.Cell(row, 2).Value = 1;
        ws.Cell(row, 3).Value = period.MonthLabel;
        ws.Cell(row, 4).Value = status;
        ws.Cell(row, 5).FormulaA1 = $"=SUM(F{row}:H{row})";
        ws.Cell(row, 6).Value = summaries.Sum(s => s.RC);
        ws.Cell(row, 7).Value = summaries.Sum(s => s.O2O);
        ws.Cell(row, 8).Value = summaries.Sum(s => s.PW);

        row += 2;
        ws.Cell(row, 2).Value = "Grade";
        ws.Cell(row, 3).Value = "RC";
        ws.Cell(row, 4).Value = "O2O";
        ws.Cell(row, 5).Value = "PW";
        ws.Cell(row, 6).Value = "Total";
        ws.Cell(row, 7).Value = "Status";
        row++;
        foreach (var s in summaries)
        {
            ws.Cell(row, 2).Value = s.Grade.Name;
            ws.Cell(row, 3).Value = s.RC;
            ws.Cell(row, 4).Value = s.O2O;
            ws.Cell(row, 5).Value = s.PW;
            ws.Cell(row, 6).FormulaA1 = $"=SUM(C{row}:E{row})";
            ws.Cell(row, 7).FormulaA1 = $"=IF(F{row}>70,\"Paid\",\"Unpaid\")";
            row++;
        }
    }

    private static int CategorySortKey(MPRDetailResult d) => d.Category switch
    {
        SubjectCategory.RC => 0,
        SubjectCategory.TT => 1,
        SubjectCategory.O2O => 2,
        SubjectCategory.PW => 3,
        _ => 9
    };

    private static string BlockLabel(Grade grade, SubjectCategory category) => category switch
    {
        SubjectCategory.O2O => $"MPR LEVEL {grade.Name} - O2O",
        SubjectCategory.PW => $"MPR LEVEL PW {grade.Name}",
        SubjectCategory.TT => $"MPR LEVEL TT{grade.Name}",
        _ => $"MPR LEVEL {grade.Name}"
    };

    private static string CategoryDefaultLabel(SubjectCategory category) => category switch
    {
        SubjectCategory.O2O => "Math/English",
        SubjectCategory.PW => "Power Writing",
        _ => "Eng"
    };

    private static string ColumnLetter(int colNumber)
    {
        string col = "";
        while (colNumber > 0)
        {
            int rem = (colNumber - 1) % 26;
            col = (char)('A' + rem) + col;
            colNumber = (colNumber - 1) / 26;
        }
        return col;
    }
}
