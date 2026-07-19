using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MPR.Application.Interfaces;
using MPR.Domain.Entities;
using MPR.Domain.Enums;
using MPR.Infrastructure.Persistence;

namespace MPR.Web.Services.Chat;

/// <summary>
/// Runs a tool call the model requested against the real backend services (the same
/// AppDbContext, IMprCalculationService, IExcelExportService, IEmailService used by
/// the wizard API) and returns a JSON string result to feed back to the model.
/// Nothing here duplicates business logic - it's a thin adapter.
/// </summary>
public class MprToolExecutor
{
    private readonly AppDbContext _db;
    private readonly IMprCalculationService _calc;
    private readonly IExcelExportService _excel;
    private readonly IEmailService _email;
    private readonly int _currentUserId;

    public MprToolExecutor(AppDbContext db, IMprCalculationService calc, IExcelExportService excel, IEmailService email, int currentUserId)
    {
        _db = db;
        _calc = calc;
        _excel = excel;
        _email = email;
        _currentUserId = currentUserId;
    }

    public async Task<string> ExecuteAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        try
        {
            return toolName switch
            {
                "list_report_periods" => await ListReportPeriods(),
                "create_report_period" => await CreateReportPeriod(args),
                "list_uploaded_files" => await ListUploadedFiles(args),
                "get_extraction_review_summary" => await GetExtractionReviewSummary(args),
                "recalculate_mpr" => await RecalculateMpr(args, ct),
                "get_mpr_preview" => await GetMprPreview(args),
                "finalize_report" => await FinalizeReport(args),
                "get_excel_download_link" => GetExcelDownloadLink(args),
                "email_report" => await EmailReport(args, ct),
                "get_low_attendance_students" => await GetLowAttendanceStudents(args),
                _ => JsonSerializer.Serialize(new { error = $"Unknown tool '{toolName}'." })
            };
        }
        catch (Exception ex)
        {
            // Fed back to the model as a tool result so it can explain the failure to
            // the user in plain language, rather than the chat turn just dying.
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private async Task<string> ListReportPeriods()
    {
        var periods = await _db.ReportPeriods
            .OrderByDescending(p => p.FromDate)
            .Take(20)
            .Select(p => new { p.Id, p.MonthLabel, PeriodWeeks = (int)p.PeriodType, p.FromDate, p.ToDate, Status = p.Status.ToString() })
            .ToListAsync();
        return JsonSerializer.Serialize(periods);
    }

    private async Task<string> CreateReportPeriod(JsonElement args)
    {
        int weeks = args.GetProperty("periodWeeks").GetInt32();
        var fromDate = DateTime.Parse(args.GetProperty("fromDate").GetString()!);
        string monthLabel = args.GetProperty("monthLabel").GetString()!;

        var period = new ReportPeriod
        {
            PeriodType = (PeriodType)weeks,
            FromDate = fromDate,
            ToDate = fromDate.AddDays(weeks * 7 - 1),
            MonthLabel = monthLabel,
            Status = ReportStatus.Draft,
            CreatedByUserId = _currentUserId
        };
        _db.ReportPeriods.Add(period);
        await _db.SaveChangesAsync();

        foreach (var family in new[] { "RC", "TT", "PW_O2O" })
            for (int w = 1; w <= weeks; w++)
                _db.PeriodWeekDates.Add(new PeriodWeekDate
                {
                    ReportPeriodId = period.Id,
                    WeekNumber = w,
                    DateFamily = family,
                    WeekEndingDate = fromDate.AddDays(w * 7 - 1)
                });
        await _db.SaveChangesAsync();

        return JsonSerializer.Serialize(new
        {
            reportPeriodId = period.Id,
            message = $"Created a {weeks}-week report period from {fromDate:yyyy-MM-dd} to {period.ToDate:yyyy-MM-dd}. Upload PDFs for this period using the upload button, tagged with the grade (and subject for Grades 11/12)."
        });
    }

    private async Task<string> ListUploadedFiles(JsonElement args)
    {
        int periodId = args.GetProperty("reportPeriodId").GetInt32();
        var files = await _db.UploadedFiles
            .Include(f => f.Grade)
            .Where(f => f.ReportPeriodId == periodId)
            .Select(f => new { f.Id, f.FileName, Grade = f.Grade.Name, Status = f.Status.ToString(), f.ProcessingNotes })
            .ToListAsync();
        return JsonSerializer.Serialize(files);
    }

    private async Task<string> GetExtractionReviewSummary(JsonElement args)
    {
        int periodId = args.GetProperty("reportPeriodId").GetInt32();
        var records = await _db.AttendanceRecords.Where(a => a.ReportPeriodId == periodId).ToListAsync();

        var summary = new
        {
            totalRows = records.Count,
            lowConfidenceRows = records.Count(r => (r.OcrConfidence ?? 1.0) < 0.75),
            manuallyCorrectedRows = records.Count(r => r.IsManuallyOverridden),
            readyForRecalculation = records.Count(r => (r.OcrConfidence ?? 1.0) >= 0.75 || r.IsManuallyOverridden) == records.Count
        };
        return JsonSerializer.Serialize(summary);
    }

    private async Task<string> RecalculateMpr(JsonElement args, CancellationToken ct)
    {
        int periodId = args.GetProperty("reportPeriodId").GetInt32();
        await _calc.RecalculateDetailAsync(periodId, ct);
        await _calc.RecalculateSummaryAsync(periodId, ct);
        return JsonSerializer.Serialize(new { message = "Recalculated. Use get_mpr_preview to see the updated numbers." });
    }

    private async Task<string> GetMprPreview(JsonElement args)
    {
        int periodId = args.GetProperty("reportPeriodId").GetInt32();
        var summary = await _db.MPRSummaries
            .Include(s => s.Grade)
            .Where(s => s.ReportPeriodId == periodId)
            .Select(s => new { Grade = s.Grade.Name, s.RC, s.O2O, s.PW, Total = s.RC + s.O2O + s.PW, Status = (s.RC + s.O2O + s.PW) > 70 ? "Paid" : "Unpaid" })
            .ToListAsync();
        return JsonSerializer.Serialize(summary);
    }

    private async Task<string> FinalizeReport(JsonElement args)
    {
        int periodId = args.GetProperty("reportPeriodId").GetInt32();
        var period = await _db.ReportPeriods.FindAsync(periodId);
        if (period is null) return JsonSerializer.Serialize(new { error = "Report period not found." });

        period.Status = ReportStatus.Finalized;
        period.FinalizedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return JsonSerializer.Serialize(new { message = "Report finalized and locked." });
    }

    private static string GetExcelDownloadLink(JsonElement args)
    {
        int periodId = args.GetProperty("reportPeriodId").GetInt32();
        return JsonSerializer.Serialize(new { downloadUrl = $"/api/export/periods/{periodId}/excel" });
    }

    private async Task<string> EmailReport(JsonElement args, CancellationToken ct)
    {
        int periodId = args.GetProperty("reportPeriodId").GetInt32();
        var recipients = args.GetProperty("recipients").EnumerateArray().Select(r => r.GetString()!).ToList();
        string subject = args.GetProperty("subject").GetString()!;

        var stream = await _excel.ExportAsync(periodId, ct);
        var ok = await _email.SendReportAsync(periodId, recipients, subject, null, stream, $"MPR_{periodId}.xlsx", ct);
        return JsonSerializer.Serialize(new { success = ok });
    }

    private async Task<string> GetLowAttendanceStudents(JsonElement args)
    {
        int periodId = args.GetProperty("reportPeriodId").GetInt32();
        double threshold = args.TryGetProperty("thresholdPercent", out var t) ? t.GetDouble() : 75;

        var period = await _db.ReportPeriods.FindAsync(periodId);
        int weekCount = period is null ? 4 : (int)period.PeriodType;

        var records = await _db.AttendanceRecords.Where(a => a.ReportPeriodId == periodId).ToListAsync();
        var flagged = records
            .GroupBy(r => r.StudentName)
            .Select(g =>
            {
                var weeks = g.SelectMany(r => new[] { r.Week1, r.Week2, r.Week3, r.Week4, r.Week5 })
                             .Take(weekCount).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                double pct = weeks.Count == 0 ? 0 : 100.0 * weeks.Sum() / weeks.Count;
                return new { student = g.Key, attendancePercent = Math.Round(pct, 1) };
            })
            .Where(x => x.attendancePercent < threshold)
            .OrderBy(x => x.attendancePercent)
            .ToList();

        return JsonSerializer.Serialize(flagged);
    }
}
