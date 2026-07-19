using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MPR.Infrastructure.Persistence;

namespace MPR.Web.Controllers;

/// <summary>
/// Read-only analytics endpoints. Each maps to one of the 20+ reports proposed
/// in the design; the wizard/detail/summary CRUD lives in ReportWizardController.
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsAnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;
    public ReportsAnalyticsController(AppDbContext db) => _db = db;

    // 2. MPR Summary (monthly rollup, Paid/Unpaid) across all historical periods
    [HttpGet("mpr-all")]
    public async Task<IActionResult> MprAll()
    {
        var data = await _db.MPRSummaries
            .Include(s => s.ReportPeriod)
            .Include(s => s.Grade)
            .GroupBy(s => new { s.ReportPeriod.MonthLabel, s.ReportPeriodId })
            .Select(g => new
            {
                Month = g.Key.MonthLabel,
                RC = g.Sum(x => x.RC),
                O2O = g.Sum(x => x.O2O),
                PW = g.Sum(x => x.PW),
                Total = g.Sum(x => x.RC + x.O2O + x.PW)
            })
            .OrderByDescending(x => x.Month)
            .ToListAsync();

        var withStatus = data.Select(d => new { d.Month, d.RC, d.O2O, d.PW, d.Total, Status = d.Total > 70 ? "Paid" : "Unpaid" });
        return Ok(withStatus);
    }

    // 3. Grade-wise attendance for a single period
    [HttpGet("periods/{id}/grade/{gradeId}")]
    public async Task<IActionResult> GradeReport(int id, int gradeId)
    {
        var rows = await _db.MPRDetailResults
            .Where(d => d.ReportPeriodId == id && d.GradeId == gradeId)
            .ToListAsync();
        return Ok(rows);
    }

    // 8. Year-over-year attendance trend per grade
    [HttpGet("trend/grade/{gradeId}")]
    public async Task<IActionResult> GradeTrend(int gradeId)
    {
        var data = await _db.MPRSummaries
            .Include(s => s.ReportPeriod)
            .Where(s => s.GradeId == gradeId)
            .OrderBy(s => s.ReportPeriod.FromDate)
            .Select(s => new { s.ReportPeriod.MonthLabel, s.RC, s.O2O, s.PW })
            .ToListAsync();
        return Ok(data);
    }

    // 9. Month-over-month Paid vs Unpaid ratio
    [HttpGet("trend/paid-ratio")]
    public async Task<IActionResult> PaidRatioTrend()
    {
        var periods = await _db.MPRSummaries
            .Include(s => s.ReportPeriod)
            .GroupBy(s => new { s.ReportPeriodId, s.ReportPeriod.MonthLabel })
            .Select(g => new { g.Key.MonthLabel, Total = g.Sum(x => x.RC + x.O2O + x.PW) })
            .ToListAsync();

        var byMonth = periods
            .GroupBy(p => p.MonthLabel)
            .Select(g => new
            {
                Month = g.Key,
                Paid = g.Count(p => p.Total > 70),
                Unpaid = g.Count(p => p.Total <= 70)
            })
            .OrderBy(x => x.Month);

        return Ok(byMonth);
    }

    // 11. Student attendance consistency (flags below threshold, e.g. <75%)
    [HttpGet("periods/{id}/low-attendance-students")]
    public async Task<IActionResult> LowAttendanceStudents(int id, [FromQuery] double thresholdPercent = 75)
    {
        var records = await _db.AttendanceRecords.Where(a => a.ReportPeriodId == id).ToListAsync();
        var period = await _db.ReportPeriods.FindAsync(id);
        int weekCount = period is null ? 4 : (int)period.PeriodType;

        var flagged = records
            .GroupBy(r => new { r.StudentName, r.GradeId })
            .Select(g =>
            {
                var weeks = g.SelectMany(r => new[] { r.Week1, r.Week2, r.Week3, r.Week4, r.Week5 })
                             .Take(weekCount).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                double pct = weeks.Count == 0 ? 0 : 100.0 * weeks.Sum() / weeks.Count;
                return new { g.Key.StudentName, g.Key.GradeId, AttendancePercent = pct };
            })
            .Where(x => x.AttendancePercent < thresholdPercent)
            .OrderBy(x => x.AttendancePercent);

        return Ok(flagged);
    }

    // 13. Late (L) frequency - requires raw mark retained; recommend storing the original
    // mark code (v/A/L) alongside the collapsed 0/1 value if this report is a priority
    // (current AttendanceRecord schema collapses L into 1 to match the "present" rule).
    // Left as a schema note rather than faked data.

    // 19. Data quality / extraction confidence report
    [HttpGet("periods/{id}/extraction-quality")]
    public async Task<IActionResult> ExtractionQuality(int id)
    {
        var records = await _db.AttendanceRecords.Where(a => a.ReportPeriodId == id).ToListAsync();
        var summary = new
        {
            TotalRows = records.Count,
            LowConfidenceRows = records.Count(r => (r.OcrConfidence ?? 1.0) < 0.75),
            ManuallyOverriddenRows = records.Count(r => r.IsManuallyOverridden),
            AverageConfidence = records.Where(r => r.OcrConfidence.HasValue).Select(r => r.OcrConfidence!.Value).DefaultIfEmpty(1.0).Average()
        };
        return Ok(summary);
    }

    // 20. Revenue threshold proximity (periods close to the 70-count boundary)
    [HttpGet("threshold-proximity")]
    public async Task<IActionResult> ThresholdProximity([FromQuery] int margin = 5)
    {
        var periods = await _db.MPRSummaries
            .Include(s => s.ReportPeriod)
            .GroupBy(s => new { s.ReportPeriodId, s.ReportPeriod.MonthLabel })
            .Select(g => new { g.Key.MonthLabel, Total = g.Sum(x => x.RC + x.O2O + x.PW) })
            .ToListAsync();

        var close = periods.Where(p => Math.Abs(p.Total - 70) <= margin)
            .Select(p => new { p.MonthLabel, p.Total, DistanceFromThreshold = p.Total - 70 });

        return Ok(close);
    }

    // 21. User activity / audit report
    [HttpGet("audit-log")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AuditLog([FromQuery] int take = 200)
    {
        var log = await _db.AuditLogEntries.OrderByDescending(a => a.Timestamp).Take(take).ToListAsync();
        return Ok(log);
    }

    // 22. Export/email delivery log
    [HttpGet("email-log")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> EmailLog([FromQuery] int take = 200)
    {
        var log = await _db.EmailLogEntries.OrderByDescending(a => a.SentAt).Take(take).ToListAsync();
        return Ok(log);
    }
}
